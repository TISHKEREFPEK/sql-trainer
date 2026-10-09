using System.Security.Cryptography;
using System.Text.Json;
using Classroom.Contracts;
using Classroom.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Storage;
public sealed class ElectronImport
{
    public static JsonElement Read(string path)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open();
        using var integrity = source.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        if ((string? )integrity.ExecuteScalar() != "ok")
            throw new InvalidDataException("База Electron повреждена.");
        using var command = source.CreateCommand();
        command.CommandText = "SELECT value FROM state WHERE id=1";
        var json = command.ExecuteScalar() as string ?? throw new InvalidDataException("Не найдены данные Electron.");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static ImportPreview Preview(string path, string id)
    {
        JsonElement state;
        try
        {
            state = Read(path);
            Validate(state);
        }
        catch (Exception e)when (e is SqliteException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Файл Electron повреждён или содержит некорректные поля. Импорт отменён.", e);
        }

        var students = state.GetProperty("students").EnumerateArray().ToArray();
        return new(id, SourceHash(path), students.Length, students.Sum(s => ObjectLength(s, "assigned")), students.Sum(s => ObjectLength(s, "snapshots")), ArrayLength(state, "attempts"), ObjectLength(state, "overrides"));
    }

    static string SourceHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static int ObjectLength(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) ? v.EnumerateObject().Count() : 0;
    static int ArrayLength(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) ? v.GetArrayLength() : 0;
    static string Raw(JsonElement obj, string key, string fallback) => obj.TryGetProperty(key, out var v) ? v.GetRawText() : fallback;
    static IEnumerable<JsonProperty> Properties(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) ? value.EnumerateObject() : Enumerable.Empty<JsonProperty>();
    public static void Validate(JsonElement state)
    {
        if (!state.TryGetProperty("students", out var students) || students.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Не найдены ученики Electron.");
        if (state.TryGetProperty("teacherHash", out var teacherHash) && teacherHash.ValueKind != JsonValueKind.Null && (teacherHash.ValueKind != JsonValueKind.String || !Passwords.ValidHash(teacherHash.GetString()!)))
            throw new InvalidDataException("Некорректный пароль преподавателя Electron.");
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>();
        foreach (var student in students.EnumerateArray())
        {
            var login = student.GetProperty("login").GetString()!;
            var id = student.GetProperty("id").GetString()!;
            if (string.IsNullOrWhiteSpace(login) || login.Length > 40 || !logins.Add(login) || string.IsNullOrWhiteSpace(id) || !ids.Add(id) || !Passwords.ValidHash(student.GetProperty("passwordHash").GetString()!))
                throw new InvalidDataException("Некорректный или повторяющийся профиль Electron.");
            foreach (var assignment in Properties(student, "assigned"))
            {
                var task = Catalog.Read(assignment.Value.GetProperty("task").GetRawText());
                Catalog.Validate(task);
                if (task.Id != assignment.Name)
                    throw new InvalidDataException("Идентификатор назначения не совпадает.");
            }

            foreach (var snapshot in Properties(student, "snapshots"))
                ValidateSnapshot(snapshot.Value.GetString()!);
            if (student.TryGetProperty("theme", out var theme))
                Themes.Validate(Wire.Read<ThemePreference>(theme.GetRawText()));
            if (student.TryGetProperty("themePresets", out var presets))
                Themes.ValidatePresets(Wire.Read<ThemePreset[]>(presets.GetRawText()));
            foreach (var hint in Properties(student, "hints"))
                if (hint.Value.ValueKind != JsonValueKind.Array || hint.Value.EnumerateArray().Any(t => !t.TryGetInt64(out _)))
                    throw new InvalidDataException("Некорректный журнал подсказок.");
        }

        foreach (var entry in Properties(state, "overrides"))
        {
            var task = Catalog.Read(entry.Value.GetRawText());
            Catalog.Validate(task);
            if (entry.Name != task.Id)
                throw new InvalidDataException("Идентификатор задания не совпадает.");
        }

        if (state.TryGetProperty("teacherTheme", out var tt))
            Themes.Validate(Wire.Read<ThemePreference>(tt.GetRawText()));
        if (state.TryGetProperty("teacherThemePresets", out var tp))
            Themes.ValidatePresets(Wire.Read<ThemePreset[]>(tp.GetRawText()));
    }

    public static void ValidateSnapshot(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < 100 || !bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            throw new InvalidDataException("Повреждён проектный снимок SQLite.");
        var tmp = Path.Combine(Path.GetTempPath(), "sql-import-" + Guid.NewGuid() + ".sqlite");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            if ((string? )cmd.ExecuteScalar() != "ok")
                throw new InvalidDataException("Повреждён проектный снимок SQLite.");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public static async Task<bool> Commit(ClassroomDb db, string path, string expectedHash, Backups backups)
    {
        var preview = Preview(path, "");
        if (preview.SourceHash != expectedHash)
            throw new InvalidDataException("Файл изменился после предпросмотра.");
        if (await db.Imports.AnyAsync(i => i.Hash == expectedHash))
            return false;
        var state = Read(path);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        backups.Create("before-import");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var groupId = "electron-" + expectedHash[..12];
        db.Groups.Add(new() { Id = groupId, Name = "Импорт Electron" });
        var tasks = await db.Tasks.ToDictionaryAsync(t => t.Id);
        foreach (var entry in Properties(state, "overrides"))
        {
            if (!tasks.TryGetValue(entry.Name, out var row))
            {
                row = new()
                {
                    Id = entry.Name
                };
                db.Tasks.Add(row);
                tasks.Add(row.Id, row);
            }

            row.Json = entry.Value.GetRawText();
            row.Revision++;
            row.Deleted = false;
        }

        foreach (var policy in Properties(state, "policies"))
            if (tasks.TryGetValue(policy.Name, out var row))
                row.Restricted = policy.Value.GetBoolean();
        var existing = await db.Profiles.ToListAsync();
        if (!existing.Any(p => p.Role == "teacher") && state.TryGetProperty("teacherHash", out var oldTeacher) && oldTeacher.ValueKind == JsonValueKind.String && Passwords.ValidHash(oldTeacher.GetString()!))
        {
            var initialTeacher = new ProfileEntity
            {
                Id = "teacher",
                Login = "teacher",
                Role = "teacher",
                PasswordHash = oldTeacher.GetString()!
            };
            db.Profiles.Add(initialTeacher);
            existing.Add(initialTeacher);
        }

        foreach (var student in state.GetProperty("students").EnumerateArray())
        {
            var id = student.GetProperty("id").GetString()!;
            var login = student.GetProperty("login").GetString()!;
            if (existing.Any(p => p.Id == id || p.Login.Equals(login, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Профиль «{login}» уже существует. Импорт отменён.");
            var profile = new ProfileEntity
            {
                Id = id,
                Login = login.ToLowerInvariant(),
                PasswordHash = student.GetProperty("passwordHash").GetString()!,
                GroupId = groupId,
                Slot = student.TryGetProperty("slot", out var slot) ? slot.GetInt32() : existing.Count,
                LockedUntil = student.TryGetProperty("lockedUntil", out var locked) ? locked.GetInt64() : 0,
                ThemeJson = Raw(student, "theme", "{\"preset\":\"light\"}"),
                PresetsJson = Raw(student, "themePresets", "[]")
            };
            db.Profiles.Add(profile);
            existing.Add(profile);
            var completed = student.TryGetProperty("completed", out var completedValue) ? completedValue.EnumerateArray().Select(t => t.GetString()!).ToHashSet() : [];
            var assigned = new Dictionary<string, AssignmentEntity>();
            foreach (var entry in Properties(student, "assigned"))
            {
                var definition = Catalog.Read(entry.Value.GetProperty("task").GetRawText());
                var sequence = Array.FindIndex(Catalog.Data.Tasks.Where(t => t.Project == definition.Project).ToArray(), t => t.Id == entry.Name);
                var row = new AssignmentEntity
                {
                    StudentId = id,
                    TaskId = entry.Name,
                    Json = entry.Value.GetProperty("task").GetRawText(),
                    Revision = entry.Value.TryGetProperty("revision", out var rev) ? rev.GetInt32() : 1,
                    ProjectOrder = Math.Max(0, sequence),
                    CompletedAt = completed.Contains(entry.Name) ? now : null
                };
                db.Assignments.Add(row);
                assigned.Add(row.TaskId, row);
            }

            foreach (var taskId in completed.Where(t => !assigned.ContainsKey(t)))
            {
                var task = tasks.TryGetValue(taskId, out var stored) ? Catalog.Read(stored.Json) : Catalog.Data.Tasks.FirstOrDefault(t => t.Id == taskId) ?? throw new InvalidDataException("Не найдено завершённое задание " + taskId);
                var row = new AssignmentEntity
                {
                    StudentId = id,
                    TaskId = taskId,
                    Json = Catalog.Write(Variants.Assign(task, profile.Slot, Variants.Create(task))),
                    CompletedAt = now
                };
                db.Assignments.Add(row);
                assigned.Add(taskId, row);
            }

            foreach (var hint in Properties(student, "hints"))
            {
                if (!assigned.TryGetValue(hint.Name, out var row))
                    throw new InvalidDataException("Подсказка без назначения.");
                if (hint.Value.GetArrayLength() > Catalog.Read(row.Json).Hints.Length)
                    throw new InvalidDataException("Слишком много открытых подсказок.");
                row.HintsJson = hint.Value.GetRawText();
            }

            foreach (var snapshot in Properties(student, "snapshots"))
                db.Snapshots.Add(new() { StudentId = id, Project = snapshot.Name, Base64 = snapshot.Value.GetString()! });
        }

        if (state.TryGetProperty("attempts", out var attempts))
            foreach (var attempt in attempts.EnumerateArray())
            {
                var login = attempt.GetProperty("student").GetString();
                var student = existing.FirstOrDefault(p => p.Role == "student" && p.Login.Equals(login, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("Попытка неизвестного ученика.");
                db.Attempts.Add(new() { Id = Guid.NewGuid().ToString("N"), StudentId = student.Id, TaskId = attempt.GetProperty("taskId").GetString()!, Kind = attempt.GetProperty("kind").GetString()!, At = attempt.GetProperty("at").GetInt64() });
                var taskId = attempt.GetProperty("taskId").GetString()!;
                var kind = attempt.GetProperty("kind").GetString()!;
                var at = attempt.GetProperty("at").GetInt64();
                var assignment = db.Assignments.Local.FirstOrDefault(a => a.StudentId == student.Id && a.TaskId == taskId);
                if (assignment is not null && kind != "clipboard")
                {
                    assignment.Checks++;
                    if (kind != "correct")
                        assignment.Errors++;
                    if (kind == "correct" && assignment.CompletedAt is { } completed)
                        assignment.CompletedAt = Math.Min(completed, at);
                }
            }

        var teacher = existing.SingleOrDefault(p => p.Role == "teacher");
        if (teacher is not null)
        {
            teacher.ThemeJson = Raw(state, "teacherTheme", teacher.ThemeJson);
            teacher.PresetsJson = Raw(state, "teacherThemePresets", teacher.PresetsJson);
        }

        db.Imports.Add(new() { Hash = expectedHash, At = now });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }
}

public static class Themes
{
    public static readonly string[] Names = ["light", "mist", "paper", "dark", "onyx", "midnight"];
    public static void Validate(ThemePreference theme)
    {
        if (!Names.Contains(theme.Preset) || theme.Colors?.Any(p => !new[] { "background", "surface", "accent", "editor" }.Contains(p.Key) || !System.Text.RegularExpressions.Regex.IsMatch(p.Value, "^#[0-9a-fA-F]{6}$")) == true)
            throw new InvalidDataException("Некорректная тема или цвет.");
    }

    public static void ValidatePresets(ThemePreset[] presets)
    {
        if (presets.Length > 30 || presets.Select(p => p.Id).Distinct().Count() != presets.Length || presets.Select(p => p.Name.ToLowerInvariant()).Distinct().Count() != presets.Length)
            throw new InvalidDataException("Некорректные пресеты.");
        foreach (var preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 60)
                throw new InvalidDataException("Некорректное название темы.");
            Validate(preset.Theme);
        }
    }
}
