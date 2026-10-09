using System.Text.Json;
using System.Text.RegularExpressions;
using Classroom.Contracts;
using Classroom.Domain;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Classroom.Worker;
public record WorkRequest(TaskDefinition Task, string? Snapshot, string Code, bool Check);
public record WorkResult(ExecutionDto Result, string? Snapshot);
public static class SqlEngine
{
    static readonly SqlResult Empty = new([], []);
    static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    static void Run(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static SqliteConnection Make(TaskDefinition task, string? snapshot)
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        Run(db, "PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=OFF;");
        Guard(db, false, true);
        try
        {
            if (snapshot is not null)
            {
                var path = Path.Combine(Path.GetTempPath(), "sql-worker-" + Guid.NewGuid() + ".sqlite");
                try
                {
                    File.WriteAllBytes(path, Convert.FromBase64String(snapshot));
                    using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    source.Open();
                    source.BackupDatabase(db);
                }
                finally
                {
                    File.Delete(path);
                }
            }
            else
            {
                Run(db, Variants.Sql(task.Seed, task.TableMap));
                if (task.DataVariant is { } variant)
                {
                    var factor = 1 + (variant - 1) * .15;
                    foreach (var row in Query(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").Values)
                    {
                        var name = row[0].GetString()!;
                        foreach (var column in Query(db, $"PRAGMA table_info({Quote(name)})").Values)
                        {
                            var field = column[1].GetString()!;
                            if (!new[]
                            {
                                "age",
                                "amount",
                                "price",
                                "daily_rate",
                                "quantity",
                                "prize",
                                "debt",
                                "paid_amount"
                            }.Contains(field) || !Regex.IsMatch(column[2].GetString()!, "INT|REAL|NUM|DEC|FLOAT|DOUBLE", RegexOptions.IgnoreCase))
                                continue;
                            using var command = db.CreateCommand();
                            command.CommandText = $"UPDATE {Quote(name)} SET {Quote(field)}=ROUND({Quote(field)}*@factor) WHERE {Quote(field)} IS NOT NULL";
                            command.Parameters.AddWithValue("@factor", factor);
                            command.ExecuteNonQuery();
                        }
                    }
                }
            }

            Run(db, "PRAGMA foreign_keys=ON;");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    static void Guard(SqliteConnection db, bool queryOnly, bool seed = false)
    {
        raw.sqlite3_enable_load_extension(db.Handle, 0);
        raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_LENGTH, 8 * 1024 * 1024);
        raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_SQL_LENGTH, 512000);
        raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_ATTACHED, 0);
        raw.sqlite3_set_authorizer(db.Handle, (delegate_authorizer)((_, action, p0, p1, _, _) =>
        {
            var name = p0.utf8_to_string() ?? "";
            var other = p1.utf8_to_string() ?? "";
            if (action == raw.SQLITE_ATTACH || action == raw.SQLITE_DETACH || action == raw.SQLITE_CREATE_VTABLE || action == raw.SQLITE_DROP_VTABLE)
                return raw.SQLITE_DENY;
            if (action == raw.SQLITE_FUNCTION && new[]
            {
                "load_extension",
                "readfile",
                "writefile",
                "edit"
            }.Contains(other.ToLowerInvariant()))
                return raw.SQLITE_DENY;
            if (action == raw.SQLITE_PRAGMA && !new[]
            {
                "table_info",
                "index_list",
                "index_xinfo",
                "foreign_key_list",
                "foreign_keys"
            }.Contains(name.ToLowerInvariant()))
                return raw.SQLITE_DENY;
            if (action == raw.SQLITE_PRAGMA && name.Equals("foreign_keys", StringComparison.OrdinalIgnoreCase) && other.Length > 0 && !seed)
                return raw.SQLITE_DENY;
            if (queryOnly && action is >= 1 and <= 18 && action != raw.SQLITE_READ)
                return raw.SQLITE_DENY;
            if (queryOnly && action is raw.SQLITE_UPDATE or raw.SQLITE_ALTER_TABLE or raw.SQLITE_REINDEX or raw.SQLITE_ANALYZE)
                return raw.SQLITE_DENY;
            return raw.SQLITE_OK;
        }), null);
    }

    static SqlResult Query(SqliteConnection db, string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Empty;
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var result = Empty;
        do
        {
            if (reader.FieldCount == 0)
                continue;
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var rows = new List<JsonElement[]>();
            while (reader.Read())
            {
                if (rows.Count >= 50000)
                    throw new InvalidDataException("Результат превышает 50 000 строк. Уточните запрос.");
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => JsonSerializer.SerializeToElement(reader.IsDBNull(i) ? null : reader.GetValue(i), Wire.Json)).ToArray());
            }

            result = new(columns, rows.ToArray());
        }
        while (reader.NextResult());
        return result;
    }

    static string Normalize(SqlResult result, bool ordered)
    {
        var rows = result.Values.Select(r => Wire.Write(r));
        if (!ordered)
            rows = rows.Order(StringComparer.Ordinal);
        return Wire.Write(new { result.Columns, Rows = rows.ToArray() });
    }

    static string State(SqliteConnection db)
    {
        var tables = Query(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").Values.Select(row =>
        {
            var name = row[0].GetString()!;
            var q = Quote(name);
            return new
            {
                name,
                columns = Query(db, $"PRAGMA table_info({q})").Values,
                indexes = Query(db, $"PRAGMA index_list({q})").Values.Select(r => new { name = r[1], unique = r[2], columns = Query(db, $"PRAGMA index_xinfo({Quote(r[1].GetString()!)})").Values }).ToArray(),
                foreignKeys = Query(db, $"PRAGMA foreign_key_list({q})").Values,
                rows = Query(db, $"SELECT * FROM {q}").Values.Select(r => Wire.Write(r)).Order(StringComparer.Ordinal).ToArray()
            };
        }).ToArray();
        return Wire.Write(new { tables, objects = Query(db, "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE type IN ('view','trigger') ORDER BY type,name").Values });
    }

    static string Snapshot(SqliteConnection db)
    {
        var path = Path.Combine(Path.GetTempPath(), "sql-snapshot-" + Guid.NewGuid() + ".sqlite");
        try
        {
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                destination.Open();
                db.BackupDatabase(destination);
            }

            return Convert.ToBase64String(File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static WorkResult Evaluate(WorkRequest request)
    {
        try
        {
            var task = request.Task;
            var statements = Variants.WithoutComments(request.Code);
            using var actual = Make(task, request.Snapshot);
            using var expected = Make(task, request.Snapshot);
            Guard(actual, task.Mode != "state");
            Guard(expected, task.Mode != "state");
            if (!string.IsNullOrWhiteSpace(request.Code) && task.Mode != "state" && !Regex.IsMatch(statements, "^\\s*(SELECT|WITH)\\b", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Для этого задания нужен запрос SELECT или WITH.");
            var output = Query(actual, request.Code);
            var correct = false;
            if (request.Check)
            {
                var target = Query(expected, task.Solution);
                correct = task.Mode == "state" ? State(actual) == State(expected) : Normalize(output, task.Ordered) == Normalize(target, task.Ordered);
                if (task.Id == "transaction")
                    correct = correct && Regex.IsMatch(statements, "^\\s*BEGIN\\b", RegexOptions.IgnoreCase) && Regex.IsMatch(statements.Trim(), "\\bCOMMIT\\s*;?\\s*$", RegexOptions.IgnoreCase);
                if (task.Id == "constraint")
                {
                    var table = Quote(task.TableMap?.GetValueOrDefault("tickets") ?? "tickets");
                    bool Reject(string sql)
                    {
                        try
                        {
                            Run(actual, sql);
                            return false;
                        }
                        catch (SqliteException)
                        {
                            return true;
                        }
                    }

                    Run(actual, "SAVEPOINT constraint_probe");
                    try
                    {
                        Run(actual, $"INSERT INTO {table}(id,code,price) VALUES(99,'probe',10)");
                        correct = correct && Reject($"INSERT INTO {table} VALUES(100,'probe',10)") && Reject($"INSERT INTO {table} VALUES(101,'negative',-1)") && Reject($"INSERT INTO {table} VALUES(102,NULL,10)");
                    }
                    catch (SqliteException)
                    {
                        correct = false;
                    }
                    finally
                    {
                        Run(actual, "ROLLBACK TO constraint_probe; RELEASE constraint_probe");
                    }
                }
            }

            var tables = Query(actual, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").Values.Select(row =>
            {
                var name = row[0].GetString()!;
                var columns = Query(actual, $"PRAGMA table_info({Quote(name)})").Values.Select(c => new ColumnDefinition(c[1].GetString()!, c[2].GetString()!, c[3].GetInt32() != 0, c[5].GetInt32() != 0)).ToArray();
                var keys = Query(actual, $"PRAGMA foreign_key_list({Quote(name)})").Values.Select(k => new ForeignKeyDefinition(k[3].GetString()!, k[2].GetString()!, k[4].ValueKind == JsonValueKind.Null ? "primary key" : k[4].GetString()!)).ToArray();
                return new TablePreview(name, Query(actual, $"SELECT * FROM {Quote(name)} LIMIT 8"), columns, keys);
            }).ToArray();
            var snapshot = correct && task.Mode == "state" && task.Project is not null ? Snapshot(actual) : null;
            return new(new(correct, output with { Values = output.Values.Take(200).ToArray() }, tables, null, null), snapshot);
        }
        catch (Exception e)when (e is SqliteException or InvalidDataException or FormatException)
        {
            var error = e is SqliteException se ? Regex.Replace(se.Message, "^SQLite Error \\d+: '([\\s\\S]*)'\\.$", "$1") : e.Message;
            return new(new(false, Empty, [], error, Explain(error)), null);
        }
    }

    public static string Explain(string error) => error switch
    {
        var s when s.Contains("no such table") => "Проверьте имя таблицы и номер выданного варианта.",
        var s when s.Contains("no such column") => "Проверьте название столбца в структуре таблицы.",
        var s when s.Contains("syntax error") => "Проверьте ключевые слова, запятые и скобки.",
        var s when s.Contains("constraint") => "Запрос нарушает ограничение данных таблицы.",
        var s when s.Contains("authorized") => "Эта операция недоступна в учебной базе.",
        _ => "Проверьте запрос и структуру учебной базы."
    };
}
