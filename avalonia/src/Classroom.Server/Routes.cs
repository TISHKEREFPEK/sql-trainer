using System.Net;
using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;
public static class Routes
{
    static bool Local(HttpContext context) => context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
    static ProfileEntity Profile(ClassroomDb db, string id) => db.Profiles.Find(id) ?? throw new HttpFault(404, "Профиль не найден.");
    public static void Map(WebApplication app, ServerState state, string dataDir, WorkerRunner worker)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/status", () => state.Read(async db => new StatusDto(!await db.Profiles.AnyAsync(p => p.Role == "teacher"), "0.1.0")));
        api.MapPost("/setup", async (HttpContext ctx, PasswordRequest input) =>
        {
            if (!Local(ctx))
                throw new HttpFault(403, "Первичная настройка доступна только на компьютере преподавателя.");
            if (input.Password.Length < 10 || input.Password.Length > 1024)
                throw new HttpFault(400, "Пароль преподавателя: от 10 до 1024 символов.");
            return await state.Write(async db =>
            {
                if (await db.Profiles.AnyAsync(p => p.Role == "teacher"))
                    throw new HttpFault(409, "Преподаватель уже настроен.");
                db.Profiles.Add(new() { Id = "teacher", Login = "teacher", Role = "teacher", PasswordHash = Passwords.Hash(input.Password) });
                return new
                {
                    ok = true
                };
            });
        }).RequireRateLimiting("login");
        api.MapPost("/login", (HttpContext ctx, LoginRequest input) => state.Login(input, Local(ctx))).RequireRateLimiting("login");
        api.MapPost("/logout", (HttpContext ctx) =>
        {
            state.Authorize(ctx);
            state.Logout(ctx);
            return new
            {
                ok = true
            };
        });
        api.MapGet("/catalog", async (HttpContext ctx) =>
        {
            var session = state.Authorize(ctx, "student");
            return await state.Read(async db =>
            {
                var assigned = await db.Assignments.Where(a => a.StudentId == session.ProfileId).ToArrayAsync();
                var projects = assigned.Select(a => Catalog.Read(a.Json).Project).Where(p => p is not null).ToHashSet();
                var assignedIds = assigned.Select(a => a.TaskId).ToHashSet();
                var items = (await state.Tasks(db)).Select(ServerState.Item).Where(t => t.Project is null || !projects.Contains(t.Project) || assignedIds.Contains(t.Id)).ToDictionary(t => t.Id);
                foreach (var a in assigned)
                {
                    var task = Catalog.Read(a.Json);
                    items[task.Id] = new(task.Id, task.Title, task.Module, task.Project);
                }

                return new CatalogueDto(items.Values.ToArray(), await db.Assignments.Where(a => a.StudentId == session.ProfileId && a.CompletedAt != null).Select(a => a.TaskId).ToArrayAsync(), Profile(db, session.ProfileId).LockedUntil);
            });
        });
        api.MapGet("/glossary", (HttpContext ctx) =>
        {
            state.Authorize(ctx, "student");
            return Catalog.Data.GlossaryLong;
        });
        api.MapGet("/tasks/{id}", (HttpContext ctx, string id) => state.GetTask(state.Authorize(ctx, "student"), id));
        api.MapPost("/tasks/{id}/execute", (HttpContext ctx, string id, ExecuteRequest input) => state.Execute(state.Authorize(ctx, "student"), id, input));
        api.MapPut("/tasks/{id}/draft", async (HttpContext ctx, string id, DraftRequest input) =>
        {
            var session = state.Authorize(ctx, "student");
            if (input.Code.Length > 12000)
                throw new HttpFault(400, "SQL слишком длинный.");
            return await state.Write(async db =>
            {
                var row = await state.Assignment(db, Profile(db, session.ProfileId), id);
                row.Draft = input.Code;
                return new
                {
                    ok = true
                };
            });
        });
        api.MapPost("/tasks/{id}/activity", async (HttpContext ctx, string id, ActivityRequest input) =>
        {
            var session = state.Authorize(ctx, "student");
            if (input.Seconds is < 0 or > 30)
                throw new HttpFault(400, "Некорректное время активности.");
            return await state.Write(async db =>
            {
                var row = await state.Assignment(db, Profile(db, session.ProfileId), id);
                row.ActiveSeconds += input.Seconds;
                return new
                {
                    ok = true
                };
            });
        });
        api.MapPost("/tasks/{id}/hint", async (HttpContext ctx, string id, HintRequest input) =>
        {
            var session = state.Authorize(ctx, "student");
            await state.Write(async db =>
            {
                var student = Profile(db, session.ProfileId);
                if (student.LockedUntil > ServerState.Now)
                    throw new HttpFault(423, "Работа приостановлена.");
                var row = await state.Assignment(db, student, id);
                var task = Catalog.Read(row.Json);
                var times = Wire.Read<long[]>(row.HintsJson);
                if (input.Index < 0 || input.Index >= task.Hints.Length)
                    throw new HttpFault(400, "Подсказка не найдена.");
                if (input.Index > times.Length)
                    throw new HttpFault(409, "Откройте предыдущую подсказку.");
                if (input.Index == times.Length)
                    row.HintsJson = Wire.Write(times.Append(ServerState.Now).ToArray());
                return true;
            });
            return await state.GetTask(session, id);
        });
        api.MapPost("/tasks/{id}/violation", async (HttpContext ctx, string id) =>
        {
            var session = state.Authorize(ctx, "student");
            return await state.Write(async db =>
            {
                var student = Profile(db, session.ProfileId);
                await state.Assignment(db, student, id);
                var policy = await db.Tasks.FindAsync(id);
                if (policy?.Restricted == true && student.LockedUntil <= ServerState.Now)
                {
                    student.LockedUntil = ServerState.Now + 30000;
                    db.Attempts.Add(new() { Id = Guid.NewGuid().ToString("N"), StudentId = student.Id, TaskId = id, Kind = "clipboard", At = ServerState.Now });
                }

                return new
                {
                    student.LockedUntil
                };
            });
        });
        api.MapPut("/theme", async (HttpContext ctx, ThemePreference input) =>
        {
            var session = state.Authorize(ctx);
            Themes.Validate(input);
            return await state.Write(db =>
            {
                Profile(db, session.ProfileId).ThemeJson = Wire.Write(input);
                return Task.FromResult(new { ok = true });
            });
        });
        api.MapGet("/theme-presets", async (HttpContext ctx) =>
        {
            var session = state.Authorize(ctx);
            return await state.Read(db => Task.FromResult(Wire.Read<ThemePreset[]>(Profile(db, session.ProfileId).PresetsJson)));
        });
        api.MapPost("/theme-presets", async (HttpContext ctx, ThemePresetRequest input) =>
        {
            var session = state.Authorize(ctx);
            Themes.Validate(input.Theme);
            return await state.Write(db =>
            {
                var profile = Profile(db, session.ProfileId);
                var presets = Wire.Read<ThemePreset[]>(profile.PresetsJson).Append(new(Guid.NewGuid().ToString("N"), input.Name.Trim(), input.Theme)).ToArray();
                Themes.ValidatePresets(presets);
                profile.PresetsJson = Wire.Write(presets);
                return Task.FromResult(presets);
            });
        });
        api.MapDelete("/theme-presets/{id}", async (HttpContext ctx, string id) =>
        {
            var session = state.Authorize(ctx);
            return await state.Write(db =>
            {
                var profile = Profile(db, session.ProfileId);
                var before = Wire.Read<ThemePreset[]>(profile.PresetsJson);
                if (!before.Any(p => p.Id == id))
                    throw new HttpFault(404, "Тема не найдена.");
                var after = before.Where(p => p.Id != id).ToArray();
                profile.PresetsJson = Wire.Write(after);
                return Task.FromResult(after);
            });
        });
        api.MapGet("/teacher/overview", async (HttpContext ctx) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Read(async db =>
            {
                var students = await db.Profiles.Where(p => p.Role == "student").ToArrayAsync();
                var assignments = await db.Assignments.ToArrayAsync();
                var logins = students.ToDictionary(p => p.Id, p => p.Login);
                return new TeacherOverview(students.Select(p => new StudentSummary(p.Id, p.Login, p.GroupId, assignments.Count(a => a.StudentId == p.Id && a.CompletedAt != null), p.LockedUntil)).ToArray(), await db.Groups.Select(g => new GroupDto(g.Id, g.Name)).ToArrayAsync(), (await state.Tasks(db)).Select(ServerState.Item).ToArray(), (await db.Attempts.OrderByDescending(a => a.At).Take(1000).ToArrayAsync()).Select(a => new AttemptDto(logins.GetValueOrDefault(a.StudentId, ""), a.TaskId, a.Kind, a.At)).ToArray(), assignments.Where(a => Wire.Read<long[]>(a.HintsJson).Length > 0).Select(a => new HintReport(logins[a.StudentId], a.TaskId, Catalog.Read(a.Json).VariantIndex ?? 1, Wire.Read<long[]>(a.HintsJson))).ToArray(), assignments.Select(a => new TaskStatistics(logins[a.StudentId], a.TaskId, a.ActiveSeconds, a.Checks, a.Errors, a.CompletedAt)).ToArray());
            });
        });
        api.MapPost("/teacher/students", async (HttpContext ctx, CreateStudentRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            var login = input.Login.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(login) || login == "teacher" || login.Length > 40 || input.Password.Length < 8 || input.Password.Length > 1024)
                throw new HttpFault(400, "Логин до 40 символов; пароль от 8 символов.");
            return await state.Write(async db =>
            {
                if (await db.Profiles.AnyAsync(p => p.Login == login))
                    throw new HttpFault(409, "Логин уже существует.");
                if (input.GroupId is not null && !await db.Groups.AnyAsync(g => g.Id == input.GroupId))
                    throw new HttpFault(404, "Группа не найдена.");
                var slot = (await db.Profiles.Where(p => p.Role == "student" && p.GroupId == input.GroupId).MaxAsync(p => (int? )p.Slot) ?? -1) + 1;
                var row = new ProfileEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Login = login,
                    PasswordHash = Passwords.Hash(input.Password),
                    GroupId = input.GroupId,
                    Slot = slot
                };
                db.Profiles.Add(row);
                return new
                {
                    row.Id
                };
            });
        });
        api.MapDelete("/teacher/students/{id}", async (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Write(db =>
            {
                var profile = Profile(db, id);
                if (profile.Role != "student")
                    throw new HttpFault(403, "Нельзя удалить преподавателя.");
                if (state.IsBusy(id))
                    throw new HttpFault(409, "Дождитесь завершения запроса ученика.");
                db.Profiles.Remove(profile);
                foreach (var session in state.Sessions.Where(s => s.Value.ProfileId == id))
                    state.Sessions.TryRemove(session.Key, out _);
                return Task.FromResult(new { ok = true });
            });
        });
        api.MapPut("/teacher/students/{id}/password", async (HttpContext ctx, string id, PasswordRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            if (input.Password.Length < 8 || input.Password.Length > 1024)
                throw new HttpFault(400, "Пароль от 8 символов.");
            return await state.Write(db =>
            {
                var p = Profile(db, id);
                if (p.Role != "student")
                    throw new HttpFault(403, "Нужен ученик.");
                p.PasswordHash = Passwords.Hash(input.Password);
                return Task.FromResult(new { ok = true });
            });
        });
        api.MapPut("/teacher/students/{id}/group", async (HttpContext ctx, string id, GroupMembershipRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Write(async db =>
            {
                var profile = Profile(db, id);
                if (profile.Role != "student")
                    throw new HttpFault(403, "Нужен ученик.");
                if (input.GroupId is not null && !await db.Groups.AnyAsync(g => g.Id == input.GroupId))
                    throw new HttpFault(404, "Группа не найдена.");
                if (profile.GroupId != input.GroupId)
                {
                    profile.Slot = (await db.Profiles.Where(p => p.Role == "student" && p.GroupId == input.GroupId).MaxAsync(p => (int? )p.Slot) ?? -1) + 1;
                    profile.GroupId = input.GroupId;
                }

                return new
                {
                    ok = true
                };
            });
        });
        api.MapPost("/teacher/students/{id}/unlock", async (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Write(db =>
            {
                Profile(db, id).LockedUntil = 0;
                return Task.FromResult(new { ok = true });
            });
        });
        api.MapPost("/teacher/groups", async (HttpContext ctx, GroupRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100)
                throw new HttpFault(400, "Введите название группы.");
            return await state.Write(db =>
            {
                var group = new GroupEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = input.Name.Trim()
                };
                db.Groups.Add(group);
                return Task.FromResult(new GroupDto(group.Id, group.Name));
            });
        });
        api.MapDelete("/teacher/groups/{id}", async (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Write(async db =>
            {
                var group = await db.Groups.FindAsync(id) ?? throw new HttpFault(404, "Группа не найдена.");
                foreach (var p in await db.Profiles.Where(p => p.GroupId == id).ToArrayAsync())
                    p.GroupId = null;
                db.Groups.Remove(group);
                return new
                {
                    ok = true
                };
            });
        });
        api.MapGet("/teacher/tasks/{id}", async (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Read(async db =>
            {
                var row = await db.Tasks.FindAsync(id) ?? throw new HttpFault(404, "Задание не найдено.");
                var alternatives = Variants.Create(Catalog.Read(row.Json));
                foreach (var edit in await db.Variants.Where(v => v.TaskId == id).ToArrayAsync())
                    if (edit.Index - 2 < alternatives.Length)
                        alternatives[edit.Index - 2] = Catalog.Read(edit.Json);
                return new TeacherTaskDto(row.Json, alternatives.Select(Catalog.Write).ToArray(), row.Restricted, row.Revision);
            });
        });
        api.MapGet("/teacher/tasks/{id}/revisions", async (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Read(async db => (await db.TaskRevisions.Where(r => r.TaskId == id).OrderByDescending(r => r.Revision).ToArrayAsync()).Select(r => new TaskRevisionDto(r.Revision, r.At, r.Deleted, new(r.Json, Wire.Read<string[]>(r.AlternativesJson), r.Restricted, r.Revision))).ToArray());
        });
        api.MapPut("/teacher/tasks/{id}", async (HttpContext ctx, string id, SaveTaskRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            var task = Catalog.Read(input.TaskJson);
            Catalog.Validate(task);
            if (task.Id != id)
                throw new HttpFault(400, "Идентификатор задания не совпадает.");
            return await state.Write(async db =>
            {
                var row = await db.Tasks.FindAsync(id);
                if (row is not null && (Catalog.Read(row.Json).Project != task.Project || (Catalog.Read(row.Json).Mode ?? "query") != (task.Mode ?? "query")))
                    throw new HttpFault(409, "Тип задания и проект существующей задачи менять нельзя.");
                await state.ValidateAuthoring(db, task);
                if (row is null)
                {
                    row = new()
                    {
                        Id = id
                    };
                    db.Tasks.Add(row);
                }

                row.Json = input.TaskJson;
                row.Restricted = input.Restricted;
                row.Deleted = false;
                row.Revision++;
                return new
                {
                    ok = true
                };
            });
        });
        api.MapPut("/teacher/tasks/{id}/variants/{index:int}", async (HttpContext ctx, string id, int index, SaveVariantRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            var task = Catalog.Read(input.TaskJson);
            Catalog.Validate(task);
            if (index is < 2 or > 4 || task.Id != id)
                throw new HttpFault(400, "Некорректный вариант.");
            return await state.Write(async db =>
            {
                var row = await db.Tasks.FindAsync(id) ?? throw new HttpFault(404, "Задание не найдено.");
                var original = Catalog.Read(row.Json);
                if (original.VariantEligible == false || task.Project != original.Project || (task.Mode ?? "query") != (original.Mode ?? "query"))
                    throw new HttpFault(409, "Вариант должен сохранять тип задания и проект.");
                var checkedTask = task with
                {
                    VariantIndex = index
                };
                await state.ValidateAuthoring(db, checkedTask, index);
                var variant = await db.Variants.FindAsync(id, index);
                if (variant is null)
                {
                    variant = new()
                    {
                        TaskId = id,
                        Index = index
                    };
                    db.Variants.Add(variant);
                }

                variant.Json = Catalog.Write(checkedTask);
                row.Revision++;
                return new
                {
                    ok = true
                };
            });
        });
        api.MapDelete("/teacher/tasks/{id}", async (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Write(async db =>
            {
                var row = await db.Tasks.FindAsync(id) ?? throw new HttpFault(404, "Задание не найдено.");
                row.Deleted = true;
                row.Revision++;
                return new
                {
                    ok = true
                };
            });
        });
        var staging = new Dictionary<string, (string Path, string Hash, string Owner, long Expires)>();
        api.MapPost("/teacher/import/preview", async (HttpContext ctx) =>
        {
            var session = state.Authorize(ctx, "teacher");
            var dir = Path.Combine(dataDir, "staging");
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(dir, id + ".sqlite");
            try
            {
                await using (var file = File.Create(path))
                {
                    var buffer = new byte[81920];
                    long bytes = 0;
                    int read;
                    while ((read = await ctx.Request.Body.ReadAsync(buffer)) > 0)
                    {
                        bytes += read;
                        if (bytes > 64 * 1024 * 1024)
                            throw new HttpFault(413, "База больше 64 МБ.");
                        await file.WriteAsync(buffer.AsMemory(0, read));
                    }
                }

                var preview = ElectronImport.Preview(path, id);
                lock (staging)
                {
                    foreach (var old in staging.Where(p => p.Value.Expires < ServerState.Now).ToArray())
                    {
                        File.Delete(old.Value.Path);
                        staging.Remove(old.Key);
                    }

                    staging[id] = (path, preview.SourceHash, session.ProfileId, ServerState.Now + 1800000);
                }

                return preview;
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        });
        api.MapPost("/teacher/import/commit", async (HttpContext ctx, ImportCommitRequest input) =>
        {
            var session = state.Authorize(ctx, "teacher");
            (string Path, string Hash, string Owner, long Expires) item;
            lock (staging)
            {
                if (!staging.TryGetValue(input.PreviewId, out item) || item.Owner != session.ProfileId || item.Expires < ServerState.Now)
                    throw new HttpFault(404, "Предпросмотр истёк. Выберите файл снова.");
            }

            await state.Gate.WaitAsync();
            try
            {
                if (state.HasActiveChecks)
                    throw new HttpFault(409, "Дождитесь завершения проверок перед импортом.");
                await using var db = state.Db();
                var imported = await ElectronImport.Commit(db, item.Path, item.Hash, state.Backups);
                lock (staging)
                    staging.Remove(input.PreviewId);
                File.Delete(item.Path);
                return new
                {
                    imported
                };
            }
            finally
            {
                state.Gate.Release();
            }
        });
        api.MapGet("/teacher/backups", (HttpContext ctx) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Backups.List();
        });
        api.MapPost("/teacher/backups", async (HttpContext ctx) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Write(db => Task.FromResult(new { Name = Path.GetFileName(state.Backups.Create("manual")) }));
        });
        api.MapPost("/teacher/backups/{name}/restore", async (HttpContext ctx, string name) =>
        {
            state.Authorize(ctx, "teacher");
            await state.Gate.WaitAsync();
            try
            {
                if (state.HasActiveChecks)
                    throw new HttpFault(409, "Дождитесь завершения проверок.");
                state.Backups.Restore(name);
                state.Sessions.Clear();
                return new
                {
                    ok = true,
                    loginRequired = true
                };
            }
            finally
            {
                state.Gate.Release();
            }
        });
    }
}
