using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

internal static class LessonActivityFeed
{
    const int limit = 6;

    sealed record Event(string StudentId, string TaskId, string? Scope, string Subject,
        string Title, string Action, long At, string SourceId, int Priority);

    public static async Task<LessonActivity> Read(ClassroomDb db, string lessonId)
    {
        if (!await db.LessonSessions.AnyAsync(lesson => lesson.Id == lessonId))
            throw new HttpFault(404, "Занятие не найдено.");

        // An inherited credit has the old completion timestamp and adds no event to this lesson.
        var assignedSql = await db.LessonSqlWorks.AsNoTracking()
            .Where(w => w.LessonId == lessonId && (w.StartedAt != null || w.LastActivityAt != null ||
                w.CompletedAt != null && w.CompletedAt != w.PreviousCompletedAt))
            .Select(w => new
            {
                w.StudentId, w.TaskId, w.Json,
                Completed = w.CompletedAt != null && w.CompletedAt != w.PreviousCompletedAt,
                At = (w.CompletedAt != w.PreviousCompletedAt ? w.CompletedAt : null) ?? w.LastActivityAt ?? w.StartedAt ?? w.CreatedAt
            })
            .OrderByDescending(w => w.At).ThenByDescending(w => w.Completed).ThenBy(w => w.StudentId).ThenBy(w => w.TaskId)
            .Take(limit).ToArrayAsync();
        var practiceSql = await db.LessonSqlPractice.AsNoTracking().Where(w => w.LessonId == lessonId)
            .Select(w => new
            {
                w.StudentId, w.TaskId, w.Json, Completed = w.CompletedAt != null,
                At = w.CompletedAt ?? w.LastActivityAt ?? w.StartedAt ?? w.CreatedAt
            })
            .OrderByDescending(w => w.At).ThenByDescending(w => w.Completed).ThenBy(w => w.StudentId).ThenBy(w => w.TaskId)
            .Take(limit).ToArrayAsync();
        var sqlEvents = assignedSql.Select(w => SqlEvent(w.StudentId, w.TaskId, w.Json, null, w.Completed, w.At))
            .Concat(practiceSql.Select(w => SqlEvent(w.StudentId, w.TaskId, w.Json, "course", w.Completed, w.At)));

        var assignedSchemes =
            from work in db.ElectronicsAssignments.AsNoTracking()
            join task in db.LessonElectronicsTasks.AsNoTracking()
                on new { work.LessonId, work.TaskId } equals new { task.LessonId, task.TaskId }
            where work.LessonId == lessonId
            select new { work.StudentId, work.TaskId, task.Title, work.OpenedAt, AssignmentId = work.Id };
        var freeSchemes = db.ElectronicsPractice.AsNoTracking().Where(w => w.LessonId == lessonId);
        var openedAssigned = await assignedSchemes.OrderByDescending(w => w.OpenedAt)
            .ThenBy(w => w.StudentId).ThenBy(w => w.TaskId).Take(limit)
            .Select(w => new Event(w.StudentId, w.TaskId, null, "electronics", w.Title, "opened", w.OpenedAt, w.AssignmentId, 0))
            .ToArrayAsync();
        var openedFree = await freeSchemes
            .Select(w => new { w.StudentId, w.ProjectId, w.Name, Opened = w.OpenedAt != null, At = w.OpenedAt ?? w.FirstSavedAt ?? w.UpdatedAt })
            .OrderByDescending(w => w.At).ThenBy(w => w.StudentId).ThenBy(w => w.ProjectId).Take(limit)
            .Select(w => new Event(w.StudentId, w.ProjectId, "free-project", "electronics", w.Name, w.Opened ? "opened" : "saved", w.At, w.ProjectId, 0))
            .ToArrayAsync();

        var reports = db.ElectronicsReports.AsNoTracking().Where(r => r.Kind == "submitted");
        // Assigned reports carry an assignment ID. Free projects carry their lesson ID.
        var assignedSubmissions =
            from work in assignedSchemes
            join report in reports
                on new { work.StudentId, AssignmentId = (string?)work.AssignmentId } equals new { report.StudentId, report.AssignmentId }
            select new { work.StudentId, work.TaskId, Scope = (string?)null, work.Title, ReportId = report.Id, report.At };
        var freeSubmissions =
            from report in reports
            where report.LessonId == lessonId
            join work in freeSchemes
                on new { report.StudentId, ProjectId = report.FreeProjectId } equals new { work.StudentId, ProjectId = (string?)work.ProjectId }
            select new { report.StudentId, TaskId = work.ProjectId, Scope = (string?)"free-project", Title = report.Name, ReportId = report.Id, report.At };
        var submissions = assignedSubmissions.Concat(freeSubmissions);
        var submitted = await submissions.OrderByDescending(s => s.At)
            .ThenBy(s => s.StudentId).ThenBy(s => s.TaskId).ThenBy(s => s.ReportId).Take(limit)
            .Select(s => new Event(s.StudentId, s.TaskId, s.Scope, "electronics", s.Title, "submitted", s.At, s.ReportId, 2))
            .ToArrayAsync();
        var decisions = await (
            from submission in submissions
            join decision in db.ElectronicsReviews.AsNoTracking()
                on new { submission.StudentId, submission.ReportId } equals new { decision.StudentId, decision.ReportId }
            where decision.Action == "accepted" || decision.Action == "rejected" || decision.Action == "changes-requested"
            orderby decision.At descending, submission.StudentId, submission.TaskId, decision.Sequence descending, decision.Id
            select new Event(submission.StudentId, submission.TaskId, submission.Scope, "electronics", submission.Title,
                decision.Action, decision.At, decision.Id, 3)).Take(limit).ToArrayAsync();

        var recent = sqlEvents.Concat(openedAssigned).Concat(openedFree).Concat(submitted).Concat(decisions)
            .OrderByDescending(item => item.At).ThenByDescending(item => item.Priority)
            .ThenBy(item => item.StudentId, StringComparer.Ordinal).ThenBy(item => item.TaskId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceId, StringComparer.Ordinal).Take(limit).ToArray();
        if (recent.Length == 0) return new(lessonId, []);

        var studentIds = recent.Select(item => item.StudentId).Distinct().ToArray();
        var roster = await db.LessonRoster.AsNoTracking().Where(r => r.LessonId == lessonId && studentIds.Contains(r.StudentId))
            .ToDictionaryAsync(r => r.StudentId);
        var missing = studentIds.Where(id => !roster.ContainsKey(id)).ToArray();
        var older = await (
            from participant in db.LessonParticipants.AsNoTracking()
            join profile in db.Profiles.AsNoTracking() on participant.StudentId equals profile.Id
            where participant.SessionId == lessonId && missing.Contains(participant.StudentId)
            select new { participant.StudentId, Name = profile.FullName == null || profile.FullName.Trim() == ""
                ? profile.Login : profile.FullName, Group = participant.GroupNameAtEntry })
            .ToDictionaryAsync(r => r.StudentId);
        var items = new List<LessonActivityItem>(recent.Length);
        foreach (var item in recent)
        {
            var name = roster.GetValueOrDefault(item.StudentId)?.StudentName ?? older.GetValueOrDefault(item.StudentId)?.Name;
            if (name is null) continue;
            var group = roster.GetValueOrDefault(item.StudentId)?.GroupName ?? older[item.StudentId].Group;
            items.Add(new(item.StudentId, name, group, item.Subject, item.TaskId, item.Scope,
                item.Title, item.Action, item.At));
        }
        return new(lessonId, items.ToArray());
    }

    static Event SqlEvent(string studentId, string taskId, string json, string? scope, bool completed, long at) =>
        new(studentId, taskId, scope, "sql", Catalog.Read(json).Title, completed ? "solved" : "working", at,
            scope ?? "", completed ? 2 : 1);
}
