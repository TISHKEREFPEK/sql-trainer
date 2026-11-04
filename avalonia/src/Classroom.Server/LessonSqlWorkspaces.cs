using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class LessonSqlWorkspaces
{
    sealed record PreviousGroup(ISqlWork[] Work, string? LessonId, long At)
    {
        public bool Completed => Work.Length > 0 && Work.All(w => w.CompletedAt is not null);
    }
    sealed record Source(string Id, string Json, int Revision, int Order, ISqlWork? Previous);

    static async Task<PreviousGroup?> Previous(ClassroomDb db, ProfileEntity student, string lessonId, string taskId, string? project)
    {
        var allCourse = await db.Assignments.Where(a => a.StudentId == student.Id).ToArrayAsync();
        var candidateProjects = (await db.LessonSqlWorks.Where(w => w.StudentId == student.Id && w.LessonId != lessonId && w.TaskId == taskId)
            .Select(w => w.Project).Distinct().ToArrayAsync()).Concat(allCourse.Where(w => w.TaskId == taskId).Select(w => Catalog.Read(w.Json).Project))
            .Append(project).Where(p => p is not null).Cast<string>().Distinct().ToArray();
        var course = allCourse.Where(w => w.TaskId == taskId || Catalog.Read(w.Json).Project is { } p && candidateProjects.Contains(p)).ToArray();
        var older = await db.LessonSqlWorks.Where(w => w.StudentId == student.Id && w.LessonId != lessonId &&
            (w.TaskId == taskId || w.Project != null && candidateProjects.Contains(w.Project))).ToArrayAsync();
        var groups = older.GroupBy(w => new { w.LessonId, w.Project }).Select(g => new PreviousGroup(g.Cast<ISqlWork>().ToArray(), g.Key.LessonId,
            g.Max(w => Math.Max(w.LastActivityAt ?? w.CreatedAt, w.CompletedAt ?? 0)))).ToList();
        groups.AddRange(course.GroupBy(w => Catalog.Read(w.Json).Project ?? "task:" + w.TaskId).Select(g =>
            new PreviousGroup(g.Cast<ISqlWork>().ToArray(), null, g.Max(w => Math.Max(w.LastActivityAt ?? 0, w.CompletedAt ?? 0)))));
        return groups.OrderByDescending(g => g.Work.Any(w => w.TaskId == taskId)).ThenByDescending(g => g.Completed)
            .ThenByDescending(g => g.Completed ? g.Work.Max(w => w.CompletedAt ?? 0) : g.At).ThenBy(g => g.LessonId).FirstOrDefault();
    }
    public static async Task<ISqlWork[]> Preview(ClassroomDb db, ProfileEntity student, string lessonId, LessonSqlTaskEntity policy)
    {
        var project = Catalog.Read(policy.Json).Project;
        var current = await db.LessonSqlWorks.Where(w => w.LessonId == lessonId && w.StudentId == student.Id &&
            (project == null ? w.TaskId == policy.TaskId : w.Project == project)).ToArrayAsync();
        if (current.Length > 0)
        {
            if (current.FirstOrDefault(w => w.TaskId == policy.TaskId)?.Project is { } actual && actual != project)
                return await db.LessonSqlWorks.Where(w => w.LessonId == lessonId && w.StudentId == student.Id && w.Project == actual).ToArrayAsync();
            return current;
        }
        if (policy.RequireNewSolution) return [];
        return (await Previous(db, student, lessonId, policy.TaskId, project))?.Work ?? [];
    }

    public static async Task<LessonSqlTaskEntity?> Binding(ClassroomDb db, ProfileEntity student, string lessonId, string taskId, bool retainedSave)
    {
        if (retainedSave && await db.LessonSqlWorks.FindAsync(lessonId, student.Id, taskId) is { } saved)
            return await db.LessonSqlTasks.FindAsync(lessonId, saved.PolicyTaskId);
        var issued = await LessonSqlTasks.Issued(db, lessonId);
        if (issued.SingleOrDefault(t => t.TaskId == taskId) is { } direct) return direct;
        var old = await db.Assignments.FindAsync(student.Id, taskId);
        if (old is not null && Catalog.Read(old.Json).Project is { } project && issued.FirstOrDefault(t => !t.RequireNewSolution && Catalog.Read(t.Json).Project == project) is { } sameProject)
            return sameProject;
        foreach (var policy in issued.Where(t => !t.RequireNewSolution))
            if ((await Preview(db, student, lessonId, policy)).Any(w => w.TaskId == taskId)) return policy;
        return null;
    }

    public static async Task<LessonSqlWorkEntity> Open(ServerState state, ClassroomDb db, ProfileEntity student,
        string lessonId, string taskId, LessonSqlTaskEntity policy)
    {
        if (await db.LessonSqlWorks.FindAsync(lessonId, student.Id, taskId) is { } saved)
        {
            if (saved.RequireNewSolution != policy.RequireNewSolution) throw new HttpFault(409, "Работа уже открыта в другой редакции выдачи. Прежний черновик сохранён.");
            return saved;
        }
        var definition = Catalog.Read(policy.Json); var project = definition.Project;
        var previous = await Previous(db, student, lessonId, taskId, project);
        if (previous is null)
        {
            await state.LegacyAssignment(db, student, policy.TaskId, lessonId);
            previous = await Previous(db, student, lessonId, taskId, project);
        }
        // Reuse an actually solved earlier edition before an unfinished retry of another edition.
        // A whole project always comes from one scope and its matching database snapshot.
        var sources = new List<Source>();
        if (!policy.RequireNewSolution && previous is not null)
        {
            project = Catalog.Read(previous.Work.FirstOrDefault(w => w.TaskId == taskId)?.Json ?? previous.Work[0].Json).Project;
            sources.AddRange(previous.Work.OrderBy(w => w.ProjectOrder).Select(w => new Source(w.TaskId, w.Json, w.Revision, w.ProjectOrder, w)));
            if (!sources.Any(s => s.Id == taskId)) throw new HttpFault(409, "Этот шаг не входит в прежнюю редакцию проекта ученика.");
        }
        else
        {
            var issued = (await LessonSqlTasks.Issued(db, lessonId)).Where(t => project is null ? t.TaskId == taskId : Catalog.Read(t.Json).Project == project).ToArray();
            int? pinned = previous?.Work.Select(w => Catalog.Read(w.Json).VariantIndex).FirstOrDefault(i => i is not null);
            var order = 0;
            foreach (var row in issued)
            {
                var original = Catalog.Read(row.Json);
                var alternatives = Wire.Read<string[]>(row.AlternativesJson).Select(Catalog.Read).ToArray();
                var generated = Variants.Assign(original, student.Slot, alternatives);
                var index = pinned ?? generated.VariantIndex ?? 1;
                if (index > alternatives.Length + 1 || index < 1) index = 1;
                var assigned = (index == 1 ? original : alternatives[index - 2]) with { VariantIndex = index };
                pinned ??= project is null ? null : index;
                sources.Add(new(row.TaskId, Catalog.Write(assigned), row.Revision, order++, previous?.Work.FirstOrDefault(w => w.TaskId == row.TaskId)));
            }
        }
        var policies = await db.LessonSqlTasks.Where(t => t.LessonId == lessonId).ToArrayAsync();
        foreach (var source in sources)
        {
            if (await db.LessonSqlWorks.FindAsync(lessonId, student.Id, source.Id) is not null) continue;
            var prior = source.Previous;
            var mode = policy.RequireNewSolution;
            db.LessonSqlWorks.Add(new()
            {
                LessonId = lessonId, StudentId = student.Id, TaskId = source.Id,
                Project = project,
                PolicyTaskId = policies.Any(p => p.TaskId == source.Id && p.RequireNewSolution == mode) ? source.Id : policy.TaskId,
                RequireNewSolution = mode, CreatedAt = ServerState.Now,
                Json = source.Json, Revision = source.Revision, ProjectOrder = source.Order,
                PreviousJson = prior?.Json, PreviousAcceptedCode = prior?.AcceptedCode, PreviousCompletedAt = prior?.CompletedAt,
                Draft = mode ? "" : prior?.Draft ?? "", HintsJson = mode ? "[]" : prior?.HintsJson ?? "[]",
                AcceptedCode = mode ? null : prior?.AcceptedCode, CompletedAt = mode ? null : prior?.CompletedAt
            });
        }
        if (!policy.RequireNewSolution && project is not null && previous is not null &&
            await db.LessonSqlSnapshots.FindAsync(lessonId, student.Id, project) is null)
        {
            var snapshot = previous.LessonId is { } previousId
                ? (await db.LessonSqlSnapshots.FindAsync(previousId, student.Id, project))?.Base64
                : (await db.Snapshots.FindAsync(student.Id, project))?.Base64;
            if (snapshot is not null) db.LessonSqlSnapshots.Add(new() { LessonId = lessonId, StudentId = student.Id, Project = project, Base64 = snapshot });
        }
        await db.SaveChangesAsync();
        return await db.LessonSqlWorks.FindAsync(lessonId, student.Id, taskId) ?? throw new HttpFault(404, "Работа занятия не найдена.");
    }

    public static async Task<ISqlWork[]> Chain(ClassroomDb db, ISqlWork work, string project)
    {
        var rows = work is LessonSqlWorkEntity lesson
            ? (await db.LessonSqlWorks.Where(w => w.LessonId == lesson.LessonId && w.StudentId == work.StudentId).ToArrayAsync()).Cast<ISqlWork>()
            : (await db.Assignments.Where(w => w.StudentId == work.StudentId).ToArrayAsync()).Cast<ISqlWork>();
        return rows.Where(w => Catalog.Read(w.Json).Project == project).ToArray();
    }
    public static async Task<string?> Snapshot(ClassroomDb db, ISqlWork work, string project) => work is LessonSqlWorkEntity lesson
        ? (await db.LessonSqlSnapshots.FindAsync(lesson.LessonId, work.StudentId, project))?.Base64
        : (await db.Snapshots.FindAsync(work.StudentId, project))?.Base64;

    // Course completion is promoted only into the same immutable edition and only once.
    // A solved previous answer and its project database are never overwritten by a retry.
    public static async Task<bool> Promote(ClassroomDb db, ISqlWork work)
    {
        if (work is not LessonSqlWorkEntity || work.CompletedAt is null) return false;
        var course = await db.Assignments.FindAsync(work.StudentId, work.TaskId);
        if (course is null || course.CompletedAt is not null || Catalog.Write(Catalog.Read(course.Json)) != Catalog.Write(Catalog.Read(work.Json))) return false;
        course.CompletedAt = work.CompletedAt; course.AcceptedCode = work.AcceptedCode;
        course.ActiveSeconds += work.ActiveSeconds; course.ActiveSecondsAtCompletion = course.ActiveSeconds;
        course.Checks += work.Checks; course.Errors += work.Errors;
        if (course.TrackingVersion > 0) course.StartedAt ??= work.StartedAt;
        course.LastActivityAt = work.LastActivityAt;
        return true;
    }
    public static async Task SaveSnapshot(ClassroomDb db, ISqlWork work, string project, string base64, bool promoted)
    {
        if (work is LessonSqlWorkEntity lesson)
        {
            var row = await db.LessonSqlSnapshots.FindAsync(lesson.LessonId, work.StudentId, project);
            if (row is null) db.LessonSqlSnapshots.Add(new() { LessonId = lesson.LessonId, StudentId = work.StudentId, Project = project, Base64 = base64 });
            else row.Base64 = base64;
            if (!promoted) return;
            var chain = await Chain(db, work, project);
            var courseChain = (await db.Assignments.Where(a => a.StudentId == work.StudentId).ToArrayAsync()).Where(a => Catalog.Read(a.Json).Project == project).ToArray();
            if (chain.Length != courseChain.Length || chain.Any(w => !courseChain.Any(c => c.TaskId == w.TaskId && c.Json == w.Json))) return;
        }
        var courseSnapshot = await db.Snapshots.FindAsync(work.StudentId, project);
        if (courseSnapshot is null) db.Snapshots.Add(new() { StudentId = work.StudentId, Project = project, Base64 = base64 });
        else courseSnapshot.Base64 = base64;
    }
}
