using Classroom.Contracts;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class ElectronicsJournalRoutes
{
    static string Replay(ElectronicsJournalStreamEntity stream, string json, ElectronicsChange change) =>
        new ElectronicsJournal(1, stream.Id, stream.StartedAt, json, [change with { Sequence = 1 }]).Replay();
    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapPost("/electronics-journal/sync", (HttpContext ctx, ElectronicsJournalBatch input) =>
        {
            var session = state.Authorize(ctx, "student");
            if (!Guid.TryParseExact(input.StreamId, "N", out _) || !Guid.TryParseExact(input.ClientId, "N", out _) || !Guid.TryParseExact(input.LessonId, "N", out _) ||
                input.AssignmentId is { } assigned && !Guid.TryParseExact(assigned, "N", out _) || input.StartedAt <= 0 || input.StartedAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
                input.BaseJson is null || input.BaseJson.Length > 2000000 || input.KnownSequence is < 0 or > 10000000 || input.Changes is null || input.Changes.Length > 8 ||
                input.ForkedFrom is { } parentId && (!Guid.TryParseExact(parentId, "N", out _) || parentId == input.StreamId)) throw new HttpFault(400, "Некорректный пакет истории электроники.");
            return state.Write(async db =>
            {
                if (!await db.LessonParticipants.AnyAsync(p => p.SessionId == input.LessonId && p.StudentId == session.ProfileId)) throw new HttpFault(403, "Вы не участвовали в исходном занятии.");
                var work = await db.ElectronicsWorks.FindAsync(session.ProfileId, input.ClientId) ?? throw new HttpFault(404, "Сначала передайте сохранённую копию схемы.");
                if (work.AssignmentId != input.AssignmentId) throw new HttpFault(409, "Копия относится к другой работе.");
                string title = ElectronicsProjectFormat.Inspect(input.BaseJson).Name;
                if (input.AssignmentId is { } id)
                {
                    var assignment = await ElectronicsAssignments.Own(db, session, id);
                    if (assignment.LessonId != input.LessonId) throw new HttpFault(409, "Работа относится к другому занятию.");
                    title = (await db.LessonElectronicsTasks.FindAsync(assignment.LessonId, assignment.TaskId))!.Title;
                }
                var row = await db.ElectronicsJournalStreams.FindAsync(session.ProfileId, input.StreamId);
                if (row is null)
                {
                    if (input.ForkedFrom is { } parent)
                    {
                        var origin = await db.ElectronicsJournalStreams.FindAsync(session.ProfileId, parent);
                        if (origin is null || origin.LessonId != input.LessonId || origin.AssignmentId != input.AssignmentId || origin.ClientId != input.ClientId) throw new HttpFault(409, "Исходная ветвь не найдена в этой копии работы.");
                    }
                    _ = new ElectronicsJournal(1, input.StreamId, input.StartedAt, input.BaseJson, []).Replay();
                    row = new() { StudentId = session.ProfileId, Id = input.StreamId, ClientId = input.ClientId, LessonId = input.LessonId, AssignmentId = input.AssignmentId, Title = title,
                        StartedAt = input.StartedAt, ReceivedAt = ServerState.Now, BaseJson = input.BaseJson, TipJson = input.BaseJson, ForkedFrom = input.ForkedFrom };
                }
                else if (row.ClientId != input.ClientId || row.LessonId != input.LessonId || row.AssignmentId != input.AssignmentId || row.StartedAt != input.StartedAt || row.BaseJson != input.BaseJson || row.ForkedFrom != input.ForkedFrom)
                    throw new HttpFault(409, "Номер истории уже связан с другой исходной работой.");
                ElectronicsJournalAck Ack(string status) => new(row.Id, status, row.Sequence, SqlSyncHash.Text(row.TipJson));
                if (row.Sequence > input.KnownSequence) return Ack("fork");
                var json = row.TipJson; var sequence = row.Sequence; long previous = 0; var ids = new HashSet<string>(); var additions = new List<ElectronicsJournalChangeEntity>();
                foreach (var change in input.Changes)
                {
                    if (change is null || change.Sequence < 1 || change.Sequence > input.KnownSequence || change.Inserted is null || change.Inserted.Length > 2000000 || change.Checkpoint?.Length > 2000000 ||
                        previous != 0 && change.Sequence != previous + 1 || !ids.Add(change.Id)) throw new HttpFault(400, "Нарушен порядок пакета истории схемы.");
                    previous = change.Sequence;
                    if (change.Sequence <= row.Sequence)
                    {
                        var accepted = await db.ElectronicsJournalChanges.FindAsync(session.ProfileId, row.Id, change.Sequence);
                        if (accepted is null || Wire.Read<ElectronicsChange>(accepted.ChangeJson) != change) return Ack("fork");
                        continue;
                    }
                    if (change.Sequence != sequence + 1) throw new HttpFault(409, "Сначала передайте предыдущие шаги схемы.");
                    if (await db.ElectronicsJournalChanges.AnyAsync(c => c.StudentId == session.ProfileId && c.StreamId == row.Id && c.EventId == change.Id)) throw new HttpFault(409, "Номер шага уже использован.");
                    json = Replay(row, json, change); sequence = change.Sequence;
                    additions.Add(new() { StudentId = session.ProfileId, StreamId = row.Id, Sequence = sequence, EventId = change.Id, ChangeJson = Wire.Write(change), ReceivedAt = ServerState.Now, Actor = "student", Checkpoint = sequence % 64 == 0 ? json : null });
                }
                // Track mutations only after every step is valid.
                if (db.Entry(row).State == EntityState.Detached) db.ElectronicsJournalStreams.Add(row);
                db.ElectronicsJournalChanges.AddRange(additions); row.Sequence = sequence; row.TipJson = json; return Ack("accepted");
            });
        });
        api.MapGet("/teacher/electronics-journal/{studentId}", (HttpContext ctx, string studentId, string? lessonId, string? assignmentId, int? offset) =>
        {
            state.Authorize(ctx, "teacher"); var start = offset ?? 0; if (start is < 0 or > 1000000) throw new HttpFault(400, "Некорректная страница истории.");
            return state.Read(async db =>
            {
                var query = db.ElectronicsJournalStreams.AsNoTracking().Where(s => s.StudentId == studentId);
                if (lessonId is not null) query = query.Where(s => s.LessonId == lessonId);
                if (assignmentId is not null) query = query.Where(s => s.AssignmentId == assignmentId);
                return (await query.OrderByDescending(s => s.ReceivedAt).ThenBy(s => s.Id).Skip(start).Take(30).ToArrayAsync()).Select(s => new ElectronicsJournalInfo(s.Id, s.ClientId, s.LessonId, s.AssignmentId, s.Title, s.StartedAt, s.ReceivedAt, s.Sequence, s.ForkedFrom)).ToArray();
            });
        });
        api.MapGet("/teacher/electronics-journal/{studentId}/{streamId}/changes", (HttpContext ctx, string studentId, string streamId, long? after) =>
        {
            state.Authorize(ctx, "teacher"); var cursor = after ?? 0; if (cursor < 0) throw new HttpFault(400, "Некорректная позиция истории.");
            return state.Read(async db =>
            {
                _ = await db.ElectronicsJournalStreams.FindAsync(studentId, streamId) ?? throw new HttpFault(404, "История не найдена.");
                var rows = await db.ElectronicsJournalChanges.AsNoTracking().Where(c => c.StudentId == studentId && c.StreamId == streamId && c.Sequence > cursor).OrderBy(c => c.Sequence).Take(101).ToArrayAsync();
                return new ElectronicsJournalEvents(rows.Take(100).Select(c => new ElectronicsJournalEvent(Wire.Read<ElectronicsChange>(c.ChangeJson), c.ReceivedAt, c.Actor)).ToArray(), rows.Length > 100, rows.Length == 0 ? cursor : rows[Math.Min(99, rows.Length - 1)].Sequence);
            });
        });
        api.MapGet("/teacher/electronics-journal/{studentId}/{streamId}/state", (HttpContext ctx, string studentId, string streamId, long sequence) =>
        {
            state.Authorize(ctx, "teacher"); return state.Read(async db =>
            {
                var row = await db.ElectronicsJournalStreams.FindAsync(studentId, streamId) ?? throw new HttpFault(404, "История не найдена.");
                if (sequence < 0 || sequence > row.Sequence) throw new HttpFault(400, "Такого шага нет.");
                var checkpoint = await db.ElectronicsJournalChanges.AsNoTracking().Where(c => c.StudentId == studentId && c.StreamId == streamId && c.Sequence <= sequence && c.Checkpoint != null).OrderByDescending(c => c.Sequence).FirstOrDefaultAsync();
                var first = checkpoint?.Sequence ?? 0; var json = checkpoint?.Checkpoint ?? row.BaseJson;
                foreach (var step in await db.ElectronicsJournalChanges.AsNoTracking().Where(c => c.StudentId == studentId && c.StreamId == streamId && c.Sequence > first && c.Sequence <= sequence).OrderBy(c => c.Sequence).ToArrayAsync()) json = Replay(row, json, Wire.Read<ElectronicsChange>(step.ChangeJson));
                return new ElectronicsJournalState(row.Id, sequence, json);
            });
        });
        api.MapGet("/teacher/electronics-journal/{studentId}/{streamId}/changes/{sequence:long}/report", (HttpContext ctx, string studentId, string streamId, long sequence) =>
        {
            state.Authorize(ctx, "teacher"); return state.Read(async db =>
            {
                var row = await db.ElectronicsJournalStreams.FindAsync(studentId, streamId) ?? throw new HttpFault(404, "История не найдена.");
                var step = await db.ElectronicsJournalChanges.FindAsync(studentId, streamId, sequence) ?? throw new HttpFault(404, "Шаг не найден.");
                var change = Wire.Read<ElectronicsChange>(step.ChangeJson); if (change.ReportId is not { } id) throw new HttpFault(404, "У шага нет ссылки на отчёт.");
                var report = await db.ElectronicsReports.FindAsync(studentId, id); if (report is null) return new ElectronicsJournalReportLink("unconfirmed");
                if (report.ClientId != row.ClientId || report.AssignmentId != row.AssignmentId || report.ProjectHash != change.ReportHash || change.Kind != "report-received" && change.Kind != "report-" + report.Kind) return new ElectronicsJournalReportLink("mismatch");
                // Legacy free-editor reports identify a copy, but have no lesson ID.
                // Preserve the exact reference without inventing lesson attribution.
                return new ElectronicsJournalReportLink(row.AssignmentId is null ? "legacy-context" : "available", report.Id, report.Kind);
            });
        });
    }
}
