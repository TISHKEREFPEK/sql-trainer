using Classroom.Contracts;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class SqlSyncRoutes
{
    // Delivery may target an ended lesson, but only an existing work of its participant.
    // This never creates an assignment or permits a delayed SQL execution.
    internal static async Task<ISqlWork> Existing(ClassroomDb db, Session session, string lessonId, string scope, string taskId)
    {
        if (!Guid.TryParseExact(lessonId, "N", out _) || string.IsNullOrWhiteSpace(taskId) || taskId.Length > 200 || scope is null)
            throw new HttpFault(400, "Некорректный контекст сохранённой работы.");
        if (!await db.LessonParticipants.AnyAsync(p => p.SessionId == lessonId && p.StudentId == session.ProfileId))
            throw new HttpFault(403, "Вы не участвовали в этом занятии. Локальная работа сохранена.");
        if (scope == "course") return await db.Assignments.FindAsync(session.ProfileId, taskId) ?? throw new HttpFault(404, "Прежняя работа курса не найдена.");
        if (scope != "lesson:" + lessonId) throw new HttpFault(409, "Работа относится к другому занятию.");
        return await db.LessonSqlWorks.FindAsync(lessonId, session.ProfileId, taskId) ?? throw new HttpFault(404, "Прежняя работа занятия не найдена.");
    }
    static void Validate(string id, long at)
    {
        if (!Guid.TryParseExact(id, "N", out _) || at <= 0 || at > ServerState.Now + 300000)
            throw new HttpFault(400, "Некорректный номер или время локального изменения.");
    }
    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapPut("/sql-sync/draft", (HttpContext ctx, SqlDraftSync input) =>
        {
            var session = state.Authorize(ctx, "student"); Validate(input.Id, input.At);
            if (input.Code is null || input.Code.Length > 12000 || input.BaseHash is null || input.BaseHash.Length != 64 || !input.BaseHash.All(Uri.IsHexDigit))
                throw new HttpFault(400, "Некорректный локальный SQL-черновик.");
            return state.Write(async db =>
            {
                var work = await Existing(db, session, input.LessonId, input.WorkScope, input.TaskId);
                var hash = SqlSyncHash.Text(Wire.Write(input));
                var key = "sql-draft:" + input.Id;
                var previous = await db.Operations.FindAsync(session.ProfileId, key);
                if (previous is not null)
                {
                    if (previous.RequestHash != hash) throw new HttpFault(409, "Номер сохранения уже использован для другого черновика.");
                    return Wire.Read<SqlDraftReceipt>(previous.ResponseJson);
                }
                var serverHash = SqlSyncHash.Text(work.Draft);
                if (input.BaseHash != serverHash && input.Code != work.Draft)
                    return new SqlDraftReceipt(input.Id, "conflict", work.Draft, serverHash);
                work.Draft = input.Code; work.LastActivityAt = ServerState.Now;
                var receipt = new SqlDraftReceipt(input.Id, "saved", input.Code, SqlSyncHash.Text(input.Code));
                db.Operations.Add(new() { StudentId = session.ProfileId, Id = key, RequestHash = hash, ResponseJson = Wire.Write(receipt), At = ServerState.Now });
                return receipt;
            });
        });
        api.MapPost("/sql-sync/action", (HttpContext ctx, SqlActionSync input) =>
        {
            var session = state.Authorize(ctx, "student"); Validate(input.Id, input.At);
            if (input.Action is not ("opened" or "copy" or "cut" or "paste" or "clipboard" or "drop" or "capture" or "activity") || input.Seconds is < 0 or > 30)
                throw new HttpFault(400, "Некорректное действие ученика.");
            return state.Write(async db =>
            {
                var work = await Existing(db, session, input.LessonId, input.WorkScope, input.TaskId);
                var key = "sql-action:" + input.Id; var hash = SqlSyncHash.Text(Wire.Write(input));
                var previous = await db.Operations.FindAsync(session.ProfileId, key);
                if (previous is not null)
                {
                    if (previous.RequestHash != hash) throw new HttpFault(409, "Номер действия уже использован.");
                    return new { ok = true };
                }
                db.Operations.Add(new() { StudentId = session.ProfileId, Id = key, RequestHash = hash, ResponseJson = Wire.Write(input), At = ServerState.Now });
                var student = await db.Profiles.FindAsync(session.ProfileId) ?? throw new HttpFault(401, "Профиль не найден.");
                var lesson = await db.LessonSessions.FindAsync(input.LessonId);
                var immediate = input.LessonId == session.LessonId && state.ActivatedLessonId == input.LessonId && lesson?.State == "running" && ServerState.Now - input.At <= 5000;
                if (input.Action == "opened")
                {
                    if (work.TrackingVersion > 0) work.StartedAt ??= input.At;
                    work.LastActivityAt = ServerState.Now;
                }
                else if (input.Action == "activity")
                {
                    var elapsed = work.LastHeartbeatAt is { } last ? Math.Clamp((ServerState.Now - last) / 1000, 0, 30) : Math.Min(2, input.Seconds);
                    if (immediate && lesson!.AllowSql && student.LockedUntil <= ServerState.Now && work.CompletedAt is null)
                        work.ActiveSeconds += (int)Math.Min(input.Seconds, elapsed);
                    if (immediate) work.LastHeartbeatAt = ServerState.Now;
                }
                else
                {
                    var restricted = (await db.Tasks.FindAsync(input.TaskId))?.Restricted == true;
                    if (restricted)
                    {
                        db.Attempts.Add(new() { Id = student.Id + ":" + input.Id, StudentId = student.Id, TaskId = input.TaskId, LessonId = input.LessonId, WorkScope = input.WorkScope,
                            Kind = input.Action == "capture" ? "capture" : "clipboard", Action = input.Action, At = input.At });
                        // A delayed fact must not lock a student in a different/new lesson.
                        if (immediate && input.Action != "capture" && student.LockedUntil <= ServerState.Now) student.LockedUntil = ServerState.Now + 30000;
                    }
                }
                return new { ok = true };
            });
        });
        api.MapPost("/sql-sync/receipt", (HttpContext ctx, SqlExecutionReceiptRequest input) =>
        {
            var session = state.Authorize(ctx, "student");
            if (input.Request is null || !Guid.TryParse(input.Request.OperationId, out _) || input.Request.Code is null || input.Request.Code.Length > 12000)
                throw new HttpFault(400, "Некорректная квитанция SQL.");
            return state.Read(async db =>
            {
                var row = await db.Operations.FindAsync(session.ProfileId, input.Request.OperationId);
                if (row is null) return new SqlExecutionReceipt(false, null);
                if (row.RequestHash != ServerState.ExecutionHash(input.Request.LessonId, input.TaskId, input.Request))
                    throw new HttpFault(409, "Квитанция относится к другому запросу.");
                return new SqlExecutionReceipt(true, Wire.Read<ExecutionDto>(row.ResponseJson));
            });
        });
    }
}
