using Classroom.Contracts;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class ElectronicsAssignments
{
    static string Id(string value) => Guid.TryParseExact(value, "N", out _) ? value : throw new HttpFault(400, "Некорректный номер электронного задания.");
    public static void Validate(string title, string prompt, string? starter)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl) || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32000)
            throw new InvalidDataException("Название задания: до 120 символов; условие: от 1 до 32000 символов.");
        if (starter is not null) ElectronicsProjectFormat.Inspect(starter);
    }
    static ElectronicsTaskDto TaskDto(ElectronicsTaskEntity row) => new(row.Id, row.Title, row.Prompt, row.StarterJson, row.Restricted, row.Revision, row.UpdatedAt);
    public static async Task<LessonElectronicsTaskEntity[]> Issued(ClassroomDb db, string lessonId) => await db.LessonElectronicsTasks
        .Where(t => t.LessonId == lessonId && !t.Withdrawn).OrderBy(t => t.Position).ThenBy(t => t.TaskId).ToArrayAsync();
    static ElectronicsLessonTask Info(LessonElectronicsTaskEntity row) => new(row.TaskId, row.Title, row.Prompt, row.Restricted, row.Revision, row.IssuedAt);
    public static async Task<ElectronicsAssignmentEntity> Own(ClassroomDb db, Session session, string assignmentId)
    {
        var row = await db.ElectronicsAssignments.SingleOrDefaultAsync(a => a.Id == assignmentId && a.StudentId == session.ProfileId)
            ?? throw new HttpFault(404, "Электронная работа не найдена.");
        if (!await db.LessonParticipants.AnyAsync(p => p.SessionId == row.LessonId && p.StudentId == session.ProfileId))
            throw new HttpFault(403, "Работа не относится к занятиям ученика.");
        return row;
    }
    static async Task<ElectronicsAssignmentDto> Dto(ClassroomDb db, ElectronicsAssignmentEntity assignment)
    {
        var issued = await db.LessonElectronicsTasks.FindAsync(assignment.LessonId, assignment.TaskId) ?? throw new HttpFault(404, "Выдача не найдена.");
        var latest = await db.ElectronicsWorks.Where(w => w.AssignmentId == assignment.Id && w.StudentId == assignment.StudentId)
            .OrderByDescending(w => w.UpdatedAt).ThenBy(w => w.ClientId).Select(w => w.ProjectJson).FirstOrDefaultAsync();
        return new(assignment.Id, assignment.LessonId, assignment.TaskId, issued.Title, issued.Prompt, issued.Revision, issued.Restricted,
            latest ?? assignment.InitialProjectJson, assignment.State, assignment.CanSubmit, !issued.Withdrawn, assignment.StateVersion);
    }
    public static async Task<StudentElectronicsWork[]> Home(ClassroomDb db, Session session)
    {
        if (session.LessonId is null) return [];
        var assignments = await db.ElectronicsAssignments.Where(a => a.LessonId == session.LessonId && a.StudentId == session.ProfileId).ToDictionaryAsync(a => a.TaskId);
        return (await Issued(db, session.LessonId)).Select(t => new StudentElectronicsWork(t.TaskId, t.Title,
            assignments.GetValueOrDefault(t.TaskId)?.State ?? "not-started", assignments.GetValueOrDefault(t.TaskId)?.Id, t.Revision)).ToArray();
    }
    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapGet("/teacher/electronics/tasks", async (HttpContext ctx) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Read(db => db.ElectronicsTasks.Where(t => !t.Deleted).OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
                .Select(t => new ElectronicsTaskSummary(t.Id, t.Title, t.Restricted, t.Revision, t.UpdatedAt)).ToArrayAsync());
        });
        api.MapGet("/teacher/electronics/tasks/{id}", (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(async db => TaskDto(await db.ElectronicsTasks.FindAsync(id) ?? throw new HttpFault(404, "Задание не найдено.")));
        });
        api.MapPut("/teacher/electronics/tasks/{id}", (HttpContext ctx, string id, ElectronicsTaskRequest input) =>
        {
            state.Authorize(ctx, "teacher"); Id(id); Validate(input.Title, input.Prompt, input.StarterJson);
            return state.Write(async db =>
            {
                var row = await db.ElectronicsTasks.FindAsync(id);
                if (row is not null)
                {
                    if (row.Deleted) throw new HttpFault(409, "Задание удалено из каталога. Создайте новое с другим номером.");
                    if (row.Title == input.Title.Trim() && row.Prompt == input.Prompt && row.StarterJson == input.StarterJson && row.Restricted == input.Restricted) return TaskDto(row);
                    if (row.Revision != input.ExpectedRevision) throw new HttpFault(409, "Задание уже изменено. Откройте последнюю редакцию.");
                    row.Revision++;
                }
                else
                {
                    if (input.ExpectedRevision != 0) throw new HttpFault(409, "Исходная редакция не найдена.");
                    row = new() { Id = id, CreatedAt = ServerState.Now }; db.ElectronicsTasks.Add(row);
                }
                row.Title = input.Title.Trim(); row.Prompt = input.Prompt; row.StarterJson = input.StarterJson;
                row.Restricted = input.Restricted; row.UpdatedAt = ServerState.Now;
                db.ElectronicsTaskRevisions.Add(new() { TaskId = id, Revision = row.Revision, Title = row.Title, Prompt = row.Prompt,
                    StarterJson = row.StarterJson, Restricted = row.Restricted, At = row.UpdatedAt });
                return TaskDto(row);
            });
        });
        api.MapGet("/teacher/electronics/tasks/{id}/revisions", (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(async db => await db.ElectronicsTaskRevisions.Where(r => r.TaskId == id).OrderByDescending(r => r.Revision)
                .Select(r => new ElectronicsTaskDto(r.TaskId, r.Title, r.Prompt, r.StarterJson, r.Restricted, r.Revision, r.At)).ToArrayAsync());
        });
        api.MapDelete("/teacher/electronics/tasks/{id}", (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Write(async db =>
            {
                var row = await db.ElectronicsTasks.FindAsync(id) ?? throw new HttpFault(404, "Задание не найдено.");
                row.Deleted = true; return new { ok = true };
            });
        });
        api.MapGet("/teacher/lessons/{id}/electronics-tasks", (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(async db => new ElectronicsSelection((await Issued(db, id)).Select(Info).ToArray()));
        });
        api.MapPut("/teacher/lessons/{id}/electronics-tasks", (HttpContext ctx, string id, ElectronicsSelectionRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            if (input.TaskIds.Length > 1000 || input.TaskIds.Any(string.IsNullOrWhiteSpace) || input.TaskIds.Distinct().Count() != input.TaskIds.Length)
                throw new HttpFault(400, "Проверьте выбор электронных заданий: не более 1000, без повторов.");
            return state.Write(async db =>
            {
                var lesson = await db.LessonSessions.FindAsync(id) ?? throw new HttpFault(404, "Занятие не найдено.");
                if (lesson.EndedAt is not null) throw new HttpFault(409, "Занятие завершено.");
                var prior = await db.LessonElectronicsTasks.Where(t => t.LessonId == id).ToArrayAsync();
                var tasks = await db.ElectronicsTasks.Where(t => !t.Deleted).ToArrayAsync();
                if (input.TaskIds.Any(taskId => !prior.Any(t => t.TaskId == taskId) && !tasks.Any(t => t.Id == taskId))) throw new HttpFault(404, "Задание не найдено.");
                foreach (var row in prior) row.Withdrawn = !input.TaskIds.Contains(row.TaskId);
                for (var position = 0; position < input.TaskIds.Length; position++)
                {
                    var taskId = input.TaskIds[position]; var row = prior.SingleOrDefault(t => t.TaskId == taskId);
                    if (row is null)
                    {
                        var source = tasks.Single(t => t.Id == taskId);
                        row = new() { LessonId = id, TaskId = taskId, Title = source.Title, Prompt = source.Prompt, StarterJson = source.StarterJson,
                            Restricted = source.Restricted, Revision = source.Revision, IssuedAt = ServerState.Now };
                        db.LessonElectronicsTasks.Add(row);
                    }
                    row.Position = position; row.Withdrawn = false;
                }
                await db.SaveChangesAsync(); return new ElectronicsSelection((await Issued(db, id)).Select(Info).ToArray());
            });
        });
        api.MapGet("/electronics/tasks/{id}", (HttpContext ctx, string id) =>
        {
            var session = state.Authorize(ctx, "student");
            return state.Write(async db =>
            {
                await LessonRoutes.RequireWork(state, db, session, "electronics");
                var issued = await db.LessonElectronicsTasks.FindAsync(session.LessonId!, id);
                if (issued is null || issued.Withdrawn) throw new HttpFault(403, "Это электронное задание не выдано на занятие.");
                var assignment = await db.ElectronicsAssignments.SingleOrDefaultAsync(a => a.LessonId == session.LessonId && a.StudentId == session.ProfileId && a.TaskId == id);
                if (assignment is null)
                {
                    var empty = Wire.Write(new { version = 1, name = issued.Title, code = "", parts = Array.Empty<object>(), wires = Array.Empty<object>() });
                    assignment = new() { Id = Guid.NewGuid().ToString("N"), LessonId = session.LessonId!, StudentId = session.ProfileId, TaskId = id,
                        InitialProjectJson = issued.StarterJson ?? empty, OpenedAt = ServerState.Now };
                    db.ElectronicsAssignments.Add(assignment);
                }
                return await Dto(db, assignment);
            });
        });
        api.MapGet("/electronics/assignments/{id}", (HttpContext ctx, string id) =>
        {
            var session = state.Authorize(ctx, "student");
            return state.Read(async db => await Dto(db, await Own(db, session, id)));
        });
        api.MapGet("/electronics/assignments/{id}/status", (HttpContext ctx, string id) =>
        {
            var session = state.Authorize(ctx, "student");
            return state.Read(async db =>
            {
                var assignment = await Own(db, session, id);
                var issued = await db.LessonElectronicsTasks.FindAsync(assignment.LessonId, assignment.TaskId) ?? throw new HttpFault(404, "Выдача не найдена.");
                return new ElectronicsAssignmentStatus(id, assignment.State, assignment.CanSubmit, !issued.Withdrawn, assignment.StateVersion, await ElectronicsReviews.Latest(db, id));
            });
        });
    }
}
