using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class LearningAnalytics
{
    static bool Check(AttemptEntity e) => e.Action == "check" || e.Action is null && e.Kind is not ("clipboard" or "capture");
    static bool Violation(AttemptEntity e) => e.Kind is "clipboard" or "capture";

    public static LearningRecord Record(AssignmentEntity a, AttemptEntity[] events)
    {
        var task = Catalog.Read(a.Json);
        var first = events.Where(Check).OrderBy(e => e.At).ThenBy(e => e.Id).FirstOrDefault();
        return new(a.StudentId, a.TaskId, task.Title, task.Module, task.VariantIndex ?? 1, a.Revision,
            a.StartedAt, a.LastActivityAt, a.CompletedAt, a.ActiveSeconds, a.ActiveSecondsAtCompletion,
            a.Checks, a.Errors, Wire.Read<long[]>(a.HintsJson).Length, task.Hints.Length,
            events.Count(Violation), first is null || a.TrackingVersion == 0 ? null : first.Kind == "correct" ? 0 : 1);
    }

    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapGet("/teacher/learning", async (HttpContext ctx) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Read(async db =>
            {
                var catalog = (await state.Tasks(db)).Select(ServerState.Item).ToArray();
                var assignments = await db.Assignments.AsNoTracking().ToArrayAsync();
                // SQL text is deliberately excluded from the overview query.
                var events = (await db.Attempts.AsNoTracking().Select(e => new { e.Id, e.StudentId, e.TaskId, e.Kind, e.Action, e.At }).ToArrayAsync())
                    .Select(e => new AttemptEntity { Id = e.Id, StudentId = e.StudentId, TaskId = e.TaskId, Kind = e.Kind, Action = e.Action, At = e.At }).ToLookup(e => (e.StudentId, e.TaskId));
                var records = assignments.Select(a => Record(a, events[(a.StudentId, a.TaskId)].ToArray())).ToArray();
                var students = (await db.Profiles.Where(p => p.Role == "student").ToArrayAsync()).Select(p =>
                {
                    var own = assignments.Where(a => a.StudentId == p.Id).ToArray();
                    var projects = own.Select(a => Catalog.Read(a.Json).Project).Where(x => x is not null).ToHashSet();
                    var ids = own.Select(a => a.TaskId).ToHashSet();
                    var available = catalog.Where(t => t.Project is null || !projects.Contains(t.Project) || ids.Contains(t.Id)).Select(t => t.Id).Concat(ids).Distinct().ToArray();
                    return new LearningStudent(p.Id, p.Login, p.GroupId, own.Count(a => a.CompletedAt is not null), available.Length,
                        own.Select(a => a.LastActivityAt).Max(), available);
                }).ToArray();
                return new LearningOverview(await db.Groups.Select(g => new GroupDto(g.Id, g.Name)).ToArrayAsync(), students, catalog, records, ServerState.Now);
            });
        });
        api.MapGet("/teacher/learning/{studentId}/{taskId}", (HttpContext ctx, string studentId, string taskId, int? offset) =>
        {
            state.Authorize(ctx, "teacher");
            if (offset is < 0 or > 1000000)
                throw new HttpFault(400, "Некорректная страница истории.");
            return state.Read(async db =>
            {
                var a = await db.Assignments.FindAsync(studentId, taskId) ?? throw new HttpFault(404, "Ученик ещё не открывал это задание.");
                var task = Catalog.Read(a.Json);
                var raw = (await db.Attempts.AsNoTracking().Where(e => e.StudentId == studentId && e.TaskId == taskId)
                    .Select(e => new { e.Id, e.StudentId, e.TaskId, e.Kind, e.Action, e.At }).ToArrayAsync())
                    .Select(e => new AttemptEntity { Id = e.Id, StudentId = e.StudentId, TaskId = e.TaskId, Kind = e.Kind, Action = e.Action, At = e.At }).ToArray();
                var events = raw.Select(e => new LearningEvent(e.Id, e.At, e.Action ?? (Violation(e) ? e.Kind : "check"), e.Kind, e.Code, e.Explanation)).ToList();
                if (a.StartedAt is { } start)
                    events.Add(new("opened", start, "opened", "", null, null));
                events.AddRange(Wire.Read<long[]>(a.HintsJson).Select((at, i) => new LearningEvent("hint-" + i, at, "hint", "Подсказка №" + (i + 1), null, null)));
                var page = events.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(offset ?? 0).Take(101).ToArray();
                var pageIds = page.Take(100).Select(e => e.Id).ToArray();
                var payloads = await db.Attempts.AsNoTracking().Where(e => e.StudentId == studentId && pageIds.Contains(e.Id)).Select(e => new { e.Id, e.Code, e.Explanation }).ToDictionaryAsync(e => e.Id);
                return new LearningTaskDetail(Record(a, raw), task.Prompt, a.Draft, a.AcceptedCode,
                    page.Take(100).Select(e => payloads.TryGetValue(e.Id, out var payload) ? e with { Code = payload.Code, Explanation = payload.Explanation } : e).ToArray(),
                    page.Length > 100, (offset ?? 0) + Math.Min(100, page.Length));
            });
        });
        api.MapPost("/tasks/{id}/events", (HttpContext ctx, string id, TaskEventRequest input) =>
        {
            var session = state.Authorize(ctx, "student");
            if (!Guid.TryParseExact(input.EventId, "N", out _) || input.Action is not ("opened" or "copy" or "cut" or "paste" or "clipboard" or "drop" or "capture"))
                throw new HttpFault(400, "Некорректное событие.");
            return state.Write(async db =>
            {
                var student = await db.Profiles.FindAsync(session.ProfileId) ?? throw new HttpFault(404, "Профиль не найден.");
                var a = await state.Assignment(db, student, id);
                var eventId = student.Id + ":" + input.EventId;
                var receipt = await db.Operations.FindAsync(student.Id, "event:" + input.EventId);
                var hash = id + ":" + input.Action;
                if (receipt is not null && receipt.RequestHash != hash)
                    throw new HttpFault(409, "Событие уже использовано.");
                if (receipt is not null)
                    return new { student.LockedUntil };
                db.Operations.Add(new() { StudentId = student.Id, Id = "event:" + input.EventId, RequestHash = hash, ResponseJson = "{}", At = ServerState.Now });
                if (input.Action == "opened")
                {
                    // An old assignment may already have an unrecorded start; do not invent it.
                    if (a.TrackingVersion > 0)
                        a.StartedAt ??= ServerState.Now;
                    a.LastActivityAt = ServerState.Now;
                }
                else if ((await db.Tasks.FindAsync(id))?.Restricted == true)
                {
                    db.Attempts.Add(new() { Id = eventId, StudentId = student.Id, TaskId = id, Action = input.Action, Kind = input.Action == "capture" ? "capture" : "clipboard", At = ServerState.Now });
                    a.LastActivityAt = ServerState.Now;
                    if (input.Action != "capture" && student.LockedUntil <= ServerState.Now)
                        student.LockedUntil = ServerState.Now + 30000;
                }
                return new { student.LockedUntil };
            });
        });
    }
}
