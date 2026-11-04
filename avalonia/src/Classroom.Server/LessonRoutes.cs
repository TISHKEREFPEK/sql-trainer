using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class LessonRoutes
{
    static string Id(string value) => Guid.TryParseExact(value, "N", out _) ? value : throw new HttpFault(400, "Некорректный номер занятия или регистрации.");
    static string Name(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw new HttpFault(400, "Введите ФИО до 200 символов, без управляющих символов.");
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
    static void Password(string value)
    {
        if (value.Length is < 8 or > 1024) throw new HttpFault(400, "Пароль: от 8 до 1024 символов.");
    }
    static Task<LessonSessionEntity?> Current(ClassroomDb db) => db.LessonSessions.SingleOrDefaultAsync(s => s.EndedAt == null);
    static bool Active(ServerState state, LessonSessionEntity? lesson) => lesson is not null && state.ActivatedLessonId == lesson.Id;
    static bool Running(ServerState state, LessonSessionEntity? lesson) => Active(state, lesson) && lesson!.State == "running";
    static string Reason(ServerState state, LessonSessionEntity? lesson) => lesson is null ? "Преподаватель ещё не начал занятие."
        : lesson.State == "finished" ? "Занятие завершено."
        : !Active(state, lesson) ? "Преподаватель должен начать или восстановить занятие."
        : lesson.State == "paused" ? "Занятие на паузе. Работа сохранена; дождитесь преподавателя."
        : lesson.State != "running" ? "Занятие ещё не началось." : "";
    static LessonView View(ServerState state, LessonSessionEntity row, int members) => new(row.Id, row.GroupId, row.GroupName, row.State,
        row.CreatedAt, row.StartedAt, row.PausedAt, row.EndedAt, row.AllowSql, row.AllowElectronics, row.AdmissionOpen,
        row.OpenToAllGroups, row.RegistrationOpen, row.FreePractice, Active(state, row), members);
    public static async Task<LessonView> View(ServerState state, ClassroomDb db, LessonSessionEntity row) => View(state, row, await db.LessonParticipants.CountAsync(p => p.SessionId == row.Id));
    static async Task Policy(ClassroomDb db, LessonSessionEntity row, LessonPolicyRequest input)
    {
        if (!input.AllowSql && !input.AllowElectronics) throw new HttpFault(400, "Разрешите SQL, электронику или оба направления.");
        var group = input.GroupId is null ? null : await db.Groups.FindAsync(input.GroupId) ?? throw new HttpFault(404, "Группа не найдена.");
        if (input.RegistrationOpen && group is null) throw new HttpFault(400, "Для регистрации выберите группу.");
        if (input.AdmissionOpen && !input.OpenToAllGroups && group is null) throw new HttpFault(400, "Выберите группу или включите свободный вход.");
        row.GroupId = group?.Id; row.GroupName = group?.Name ?? "Свободное занятие";
        row.AllowSql = input.AllowSql; row.AllowElectronics = input.AllowElectronics;
        row.AdmissionOpen = input.AdmissionOpen; row.OpenToAllGroups = input.OpenToAllGroups;
        row.RegistrationOpen = input.RegistrationOpen; row.FreePractice = input.FreePractice;
    }
    static LessonPolicyRequest Policy(LessonSessionEntity row) => new(row.GroupId, row.AllowSql, row.AllowElectronics, row.AdmissionOpen, row.OpenToAllGroups, row.RegistrationOpen, row.FreePractice);

    public static async Task<string> Join(ServerState state, ClassroomDb db, ProfileEntity student)
    {
        var lesson = await Current(db);
        if (!Running(state, lesson)) throw new HttpFault(423, Reason(state, lesson));
        if (!lesson!.AdmissionOpen) throw new HttpFault(423, "Преподаватель закрыл вход на занятие.");
        if (!lesson.OpenToAllGroups && (lesson.GroupId is null || lesson.GroupId != student.GroupId))
            throw new HttpFault(403, "На занятие допущена другая группа.");
        var participant = await db.LessonParticipants.FindAsync(lesson.Id, student.Id);
        if (participant is null)
            db.LessonParticipants.Add(new() { SessionId = lesson.Id, StudentId = student.Id, JoinedAt = ServerState.Now, LastSeenAt = ServerState.Now,
                GroupNameAtEntry = student.GroupId is null ? "Без группы" : (await db.Groups.FindAsync(student.GroupId))?.Name ?? "Без группы" });
        else participant.LastSeenAt = ServerState.Now;
        return lesson.Id;
    }

    // Existing participants keep membership when admission switches to another group or closes.
    // This check is separate from the permission to run work and from the individual violation lock.
    public static async Task<LessonSessionEntity> RequireMember(ServerState state, ClassroomDb db, Session session, bool saving = false)
    {
        var lesson = session.LessonId is null ? null : await db.LessonSessions.FindAsync(session.LessonId);
        if (lesson is null || !await db.LessonParticipants.AnyAsync(p => p.SessionId == lesson.Id && p.StudentId == session.ProfileId))
            throw new HttpFault(423, "Войдите в начатое преподавателем занятие.");
        // Late draft/history delivery is retained even after pause/end; it never grants execution permission.
        if (!saving && (!Active(state, lesson) || lesson.State == "finished")) throw new HttpFault(423, Reason(state, lesson));
        return lesson;
    }
    public static async Task RequireWork(ServerState state, ClassroomDb db, Session session, string subject)
    {
        var lesson = await RequireMember(state, db, session);
        if (!Running(state, lesson)) throw new HttpFault(423, Reason(state, lesson));
        if (subject == "sql" && !lesson.AllowSql || subject == "electronics" && !lesson.AllowElectronics)
            throw new HttpFault(403, "Преподаватель не разрешил это направление на занятии.");
    }
    public static void CheckContext(Session session, string? lessonId)
    {
        if (lessonId is not null && lessonId != session.LessonId)
            throw new HttpFault(409, "Этот запрос относится к другому занятию. Его черновик не должен заменять новую работу.");
    }

    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapGet("/teacher/lessons", (HttpContext ctx, int? offset) =>
        {
            state.Authorize(ctx, "teacher"); var start = offset ?? 0;
            if (start is < 0 or > 1000000) throw new HttpFault(400, "Некорректная страница истории.");
            return state.Read(async db =>
            {
                var current = await Current(db);
                var rows = await db.LessonSessions.Where(s => s.EndedAt != null).OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id).Skip(start).Take(21).ToArrayAsync();
                var counts = await db.LessonParticipants.GroupBy(p => p.SessionId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count);
                return new LessonDashboard(current is null ? null : View(state, current, counts.GetValueOrDefault(current.Id)), rows.Take(20).Select(s => View(state, s, counts.GetValueOrDefault(s.Id))).ToArray(), rows.Length > 20, start + Math.Min(20, rows.Length));
            });
        });
        api.MapPost("/teacher/lessons", (HttpContext ctx, CreateLessonRequest input) =>
        {
            var teacher = state.Authorize(ctx, "teacher"); var id = Id(input.Id);
            return state.Write(async db =>
            {
                var existing = await db.LessonSessions.FindAsync(id);
                if (existing is not null)
                {
                    if (existing.TeacherId != teacher.ProfileId || Policy(existing) != input.Policy) throw new HttpFault(409, "Этот номер уже использован для другого занятия.");
                    return await View(state, db, existing);
                }
                if (await Current(db) is not null) throw new HttpFault(409, "Сначала завершите или восстановите незавершённое занятие.");
                var row = new LessonSessionEntity { Id = id, TeacherId = teacher.ProfileId, CreatedAt = ServerState.Now };
                await Policy(db, row, input.Policy); db.LessonSessions.Add(row);
                return View(state, row, 0);
            });
        });
        api.MapPut("/teacher/lessons/{id}/policy", (HttpContext ctx, string id, LessonPolicyRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Write(async db =>
            {
                var row = await db.LessonSessions.FindAsync(id) ?? throw new HttpFault(404, "Занятие не найдено.");
                if (row.EndedAt is not null) throw new HttpFault(409, "Завершённое занятие менять нельзя.");
                await Policy(db, row, input); return await View(state, db, row);
            });
        });
        api.MapPost("/teacher/lessons/{id}/{action}", (HttpContext ctx, string id, string action) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Write(async db =>
            {
                var row = await db.LessonSessions.FindAsync(id) ?? throw new HttpFault(404, "Занятие не найдено.");
                if (action == "finish")
                {
                    row.State = "finished"; row.EndedAt ??= ServerState.Now; row.AdmissionOpen = false; row.RegistrationOpen = false;
                }
                else if (row.State == "finished") throw new HttpFault(409, "Занятие уже завершено. Создайте новое.");
                else if (action == "start")
                {
                    if (row.State is not ("preparing" or "running")) throw new HttpFault(409, "Для паузы используйте «Продолжить».");
                    row.StartedAt ??= ServerState.Now; row.State = "running";
                }
                else if (action == "pause")
                {
                    if (row.State is not ("running" or "paused")) throw new HttpFault(409, "Сначала начните занятие.");
                    row.PausedAt ??= ServerState.Now; row.State = "paused";
                }
                else if (action == "resume")
                {
                    if (row.State is not ("running" or "paused")) throw new HttpFault(409, "Сначала начните занятие.");
                    row.PausedAt = null; row.State = "running";
                }
                else if (action != "restore" || row.State == "preparing") throw new HttpFault(400, "Неизвестное действие с занятием.");
                await db.SaveChangesAsync();
                if (row.State != "finished") state.ActivatedLessonId = row.Id;
                else if (state.ActivatedLessonId == row.Id) state.ActivatedLessonId = null;
                return await View(state, db, row);
            });
        });
        api.MapGet("/admission", () => state.Read(async db =>
        {
            var row = await Current(db); var running = Running(state, row);
            var admission = running && row!.AdmissionOpen; var registration = running && row!.RegistrationOpen && row.GroupId is not null;
            var groups = admission ? await db.Groups.Where(g => row!.OpenToAllGroups || g.Id == row.GroupId).OrderBy(g => g.Name).Select(g => new GroupDto(g.Id, g.Name)).ToArrayAsync() : [];
            if (admission && row!.OpenToAllGroups && await db.Profiles.AnyAsync(p => p.Role == "student" && p.GroupId == null)) groups = [.. groups, new("", "Без группы")];
            var registrationGroup = registration ? await db.Groups.Where(g => g.Id == row!.GroupId).Select(g => new GroupDto(g.Id, g.Name)).SingleOrDefaultAsync() : null;
            return new AdmissionDto(row?.Id, row?.State ?? "closed", Reason(state, row) is { Length: > 0 } reason ? reason : admission ? "Выберите группу и своё ФИО." : "Преподаватель закрыл вход на занятие.", admission, registration, registrationGroup, groups);
        }));
        api.MapGet("/admission/students", (string? groupId) => state.Read(async db =>
        {
            var row = await Current(db);
            if (!Running(state, row) || !row!.AdmissionOpen) throw new HttpFault(423, "Вход на занятие закрыт.");
            var group = string.IsNullOrEmpty(groupId) ? null : groupId;
            if (!row.OpenToAllGroups && (group is null || row.GroupId != group)) throw new HttpFault(403, "Группа не допущена на занятие.");
            var students = await db.Profiles.Where(p => p.Role == "student" && p.GroupId == group).Select(p => new { p.Id, p.Login, p.FullName }).ToArrayAsync();
            var names = students.GroupBy(p => p.FullName ?? p.Login).ToDictionary(g => g.Key, g => g.Count());
            return students.Select(p => new StudentChoice(p.Id, (p.FullName ?? p.Login) + (names[p.FullName ?? p.Login] > 1 ? " · " + p.Id[..Math.Min(6, p.Id.Length)] : ""))).OrderBy(p => p.Name).ToArray();
        })).RequireRateLimiting("login");
        api.MapPost("/student-login", (StudentLoginRequest input) => state.Write(async db =>
        {
            if (input.Password.Length > 1024) throw new HttpFault(400, "Проверьте данные входа.");
            var groupId = string.IsNullOrEmpty(input.GroupId) ? null : input.GroupId;
            var profile = await db.Profiles.SingleOrDefaultAsync(p => p.Id == input.StudentId && p.Role == "student" && p.GroupId == groupId);
            if (profile is null || !Passwords.Verify(input.Password, profile.PasswordHash)) throw new HttpFault(401, "Неверное ФИО или пароль.");
            var lessonId = await Join(state, db, profile); await db.SaveChangesAsync();
            return state.IssueLogin(profile, lessonId);
        })).RequireRateLimiting("login");
        api.MapPost("/student-registration", (StudentRegistrationRequest input) =>
        {
            var id = Id(input.Id); var fullName = Name(input.FullName); Password(input.Password);
            return state.Write(async db =>
            {
                var row = await Current(db);
                if (!Running(state, row) || !row!.RegistrationOpen || row.GroupId is null) throw new HttpFault(423, "Преподаватель не открыл регистрацию.");
                var existing = await db.Profiles.FindAsync(id);
                if (existing is not null)
                {
                    if (existing.Role != "student" || existing.GroupId != row.GroupId || existing.FullName != fullName || !Passwords.Verify(input.Password, existing.PasswordHash)) throw new HttpFault(409, "Номер регистрации уже использован.");
                    return new StudentChoice(existing.Id, fullName);
                }
                if (await db.Profiles.AnyAsync(p => p.Role == "student" && p.GroupId == row.GroupId && p.FullName == fullName)) throw new HttpFault(409, "В этой группе уже есть ученик с таким ФИО. Обратитесь к преподавателю.");
                var slot = (await db.Profiles.Where(p => p.Role == "student" && p.GroupId == row.GroupId).MaxAsync(p => (int?)p.Slot) ?? -1) + 1;
                db.Profiles.Add(new() { Id = id, Login = "s-" + id, FullName = fullName, GroupId = row.GroupId, Slot = slot, PasswordHash = Passwords.Hash(input.Password) });
                return new StudentChoice(id, fullName);
            });
        }).RequireRateLimiting("login");
        api.MapPut("/teacher/students/{id}/name", (HttpContext ctx, string id, StudentNameRequest input) =>
        {
            state.Authorize(ctx, "teacher"); var name = Name(input.FullName);
            return state.Write(async db =>
            {
                var student = await db.Profiles.SingleOrDefaultAsync(p => p.Id == id && p.Role == "student") ?? throw new HttpFault(404, "Ученик не найден.");
                student.FullName = name; return new StudentChoice(id, name);
            });
        });
        api.MapGet("/student/lesson", async (HttpContext ctx) =>
        {
            var session = state.Authorize(ctx, "student");
            return await state.Read(async db =>
            {
                var row = session.LessonId is null ? null : await db.LessonSessions.FindAsync(session.LessonId);
                return new StudentLessonDto(row is null ? null : await View(state, db, row), Running(state, row), Reason(state, row));
            });
        });
    }
}
