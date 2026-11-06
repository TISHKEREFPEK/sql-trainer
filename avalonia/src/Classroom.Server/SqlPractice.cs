using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class SqlPractice
{
    public static async Task<LessonSqlPracticeEntity?> Observe(ClassroomDb db, ISqlWork work, string? lessonId, bool opened = false, string? draft = null)
    {
        if (work is not AssignmentEntity || lessonId is null) return null;
        var row = db.LessonSqlPractice.Local.SingleOrDefault(r => r.LessonId == lessonId && r.StudentId == work.StudentId && r.TaskId == work.TaskId)
            ?? await db.LessonSqlPractice.FindAsync(lessonId, work.StudentId, work.TaskId);
        if (row is null)
        {
            var previous = await db.Attempts.Where(a => a.LessonId == lessonId && a.StudentId == work.StudentId && a.TaskId == work.TaskId && (a.WorkScope == "course" || a.WorkScope == null))
                .OrderBy(a => a.At).ThenBy(a => a.Id).ToArrayAsync();
            var correct = previous.FirstOrDefault(a => a.Kind == "correct" && (a.Action == "check" || a.Action == null));
            var lesson=await db.LessonSessions.FindAsync(lessonId);
            var partial = !opened || lesson?.SqlPracticeTrackingAt is null || previous.Length > 0 || await db.SqlJournalStreams.AnyAsync(s => s.LessonId == lessonId && s.StudentId == work.StudentId && s.TaskId == work.TaskId && s.WorkScope == "course");
            row = new() { LessonId = lessonId, StudentId = work.StudentId, TaskId = work.TaskId, Json = work.Json, Revision = work.Revision,
                CreatedAt = ServerState.Now, Partial = partial, TrackingVersion = partial ? 0 : 1, StartedAt = partial ? null : ServerState.Now,
                Draft = work.Draft, ProjectOrder = work.ProjectOrder, CompletedAt = correct?.At, AcceptedCode = correct?.Code,
                Checks = previous.Count(a => a.Action == "check" || a.Action == null && a.Kind is not ("clipboard" or "capture")),
                Errors = previous.Count(a => (a.Action == "check" || a.Action == null && a.Kind is not ("clipboard" or "capture")) && a.Kind != "correct") };
            db.LessonSqlPractice.Add(row);
        }
        if (Catalog.Write(Catalog.Read(row.Json)) != Catalog.Write(Catalog.Read(work.Json)) || row.Revision != work.Revision)
            throw new HttpFault(409, "Свободная работа этой пары сохранена в другой редакции. Прежний снимок не заменён.");
        if (draft is not null) row.Draft = draft;
        row.LastActivityAt = ServerState.Now;
        return row;
    }
    public static async Task Execution(ClassroomDb db, ISqlWork work, string? lessonId, ExecuteRequest request, ExecutionDto result)
    {
        if (!request.Check && string.IsNullOrWhiteSpace(request.Code)) return;
        var row = await Observe(db, work, lessonId); if (row is null) return;
        if (request.Check)
        {
            row.Checks++; if (!result.Correct) row.Errors++;
            if (result.Correct && row.CompletedAt is null)
            { row.CompletedAt = ServerState.Now; row.AcceptedCode = request.Code; row.ActiveSecondsAtCompletion = row.ActiveSeconds; }
        }
    }
    public static async Task Activity(ClassroomDb db, ISqlWork work, string? lessonId, int seconds, bool immediate)
    {
        var row = await Observe(db, work, lessonId); if (row is null || !immediate) return;
        var elapsed = row.LastHeartbeatAt is { } last ? Math.Clamp((ServerState.Now - last) / 1000, 0, 30) : Math.Min(2, seconds);
        if (row.CompletedAt is null) row.ActiveSeconds += (int)Math.Min(seconds, elapsed);
        row.LastHeartbeatAt = ServerState.Now;
    }
    public static async Task Hint(ClassroomDb db, ISqlWork work, string? lessonId)
    {
        var row = await Observe(db, work, lessonId); if (row is not null) row.HintsJson = Wire.Write(Wire.Read<long[]>(row.HintsJson).Append(ServerState.Now).ToArray());
    }
}
