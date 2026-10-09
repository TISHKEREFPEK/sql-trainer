using System.Collections.Concurrent;
using System.Security.Cryptography;
using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;
public sealed class HttpFault(int status, string message) : Exception(message)
{
    public int Status => status;
}

public record Session(string ProfileId, string Role, long Expires);
public sealed class ServerState(string databasePath, WorkerRunner worker)
{
    public readonly SemaphoreSlim Gate = new(1, 1);
    public readonly ConcurrentDictionary<string, Session> Sessions = new();
    readonly ConcurrentDictionary<string, byte> busy = new();
    public Backups Backups { get; } = new(databasePath);

    public ClassroomDb Db() => new(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 30 }.ToString()).Options);
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public bool HasActiveChecks => !busy.IsEmpty;
    public bool Stopping { get; set; }

    public async Task<T> Write<T>(Func<ClassroomDb, Task<T>> change)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = Db();
            var result = await change(db);
            await db.SaveChangesAsync();
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<T> Read<T>(Func<ClassroomDb, Task<T>> read)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = Db();
            return await read(db);
        }
        finally
        {
            Gate.Release();
        }
    }

    public bool IsBusy(string id) => busy.ContainsKey(id);
    public async Task Initialize()
    {
        await using var db = Db();
        if (File.Exists(databasePath) && (await db.Database.GetPendingMigrationsAsync()).Any())
            Backups.Create("before-migration");
        await db.Database.MigrateAsync();
        var ids = await db.Tasks.Select(t => t.Id).ToListAsync();
        foreach (var task in Catalog.Data.Tasks.Where(t => !ids.Contains(t.Id)))
            db.Tasks.Add(new() { Id = task.Id, Json = Catalog.Write(task) });
        await db.SaveChangesAsync();
        foreach (var row in await db.Tasks.ToArrayAsync())
            if (!await db.TaskRevisions.AnyAsync(r => r.TaskId == row.Id && r.Revision == row.Revision))
                db.TaskRevisions.Add(new() { TaskId = row.Id, Revision = row.Revision, Json = row.Json, AlternativesJson = Wire.Write((await db.Variants.Where(v => v.TaskId == row.Id).OrderBy(v => v.Index).ToArrayAsync()).Select(v => v.Json).ToArray()), Restricted = row.Restricted, Deleted = row.Deleted, At = Now });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        if (Backups.List().All(b => DateTimeOffset.FromUnixTimeMilliseconds(b.CreatedAt).UtcDateTime.Date != DateTime.UtcNow.Date))
            Backups.Create("daily");
    }

    public Session Authorize(HttpContext context, string? role = null)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        if (!Sessions.TryGetValue(hash, out var session) || session.Expires < Now)
        {
            Sessions.TryRemove(hash, out _);
            throw new HttpFault(401, "Войдите в приложение.");
        }

        if (role is not null && session.Role != role)
            throw new HttpFault(403, "Недостаточно прав.");
        return session;
    }

    public void Logout(HttpContext context)
    {
        var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
        Sessions.TryRemove(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))), out _);
    }

    public async Task<LoginDto> Login(LoginRequest input, bool local)
    {
        if (input.Role is not ("teacher" or "student") || input.Password.Length > 1024 || input.Login.Length > 40)
            throw new HttpFault(400, "Проверьте данные входа.");
        if (input.Role == "teacher" && !local)
            throw new HttpFault(403, "Вход преподавателя доступен на его компьютере.");
        return await Read<LoginDto>(async db =>
        {
            var login = input.Role == "teacher" ? "teacher" : input.Login.Trim().ToLowerInvariant();
            var profile = await db.Profiles.SingleOrDefaultAsync(p => p.Login == login && p.Role == input.Role);
            if (profile is null || !Passwords.Verify(input.Password, profile.PasswordHash))
                throw new HttpFault(401, "Неверный логин или пароль.");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            Sessions[Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))] = new(profile.Id, profile.Role, Now + 8 * 3600000);
            return new(token, profile.Role, profile.Id, Wire.Read<ThemePreference>(profile.ThemeJson), Wire.Read<ThemePreset[]>(profile.PresetsJson));
        });
    }

    public async Task<TaskEntity[]> Tasks(ClassroomDb db)
    {
        var order = Catalog.Data.Tasks.Select((t, i) => (t.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var created = await db.TaskRevisions.GroupBy(r => r.TaskId).Select(g => new { Id = g.Key, At = g.Min(r => r.At) }).ToDictionaryAsync(r => r.Id, r => r.At);
        return (await db.Tasks.Where(t => !t.Deleted).ToArrayAsync()).OrderBy(t => order.GetValueOrDefault(t.Id, int.MaxValue)).ThenBy(t => created.GetValueOrDefault(t.Id, long.MaxValue)).ThenBy(t => t.Id).ToArray();
    }

    public static CatalogueItem Item(TaskEntity task)
    {
        var t = Catalog.Read(task.Json);
        return new(t.Id, t.Title, t.Module, t.Project);
    }

    public async Task ValidateAuthoring(ClassroomDb db, TaskDefinition candidate, int? variant = null)
    {
        var rows = (await Tasks(db)).Select(t => Catalog.Read(t.Json)).ToList();
        var position = rows.FindIndex(t => t.Id == candidate.Id);
        if (position < 0)
            rows.Add(candidate);
        else
            rows[position] = candidate;
        var chain = candidate.Project is null ? new[]
        {
            candidate
        }

        : rows.Where(t => t.Project == candidate.Project).ToArray();
        var variants = variant is { } fixedVariant ? new[]
        {
            fixedVariant
        }

        : candidate.VariantEligible == false ? new[]
        {
            1
        }

        : new[]
        {
            1,
            2,
            3,
            4
        };
        foreach (var index in variants)
        {
            string? snapshot = null;
            foreach (var source in chain)
            {
                TaskDefinition task;
                if (variant is not null && source.Id == candidate.Id)
                    task = candidate with
                    {
                        VariantIndex = index
                    };
                else
                {
                    task = index == 1 ? source : Variants.Create(source).ElementAtOrDefault(index - 2) ?? source;
                    if (source.Id != candidate.Id && index > 1 && await db.Variants.FindAsync(source.Id, index)is { } edit)
                        task = Catalog.Read(edit.Json);
                }

                var result = await worker.Run(new(task, snapshot, task.Solution, true));
                if (!result.Result.Correct)
                    throw new HttpFault(400, $"Эталон {task.Id}, вариант {index}, не прошёл проверку: " + (result.Result.Error ?? "результат отличается"));
                if (result.Snapshot is not null)
                    snapshot = result.Snapshot;
            }
        }
    }

    public async Task<AssignmentEntity> Assignment(ClassroomDb db, ProfileEntity student, string taskId)
    {
        var saved = await db.Assignments.FindAsync(student.Id, taskId);
        if (saved is not null)
            return saved;
        var row = await db.Tasks.FindAsync(taskId) ?? throw new HttpFault(404, "Задание не найдено.");
        if (row.Deleted)
            throw new HttpFault(404, "Задание удалено из каталога.");
        var task = Catalog.Read(row.Json);
        if (task.Project is not null && (await db.Assignments.Where(a => a.StudentId == student.Id).ToArrayAsync()).Any(a => Catalog.Read(a.Json).Project == task.Project))
            throw new HttpFault(409, "Этот шаг не входит в уже выданную редакцию проекта.");
        var chain = task.Project is null ? new[]
        {
            row
        }

        : (await Tasks(db)).Where(t => Catalog.Read(t.Json).Project == task.Project).ToArray();
        int? pinned = null;
        var nextOrder = 0;
        if (task.Project is not null)
            foreach (var peer in await db.Assignments.Where(a => a.StudentId == student.Id).ToListAsync())
                if (Catalog.Read(peer.Json).Project == task.Project)
                {
                    pinned ??= Catalog.Read(peer.Json).VariantIndex ?? 1;
                    nextOrder = Math.Max(nextOrder, peer.ProjectOrder + 1);
                }

        foreach (var link in chain)
        {
            if (await db.Assignments.FindAsync(student.Id, link.Id)is not null)
                continue;
            var source = Catalog.Read(link.Json);
            var alternatives = Variants.Create(source);
            foreach (var edit in await db.Variants.Where(v => v.TaskId == link.Id).ToArrayAsync())
                if (edit.Index >= 2 && edit.Index - 2 < alternatives.Length)
                    alternatives[edit.Index - 2] = Catalog.Read(edit.Json);
            var assigned = Variants.Assign(source, student.Slot, alternatives);
            if (pinned is { } index)
                assigned = (index == 1 ? source : alternatives.ElementAtOrDefault(index - 2) ?? source)with
                {
                    VariantIndex = index
                };
            db.Assignments.Add(new() { StudentId = student.Id, TaskId = link.Id, Json = Catalog.Write(assigned), Revision = link.Revision, ProjectOrder = nextOrder++ });
        }

        await db.SaveChangesAsync();
        return (await db.Assignments.FindAsync(student.Id, taskId))!;
    }

    public async Task<StudentTask> GetTask(Session session, string taskId)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = Db();
            var student = await db.Profiles.FindAsync(session.ProfileId) ?? throw new HttpFault(401, "Войдите в приложение.");
            var assignment = await Assignment(db, student, taskId);
            var task = Catalog.Read(assignment.Json);
            var row = await db.Tasks.FindAsync(taskId);
            var times = Wire.Read<long[]>(assignment.HintsJson);
            return new(task.Id, task.Title, task.Module, task.Prompt, task.Concept, task.Example, task.Starter, task.Terms, task.Terms.ToDictionary(t => t, t => Catalog.Data.Glossary.GetValueOrDefault(t, "")), task.Project, task.VariantIndex ?? 1, assignment.Revision, row?.Restricted ?? false, task.Hints.Take(times.Length).ToArray(), times, task.Hints.Length, assignment.Draft, assignment.CompletedAt is not null, student.LockedUntil);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<ExecutionDto> Execute(Session session, string taskId, ExecuteRequest input)
    {
        if (Stopping)
            throw new HttpFault(503, "Преподаватель завершает работу сервера.");
        if (!Guid.TryParse(input.OperationId, out _) || input.Code.Length > 12000)
            throw new HttpFault(400, "Некорректная операция или слишком длинный SQL.");
        var requestHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Wire.Write(new { taskId, input.Code, input.Check }))));
        if (!busy.TryAdd(session.ProfileId, 0))
            throw new HttpFault(429, "Предыдущий запрос ещё выполняется. Повторите позже с тем же номером операции.");
        try
        {
            TaskDefinition task;
            string? snapshot;
            await Gate.WaitAsync();
            try
            {
                await using var db = Db();
                var operation = await db.Operations.FindAsync(session.ProfileId, input.OperationId);
                if (operation is not null)
                {
                    if (operation.RequestHash != requestHash)
                        throw new HttpFault(409, "Номер операции уже использован для другого запроса.");
                    return Wire.Read<ExecutionDto>(operation.ResponseJson);
                }

                var student = await db.Profiles.FindAsync(session.ProfileId) ?? throw new HttpFault(401, "Войдите в приложение.");
                if (student.LockedUntil > Now)
                    throw new HttpFault(423, "Работа приостановлена на 30 секунд.");
                var assignment = await Assignment(db, student, taskId);
                task = Catalog.Read(assignment.Json);
                if (task.Project is not null)
                {
                    var chain = (await db.Assignments.Where(a => a.StudentId == student.Id).ToArrayAsync()).Where(a => Catalog.Read(a.Json).Project == task.Project).ToDictionary(a => a.TaskId);
                    if (chain.Values.Any(a => a.ProjectOrder < assignment.ProjectOrder && a.CompletedAt is null))
                        throw new HttpFault(409, "Сначала завершите предыдущий шаг проекта.");
                    if (input.Check && assignment.CompletedAt is not null)
                        throw new HttpFault(409, "Этот шаг проекта уже сохранён.");
                }

                snapshot = task.Project is null ? null : (await db.Snapshots.FindAsync(student.Id, task.Project))?.Base64;
            }
            finally
            {
                Gate.Release();
            }

            var reply = await worker.Run(new(task, snapshot, input.Code, input.Check));
            await Gate.WaitAsync();
            try
            {
                await using var db = Db();
                await using var transaction = await db.Database.BeginTransactionAsync();
                var assignment = (await db.Assignments.FindAsync(session.ProfileId, taskId))!;
                if (input.Check)
                {
                    assignment.Checks++;
                    if (!reply.Result.Correct)
                        assignment.Errors++;
                    if (reply.Result.Correct)
                    {
                        assignment.CompletedAt ??= Now;
                        if (reply.Snapshot is not null && task.Project is not null)
                        {
                            var row = await db.Snapshots.FindAsync(session.ProfileId, task.Project);
                            if (row is null)
                                db.Snapshots.Add(new() { StudentId = session.ProfileId, Project = task.Project, Base64 = reply.Snapshot });
                            else
                                row.Base64 = reply.Snapshot;
                        }
                    }

                    db.Attempts.Add(new() { Id = Guid.NewGuid().ToString("N"), StudentId = session.ProfileId, TaskId = taskId, Kind = reply.Result.Error ?? (reply.Result.Correct ? "correct" : "different"), At = Now });
                }

                db.Operations.Add(new() { StudentId = session.ProfileId, Id = input.OperationId, RequestHash = requestHash, ResponseJson = Wire.Write(reply.Result), At = Now });
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            finally
            {
                Gate.Release();
            }

            return reply.Result;
        }
        finally
        {
            busy.TryRemove(session.ProfileId, out _);
        }
    }
}
