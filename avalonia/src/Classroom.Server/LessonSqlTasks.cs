using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class LessonSqlTasks
{
    public sealed record StudentCatalogue(CatalogueItem[] Tasks, string[] Required, Dictionary<string, ISqlWork> Work, string[] RequireNew);
    public static async Task<LessonSqlTaskEntity[]> Issued(ClassroomDb db, string lessonId) => await db.LessonSqlTasks
        .Where(t => t.LessonId == lessonId && !t.Withdrawn).OrderBy(t => t.Position).ThenBy(t => t.TaskId).ToArrayAsync();
    static LessonSqlTaskInfo Info(LessonSqlTaskEntity row)
    {
        var task = Catalog.Read(row.Json);
        return new(row.TaskId, task.Title, task.Module, task.Project, row.Revision, row.IssuedAt, row.RequireNewSolution);
    }
    public static async Task<StudentCatalogue> Catalogue(ServerState state, ClassroomDb db, ProfileEntity student, LessonSessionEntity lesson)
    {
        var assigned = await db.Assignments.Where(a => a.StudentId == student.Id).ToArrayAsync();
        var assignedIds = assigned.Select(a => a.TaskId).ToHashSet();
        var assignedProjects = assigned.Select(a => Catalog.Read(a.Json).Project).Where(p => p is not null).ToHashSet();
        var items = new Dictionary<string, CatalogueItem>(); var required = new HashSet<string>();
        var works = new Dictionary<string, ISqlWork>(); var requireNew = new HashSet<string>(); var processed = new HashSet<string>();
        var issuedRows = await Issued(db, lesson.Id);
        foreach (var issued in issuedRows)
        {
            var task = Catalog.Read(issued.Json);
            var key = task.Project is { } projectId ? "project:" + projectId : "task:" + task.Id;
            if (!processed.Add(key)) continue;
            var preview = await LessonSqlWorkspaces.Preview(db, student, lesson.Id, issued);
            if (preview.Length > 0)
            {
                foreach (var old in preview.OrderBy(a => a.ProjectOrder))
                {
                    var definition = Catalog.Read(old.Json); required.Add(old.TaskId);
                    items[old.TaskId] = new(definition.Id, definition.Title, definition.Module, definition.Project);
                    works[old.TaskId] = old;
                    if (issued.RequireNewSolution) requireNew.Add(old.TaskId);
                }
            }
            else
            {
                foreach (var row in issuedRows.Where(r => task.Project is null ? r.TaskId == issued.TaskId : Catalog.Read(r.Json).Project == task.Project))
                {
                    var definition = Catalog.Read(row.Json);
                    required.Add(definition.Id); items[definition.Id] = new(definition.Id, definition.Title, definition.Module, definition.Project);
                    if (issued.RequireNewSolution) requireNew.Add(definition.Id);
                }
            }
        }
        if (lesson.FreePractice)
        {
            foreach (var item in (await state.Tasks(db)).Select(ServerState.Item).Where(t => t.Project is null || !assignedProjects.Contains(t.Project) || assignedIds.Contains(t.Id)))
                items.TryAdd(item.Id, item);
            foreach (var old in assigned)
            {
                if (required.Contains(old.TaskId)) continue;
                var task = Catalog.Read(old.Json); items[task.Id] = new(task.Id, task.Title, task.Module, task.Project);
                works[old.TaskId] = old;
            }
        }
        return new(items.Values.ToArray(), required.ToArray(), works, requireNew.ToArray());
    }
    public static async Task RequireTask(ClassroomDb db, ProfileEntity student, string lessonId, string taskId, bool retainedSave = false)
    {
        var lesson = await db.LessonSessions.FindAsync(lessonId) ?? throw new HttpFault(423, "Занятие не найдено.");
        var saved = await db.Assignments.FindAsync(student.Id, taskId);
        if (retainedSave && (saved is not null || await db.LessonSqlWorks.AnyAsync(w => w.LessonId == lessonId && w.StudentId == student.Id && w.TaskId == taskId))) return;
        var issued = await Issued(db, lessonId);
        if (issued.Any(t => t.TaskId == taskId)) return;
        if (await LessonSqlWorkspaces.Binding(db, student, lessonId, taskId, retainedSave) is not null) return;
        if (lesson.FreePractice && (saved is not null || await db.Tasks.AnyAsync(t => t.Id == taskId && !t.Deleted))) return;
        throw new HttpFault(403, "Это задание не выдано на занятие. Свободная практика закрыта.");
    }
    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapGet("/teacher/lessons/{lessonId}/students/{studentId}/sql/{taskId}", (HttpContext ctx, string lessonId, string studentId, string taskId) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(async db =>
            {
                var row = await db.LessonSqlWorks.FindAsync(lessonId, studentId, taskId) ?? throw new HttpFault(404, "Ученик ещё не открывал эту работу занятия.");
                var task = Catalog.Read(row.Json); var previous = row.PreviousJson is null ? null : Catalog.Read(row.PreviousJson);
                return new LessonSqlWorkDetail(lessonId, studentId, taskId, task.Title, task.Prompt, task.VariantIndex ?? 1, row.Revision,
                    row.RequireNewSolution, row.Draft, row.AcceptedCode, row.CompletedAt, row.CreatedAt, row.StartedAt, row.Checks, row.Errors, row.ActiveSeconds,
                    previous?.Title, previous?.Prompt, row.PreviousAcceptedCode, row.PreviousCompletedAt);
            });
        });
        api.MapGet("/student/home", async (HttpContext ctx) =>
        {
            var session = state.Authorize(ctx, "student");
            return await state.Read(async db =>
            {
                var lesson = await LessonRoutes.RequireMember(state, db, session, saving: true);
                var student = await db.Profiles.FindAsync(session.ProfileId) ?? throw new HttpFault(401, "Войдите в приложение.");
                var available = await Catalogue(state, db, student, lesson);
                var requiredIds = available.Required.ToHashSet();
                var work = available.Tasks.Where(t => requiredIds.Contains(t.Id)).Select(t =>
                {
                    available.Work.TryGetValue(t.Id, out var row);
                    var definition = row is null ? null : Catalog.Read(row.Json);
                    var prior = row is LessonSqlWorkEntity scoped ? scoped.PreviousCompletedAt : row?.CompletedAt;
                    var status = row?.CompletedAt is { } at ? row is LessonSqlWorkEntity current && current.LessonId == lesson.Id && (current.RequireNewSolution || at != current.PreviousCompletedAt) ? "completed" : "completed-before"
                        : row is not null && (!string.IsNullOrWhiteSpace(row.Draft) || row.StartedAt is not null || row.Checks > 0) ? "in-progress" : "not-started";
                    return new StudentLessonWork(t.Id, t.Title, t.Module, status, definition?.VariantIndex, row?.Revision, row?.CompletedAt, available.RequireNew.Contains(t.Id), prior);
                }).ToArray();
                return new StudentHomeDto(student.FullName ?? student.Login,
                    student.GroupId is null ? "Без группы" : (await db.Groups.FindAsync(student.GroupId))?.Name ?? "Без группы",
                    await LessonRoutes.View(state, db, lesson), work);
            });
        });
        api.MapGet("/teacher/lessons/{id}/sql-tasks", (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(async db =>
            {
                if (!await db.LessonSessions.AnyAsync(s => s.Id == id)) throw new HttpFault(404, "Занятие не найдено.");
                return new LessonSqlSelection((await Issued(db, id)).Select(Info).ToArray());
            });
        });
        api.MapPut("/teacher/lessons/{id}/sql-tasks", (HttpContext ctx, string id, LessonSqlSelectionRequest input) =>
        {
            state.Authorize(ctx, "teacher");
            if (input.TaskIds.Length > 1000 || input.TaskIds.Any(string.IsNullOrWhiteSpace) || input.TaskIds.Distinct().Count() != input.TaskIds.Length)
                throw new HttpFault(400, "Проверьте список заданий: не более 1000, без повторов.");
            if (input.RequireNewTaskIds is { } modes && (modes.Length > 1000 || modes.Distinct().Count() != modes.Length || modes.Any(id => !input.TaskIds.Contains(id))))
                throw new HttpFault(400, "Режим нового решения задаётся только для выбранных заданий, без повторов.");
            return state.Write(async db =>
            {
                var lesson = await db.LessonSessions.FindAsync(id) ?? throw new HttpFault(404, "Занятие не найдено.");
                if (lesson.EndedAt is not null) throw new HttpFault(409, "Завершённое занятие менять нельзя.");
                var existing = await db.LessonSqlTasks.Where(t => t.LessonId == id).ToArrayAsync();
                var catalog = await state.Tasks(db);
                var requested = input.TaskIds.ToHashSet();
                if (requested.Any(taskId => !catalog.Any(t => t.Id == taskId) && !existing.Any(t => t.TaskId == taskId)))
                    throw new HttpFault(404, "Задание не найдено в каталоге или прежней выдаче.");
                // A project is issued as a complete compatible chain, never as an orphaned last step.
                var projects = requested.Select(taskId => Catalog.Read(existing.SingleOrDefault(t => t.TaskId == taskId)?.Json ?? catalog.Single(t => t.Id == taskId).Json).Project).Where(p => p is not null).ToHashSet();
                var existingProjects = existing.Select(t => Catalog.Read(t.Json).Project).Where(p => p is not null).ToHashSet();
                if (catalog.Any(t => requested.Contains(t.Id) && !existing.Any(e => e.TaskId == t.Id) && Catalog.Read(t.Json).Project is { } project && existingProjects.Contains(project)))
                    throw new HttpFault(409, "Занятие уже использует другую редакцию этого проекта. Новые шаги выдайте на новом занятии.");
                foreach (var task in catalog)
                    if (Catalog.Read(task.Json).Project is { } project && projects.Contains(project) && !existingProjects.Contains(project)) requested.Add(task.Id);
                foreach (var task in existing)
                    if (Catalog.Read(task.Json).Project is { } project && projects.Contains(project)) requested.Add(task.TaskId);
                bool RequiresNew(string taskId)
                {
                    if (input.RequireNewTaskIds is null) return existing.FirstOrDefault(t => t.TaskId == taskId)?.RequireNewSolution ?? false;
                    var json = existing.FirstOrDefault(t => t.TaskId == taskId)?.Json ?? catalog.Single(t => t.Id == taskId).Json;
                    var project = Catalog.Read(json).Project;
                    return input.RequireNewTaskIds.Contains(taskId) || project is not null && input.RequireNewTaskIds.Any(modeId =>
                        Catalog.Read(existing.FirstOrDefault(t => t.TaskId == modeId)?.Json ?? catalog.Single(t => t.Id == modeId).Json).Project == project);
                }
                var changingModes = existing.Where(t => requested.Contains(t.TaskId) && t.RequireNewSolution != RequiresNew(t.TaskId)).Select(t => t.TaskId).ToArray();
                if (await db.LessonSqlWorks.AnyAsync(w => w.LessonId == id && changingModes.Contains(w.PolicyTaskId)))
                    throw new HttpFault(409, "Ученики уже начали эту работу. Режим повторного решения можно изменить на новом занятии.");
                foreach (var task in existing) task.Withdrawn = !requested.Contains(task.TaskId);
                var position = 0;
                foreach (var taskId in existing.OrderBy(t => t.Position).Select(t => t.TaskId).Concat(catalog.Select(t => t.Id)).Distinct().Where(requested.Contains))
                {
                    var row = existing.SingleOrDefault(t => t.TaskId == taskId);
                    if (row is null)
                    {
                        var task = catalog.Single(t => t.Id == taskId);
                        var alternatives = Variants.Create(Catalog.Read(task.Json));
                        foreach (var variant in await db.Variants.Where(v => v.TaskId == taskId).ToArrayAsync())
                            if (variant.Index >= 2 && variant.Index - 2 < alternatives.Length) alternatives[variant.Index - 2] = Catalog.Read(variant.Json);
                        row = new() { LessonId = id, TaskId = taskId, Json = task.Json, Revision = task.Revision,
                            AlternativesJson = Wire.Write(alternatives.Select(Catalog.Write).ToArray()), IssuedAt = ServerState.Now };
                        db.LessonSqlTasks.Add(row);
                    }
                    row.RequireNewSolution = RequiresNew(taskId); row.Withdrawn = false; row.Position = position++;
                }
                // Persist before querying so newly issued work is included in the response.
                await db.SaveChangesAsync();
                return new LessonSqlSelection((await Issued(db, id)).Select(Info).ToArray());
            });
        });
    }
}
