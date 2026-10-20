using System.Security.Cryptography;
using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

// Explicit CLI operation, allowed only in a fresh database. Never runs on normal startup.
public static class DemoData
{
    public static async Task Create(ServerState state, string directory)
    {
        await using var db = state.Db();
        if (await db.Profiles.AnyAsync() || await db.Assignments.AnyAsync())
            throw new InvalidDataException("Демонстрационные данные можно создать только в новой пустой базе.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var teacherPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(18));
        var studentPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(18));
        var hash = Passwords.Hash(studentPassword);
        db.Profiles.Add(new() { Id = "teacher", Login = "teacher", Role = "teacher", PasswordHash = Passwords.Hash(teacherPassword) });
        var groups = new[] { "Демо · ИСП-31", "Демо · ИСП-32", "Демо · ИСП-33", "Демо · Пустая группа" };
        for (var i = 0; i < groups.Length; i++) db.Groups.Add(new() { Id = "demo-group-" + i, Name = groups[i] });
        await db.SaveChangesAsync();
        var tasks = Catalog.Data.Tasks.Where(t => t.Project is null).Take(85).ToArray();
        var now = ServerState.Now;
        for (var i = 0; i < 20; i++)
        {
            var p = new ProfileEntity { Id = $"demo-student-{i + 1:D2}", Login = $"demo-{i + 1:D2}", Slot = i, PasswordHash = hash, GroupId = i < 18 ? "demo-group-" + i / 6 : null };
            db.Profiles.Add(p); await db.SaveChangesAsync();
            var count = i == 5 ? 0 : new[] { 12, 28, 50, 70, 80, 0 }[i % 6];
            foreach (var source in tasks.Take(Math.Min(tasks.Length, count + (count == 0 ? 0 : 4))))
            {
                var index = Array.IndexOf(tasks, source);
                var a = await state.Assignment(db, p, source.Id);
                var task = Catalog.Read(a.Json);
                var complete = index < count && !(index % 13 == 9 && i % 3 == 0);
                var start = now - (14L - index % 14) * 86400000 + i * 110000 + index * 9000;
                var duration = 180 + i * 27 + index * 13;
                a.StartedAt = start;
                a.LastActivityAt = start + duration * 1000L;
                a.ActiveSeconds = Math.Min(duration, 90 + i * 9 + index * 7);
                var errorCount = index % 13 == 9 ? 3 + i % 5 : (i + index) % 3;
                a.Errors = errorCount; a.Checks = errorCount + (complete ? 1 : 0);
                var hintCount = Math.Min(task.Hints.Length, errorCount > 2 ? 2 : errorCount > 0 ? 1 : 0);
                a.HintsJson = Wire.Write(Enumerable.Range(0, hintCount).Select(h => start + (h + 1) * 20000L).ToArray());
                a.Draft = complete ? task.Solution : "SELECT 0 AS wrong_answer;";
                if (complete) { a.CompletedAt = a.LastActivityAt; a.AcceptedCode = task.Solution; a.ActiveSecondsAtCompletion = a.ActiveSeconds; }
                for (var j = 0; j < errorCount; j++)
                    db.Attempts.Add(new() { Id = Guid.NewGuid().ToString("N"), StudentId = p.Id, TaskId = task.Id, Action = "check", Code = j == 0 ? "SELEC 1;" : "SELECT 0 AS wrong_answer;", Kind = j == 0 ? "near \"SELEC\": syntax error" : "different", Explanation = j == 0 ? "Проверьте написание ключевого слова SELECT." : "Полученный результат отличается от ожидаемого.", At = start + (j + 1) * 30000L });
                if (complete) db.Attempts.Add(new() { Id = Guid.NewGuid().ToString("N"), StudentId = p.Id, TaskId = task.Id, Action = "check", Code = task.Solution, Kind = "correct", At = a.CompletedAt!.Value });
                if (i == 0 && index == 0)
                {
                    a.Draft = "SELECT name FROM students; -- черновик после решения";
                    for (var run = 0; run < 110; run++)
                        db.Attempts.Add(new() { Id = Guid.NewGuid().ToString("N"), StudentId = p.Id, TaskId = task.Id, Action = "run", Code = $"SELECT {run};", Kind = "executed", At = start + (duration + run + 1) * 1000L });
                    a.LastActivityAt = start + (duration + 110) * 1000L;
                }
                if ((index + i) % 8 == 0)
                    db.Attempts.Add(new() { Id = Guid.NewGuid().ToString("N"), StudentId = p.Id, TaskId = task.Id, Action = "run", Code = "SELECT 1;", Kind = "executed", At = start + 10000 });
                if (index % 11 == 0 && i % 3 == 0)
                {
                    foreach (var action in new[] { "copy", "paste", "capture" })
                        db.Attempts.Add(new() { Id = Guid.NewGuid().ToString("N"), StudentId = p.Id, TaskId = task.Id, Action = action, Kind = action == "capture" ? "capture" : "clipboard", At = start + 12000 + Array.IndexOf(new[] { "copy", "paste", "capture" }, action) * 1000 });
                    (await db.Tasks.FindAsync(task.Id))!.Restricted = true;
                }
                // A retained legacy example demonstrates honestly missing timestamps/SQL.
                if (i == 19 && index == 0)
                {
                    a.StartedAt = null; a.AcceptedCode = null; a.ActiveSecondsAtCompletion = null; a.TrackingVersion = 0;
                    foreach (var e in db.Attempts.Local.Where(e => e.StudentId == p.Id && e.TaskId == task.Id && e.Action == "check")) { e.Code = null; e.Action = null; }
                }
                await db.SaveChangesAsync();
            }
        }
        await transaction.CommitAsync();
        // Empty/new learners, a full task history and different variants are all visible.
        await File.WriteAllTextAsync(Path.Combine(directory, "demo-access.json"), Wire.Write(new { teacher = new { login = "teacher", password = teacherPassword }, student = new { login = "demo-01", password = studentPassword }, connection = Wire.Read<ConnectionSettings>(await File.ReadAllTextAsync(Path.Combine(directory, "connection.json"))) }));
        await File.WriteAllTextAsync(Path.Combine(directory, "DEMO.txt"), "Синтетические данные для просмотра статистики. Это не результаты реальных учеников.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(directory, "demo-access.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine("Created synthetic classroom data. Local access details saved privately in demo-access.json.");
    }
}
