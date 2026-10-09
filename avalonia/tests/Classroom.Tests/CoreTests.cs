using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Classroom.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Classroom.Tests;
public sealed class CoreTests
{
    [Fact]
    public void AllVariantsMatchTypeScriptAndAllSolutionsPass()
    {
        var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "variant-fixtures.json"))).RootElement;
        Assert.Equal(120, Catalog.Data.Tasks.Length);
        var count = 0;
        foreach (var baseTask in Catalog.Data.Tasks)
        {
            var fixture = fixtures.EnumerateArray().Single(f => f.GetProperty("id").GetString() == baseTask.Id);
            var alternatives = Variants.Create(baseTask);
            Assert.Equal(fixture.GetProperty("alternatives").GetArrayLength(), alternatives.Length);
            foreach (var index in Enumerable.Range(0, 4))
                Assert.Equal(fixture.GetProperty("assigned")[index].GetProperty("variantIndex").GetInt32(), Variants.Assign(baseTask, index, alternatives).VariantIndex);
            for (var i = 0; i < alternatives.Length; i++)
            {
                var expected = Catalog.Read(fixture.GetProperty("alternatives")[i].GetRawText());
                var actual = alternatives[i];
                Assert.Equal(expected.Prompt, actual.Prompt);
                Assert.Equal(expected.Title, actual.Title);
                Assert.Equal(expected.Concept, actual.Concept);
                Assert.Equal(expected.Solution, actual.Solution);
                Assert.Equal(expected.Example, actual.Example);
                Assert.Equal(expected.Hints, actual.Hints);
                Assert.Equal(fixture.GetProperty("alternatives")[i].GetProperty("seedHash").GetString(), Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Variants.Sql(actual.Seed, actual.TableMap)))));
            }

            foreach (var task in new[]
            {
                baseTask
            }.Concat(alternatives))
            {
                if (task.Project is not null)
                    continue;
                var result = SqlEngine.Evaluate(new(task, null, task.Solution, true));
                Assert.True(result.Result.Correct, $"{task.Id} v{task.VariantIndex ?? 1}: {result.Result.Error}");
                count++;
            }
        }

        Assert.True(count > 400);
        foreach (var variant in Enumerable.Range(1, 4))
        {
            var snapshots = new Dictionary<string, string>();
            foreach (var source in Catalog.Data.Tasks.Where(t => t.Project is not null))
            {
                var task = (variant == 1 ? source : Variants.Create(source)[variant - 2])with
                {
                    VariantIndex = variant
                };
                var result = SqlEngine.Evaluate(new(task, snapshots.GetValueOrDefault(task.Project!), task.Solution, true));
                Assert.True(result.Result.Correct, $"{task.Id} v{variant}: {result.Result.Error}");
                if (result.Snapshot is not null)
                    snapshots[task.Project!] = result.Snapshot;
            }

            Assert.Equal(3, snapshots.Count);
        }
    }

    [Fact]
    public void RewritingPreservesLiteralsAndComments()
    {
        var sql = "SELECT * FROM books WHERE title='books'; -- books\n/* books */ SELECT \"books\".id FROM [books]";
        Assert.Equal("SELECT * FROM books_v2 WHERE title='books'; -- books\n/* books */ SELECT \"books_v2\".id FROM [books_v2]", Variants.Sql(sql, new() { ["books"] = "books_v2" }));
    }

    [Fact]
    public void QueriesCannotWriteOrAccessFilesAndErrorsAreOriginal()
    {
        var task = Catalog.Data.Tasks[0];
        Assert.Contains("syntax error", SqlEngine.Evaluate(new(task, null, "SELECT * FRM students;", true)).Result.Error);
        Assert.Contains("no such column", SqlEngine.Evaluate(new(task, null, "SELECT missing FROM students", true)).Result.Error);
        Assert.False(SqlEngine.Evaluate(new(task, null, "SELECT name FROM students", true)).Result.Correct);
        Assert.NotNull(SqlEngine.Evaluate(new(task, null, "WITH x AS(SELECT 1) DELETE FROM students", true)).Result.Error);
        var stateTask = Catalog.Data.Tasks.First(t => t.Id == "create");
        foreach (var sql in new[]
        {
            "ATTACH DATABASE '/tmp/other.sqlite' AS secret",
            "PRAGMA writable_schema=ON",
            "SELECT load_extension('x')",
            "SELECT readfile('/etc/passwd')",
            "CREATE VIRTUAL TABLE x USING fts5(t)",
            "VACUUM INTO '/tmp/leak.sqlite'"
        }

        )
            Assert.NotNull(SqlEngine.Evaluate(new(stateTask, null, sql, false)).Result.Error);
    }

    [Fact]
    public void QueryComparisonPreservesMultiplicityAndOrder()
    {
        var task = Catalog.Data.Tasks[0] with
        {
            Seed = "",
            Solution = "SELECT 1 AS value UNION ALL SELECT 1 AS value",
            Ordered = false
        };
        Assert.False(SqlEngine.Evaluate(new(task, null, "SELECT 1 AS value", true)).Result.Correct);
        task = task with
        {
            Solution = "SELECT 1 AS value UNION ALL SELECT 2 AS value",
            Ordered = true
        };
        Assert.False(SqlEngine.Evaluate(new(task, null, "SELECT 2 AS value UNION ALL SELECT 1 AS value", true)).Result.Correct);
        Assert.True(SqlEngine.Evaluate(new(task with { Ordered = false }, null, "SELECT 2 AS value UNION ALL SELECT 1 AS value", true)).Result.Correct);
    }

    [Fact]
    public void LegacyNodePasswordsRemainValid()
    {
        var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "password-fixture.json"))).RootElement;
        Assert.True(Passwords.Verify(fixture.GetProperty("password").GetString()!, fixture.GetProperty("hash").GetString()!));
        Assert.False(Passwords.Verify("wrong", fixture.GetProperty("hash").GetString()!));
        Assert.False(Passwords.Verify("x", "invalid"));
    }

    [Fact]
    public void PinningRejectsAnotherCertificate()
    {
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=classroom", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(ClassroomApi.VerifyCertificate(cert, Convert.ToHexString(SHA256.HashData(cert.RawData))));
        Assert.False(ClassroomApi.VerifyCertificate(cert, new string ('0', 64)));
        Assert.Throws<InvalidDataException>(() => new ClassroomApi(new("http://127.0.0.1:1", new string ('0', 64))));
    }

    [Fact]
    public async Task DamagedImportRollsBackEverything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sql-bad-import-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "new.sqlite");
        var source = Path.Combine(dir, "electron.sqlite");
        try
        {
            var task = Catalog.Data.Tasks[0];
            var state = new
            {
                teacherHash = (string? )null,
                students = new[]
                {
                    new
                    {
                        id = "bad",
                        login = "bad",
                        passwordHash = Passwords.Hash("test-password"),
                        slot = 0,
                        assigned = new Dictionary<string, object>
                        {
                            [task.Id] = new
                            {
                                task,
                                revision = 1
                            }
                        },
                        completed = Array.Empty<string>(),
                        snapshots = new Dictionary<string, string>(),
                        hints = new Dictionary<string, long[]>
                        {
                            [task.Id] = [1, 2, 3, 4, 5, 6, 7]
                        }
                    }
                },
                attempts = Array.Empty<object>(),
                overrides = new Dictionary<string, object>(),
                policies = new Dictionary<string, bool>()
            };
            using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString()))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "CREATE TABLE state(id INTEGER PRIMARY KEY,value TEXT); INSERT INTO state VALUES(1,@json)";
                cmd.Parameters.AddWithValue("@json", Wire.Write(state));
                cmd.ExecuteNonQuery();
            }

            await using (var db = new ClassroomDb(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite("Data Source=" + target).Options))
            {
                await db.Database.MigrateAsync();
                db.Tasks.Add(new() { Id = task.Id, Json = Catalog.Write(task) });
                await db.SaveChangesAsync();
                var preview = ElectronImport.Preview(source, "bad");
                await Assert.ThrowsAsync<InvalidDataException>(() => ElectronImport.Commit(db, source, preview.SourceHash, new Backups(target)));
            }

            await using var verify = new ClassroomDb(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite("Data Source=" + target).Options);
            Assert.Empty(await verify.Profiles.ToArrayAsync());
            Assert.Empty(await verify.Assignments.ToArrayAsync());
            Assert.Empty(await verify.Groups.ToArrayAsync());
            Assert.Empty(await verify.Imports.ToArrayAsync());
            File.WriteAllText(source, "damaged");
            Assert.Throws<InvalidDataException>(() => ElectronImport.Preview(source, "bad"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ImportIsAtomicIdempotentAndPreservesProfileData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sql-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "new.sqlite");
        var source = Path.Combine(dir, "electron.sqlite");
        try
        {
            var task = Catalog.Data.Tasks[0];
            var hash = Passwords.Hash("student-test-password");
            var state = new
            {
                teacherHash = Passwords.Hash("teacher-old-password"),
                students = new[]
                {
                    new
                    {
                        id = "test-student",
                        login = "test-one",
                        passwordHash = hash,
                        slot = 0,
                        completed = new[]
                        {
                            task.Id
                        },
                        assigned = new Dictionary<string, object>
                        {
                            [task.Id] = new
                            {
                                task,
                                revision = 2
                            }
                        },
                        snapshots = new Dictionary<string, string>(),
                        hints = new Dictionary<string, long[]>
                        {
                            [task.Id] = [12345]
                        },
                        theme = new ThemePreference("onyx"),
                        themePresets = new[]
                        {
                            new ThemePreset("preset-one", "Тестовая", new("paper"))
                        }
                    }
                },
                overrides = new Dictionary<string, object>(),
                policies = new Dictionary<string, bool>(),
                attempts = new[]
                {
                    new
                    {
                        student = "test-one",
                        taskId = task.Id,
                        kind = "correct",
                        at = 12345L
                    }
                }
            };
            using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString()))
            {
                con.Open();
                using var command = con.CreateCommand();
                command.CommandText = "CREATE TABLE state(id INTEGER PRIMARY KEY,value TEXT); INSERT INTO state VALUES(1,@json)";
                command.Parameters.AddWithValue("@json", Wire.Write(state));
                command.ExecuteNonQuery();
            }

            var originalBytes = File.ReadAllBytes(source);
            var preview = ElectronImport.Preview(source, "preview");
            await using var db = new ClassroomDb(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite("Data Source=" + target).Options);
            await db.Database.MigrateAsync();
            db.Tasks.Add(new() { Id = task.Id, Json = Catalog.Write(task) });
            db.Profiles.Add(new() { Id = "teacher", Login = "teacher", Role = "teacher", PasswordHash = Passwords.Hash("new-teacher-password") });
            await db.SaveChangesAsync();
            var backups = new Backups(target);
            Assert.True(await ElectronImport.Commit(db, source, preview.SourceHash, backups));
            Assert.False(await ElectronImport.Commit(db, source, preview.SourceHash, backups));
            var imported = await db.Profiles.FindAsync("test-student");
            Assert.True(Passwords.Verify("student-test-password", imported!.PasswordHash));
            Assert.Equal("onyx", Wire.Read<ThemePreference>(imported.ThemeJson).Preset);
            var assignment = await db.Assignments.FindAsync("test-student", task.Id);
            Assert.Equal(12345, assignment!.CompletedAt);
            Assert.Equal(1, assignment.Checks);
            Assert.Equal(new long[] { 12345 }, Wire.Read<long[]>(assignment.HintsJson));
            Assert.Single(await db.Attempts.ToListAsync());
            Assert.Equal(originalBytes, File.ReadAllBytes(source));
            Assert.NotEmpty(backups.List());
            backups.Restore(backups.List()[0].Name);
            await using var restored = new ClassroomDb(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite("Data Source=" + target).Options);
            Assert.False(await restored.Profiles.AnyAsync(p => p.Id == "test-student"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
