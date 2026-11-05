using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class SqlJournalRoutes
{
    static SqlJournalStreamInfo Info(SqlJournalStreamEntity row) => new(row.Id, row.LessonId, row.WorkScope, row.TaskId, row.Title, row.StartedAt, row.ReceivedAt, row.Sequence, row.ForkedFrom);
    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapPost("/sql-journal/sync", (HttpContext ctx, SqlJournalBatch input) =>
        {
            var session = state.Authorize(ctx, "student");
            if (!Guid.TryParseExact(input.StreamId, "N", out _) || input.BaseCode is null || input.BaseCode.Length > 12000 ||
                input.StartedAt <= 0 || input.StartedAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() || input.KnownSequence is < 0 or > 10000000 || input.Changes is null || input.Changes.Length > 64 ||
                input.ForkedFrom is not null && (!Guid.TryParseExact(input.ForkedFrom, "N", out _) || input.ForkedFrom == input.StreamId))
                throw new HttpFault(400, "Некорректный пакет истории SQL.");
            return state.Write(async db =>
            {
                var work = await SqlSyncRoutes.Existing(db, session, input.LessonId, input.WorkScope, input.TaskId);
                var row = await db.SqlJournalStreams.FindAsync(session.ProfileId, input.StreamId);
                if (row is null)
                {
                    if (input.ForkedFrom is { } parent)
                    {
                        var origin = await db.SqlJournalStreams.FindAsync(session.ProfileId, parent);
                        if (origin is null || origin.LessonId != input.LessonId || origin.TaskId != input.TaskId || origin.WorkScope != input.WorkScope)
                            throw new HttpFault(409, "Исходная ветвь истории не найдена в этой работе.");
                    }
                    try { _ = SqlTextJournal.Start(input.BaseCode, input.StartedAt).Replay(); }
                    catch (InvalidDataException e) { throw new HttpFault(400, e.Message); }
                    row = new() { StudentId = session.ProfileId, Id = input.StreamId, LessonId = input.LessonId, WorkScope = input.WorkScope, TaskId = input.TaskId,
                        Title = Catalog.Read(work.Json).Title, StartedAt = input.StartedAt, ReceivedAt = ServerState.Now, BaseCode = input.BaseCode, TipCode = input.BaseCode, ForkedFrom = input.ForkedFrom };
                }
                else if (row.LessonId != input.LessonId || row.WorkScope != input.WorkScope || row.TaskId != input.TaskId || row.BaseCode != input.BaseCode || row.StartedAt != input.StartedAt || row.ForkedFrom != input.ForkedFrom)
                    throw new HttpFault(409, "Номер истории уже привязан к другой исходной работе.");
                if (row.Sequence > input.KnownSequence) return new SqlJournalAck(row.Id, "fork", row.Sequence, SqlSyncHash.Text(row.TipCode));
                // Validate the whole packet before tracking mutations: a divergent
                // retransmission never changes accepted steps or the stream head.
                var code = row.TipCode; var sequence = row.Sequence;
                var additions = new List<SqlJournalChangeEntity>(); var packetIds = new HashSet<string>(); long previous = 0;
                foreach (var change in input.Changes)
                {
                    if (change is null || change.Sequence < 1 || change.At > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
                        change.Inserted is null || change.Inserted.Length > 12000 || change.Checkpoint?.Length > 12000 || change.Kind is null || change.Kind.Length > 80 ||
                        change.Sequence > input.KnownSequence || previous != 0 && change.Sequence != previous + 1 || !packetIds.Add(change.Id))
                        throw new HttpFault(400, "Нарушен порядок пакета SQL-истории.");
                    previous = change.Sequence;
                    if (change.Sequence <= row.Sequence)
                    {
                        var accepted = await db.SqlJournalChanges.FindAsync(session.ProfileId, row.Id, change.Sequence);
                        if (accepted is null || Wire.Read<SqlTextChange>(accepted.ChangeJson) != change) return new SqlJournalAck(row.Id, "fork", row.Sequence, SqlSyncHash.Text(row.TipCode));
                        continue;
                    }
                    if (change.Sequence != sequence + 1) throw new HttpFault(409, "Сначала передайте предыдущие шаги истории.");
                    if (await db.SqlJournalChanges.AnyAsync(c => c.StudentId == session.ProfileId && c.StreamId == row.Id && c.EventId == change.Id))
                        throw new HttpFault(409, "UUID шага уже использован в этой истории.");
                    try { code = new SqlTextJournal(1, row.Id, row.StartedAt, code, [change with { Sequence = 1 }]).Replay(); }
                    catch (InvalidDataException e) { throw new HttpFault(400, e.Message); }
                    sequence = change.Sequence;
                    additions.Add(new() { StudentId = session.ProfileId, StreamId = row.Id, Sequence = sequence, EventId = change.Id, ChangeJson = Wire.Write(change),
                        ReceivedAt = ServerState.Now, Actor = "student", Checkpoint = sequence % 64 == 0 ? code : null });
                }
                if (db.Entry(row).State == EntityState.Detached) db.SqlJournalStreams.Add(row);
                db.SqlJournalChanges.AddRange(additions); row.Sequence = sequence; row.TipCode = code;
                return new SqlJournalAck(row.Id, "accepted", sequence, SqlSyncHash.Text(code));
            });
        });
        api.MapGet("/teacher/sql-journal/{studentId}", (HttpContext ctx, string studentId, string? lessonId, string? taskId, int? offset) =>
        {
            state.Authorize(ctx, "teacher"); var start = offset ?? 0;
            if (start is < 0 or > 1000000) throw new HttpFault(400, "Некорректная страница истории.");
            return state.Read(async db =>
            {
                var query = db.SqlJournalStreams.AsNoTracking().Where(s => s.StudentId == studentId);
                if (lessonId is not null) query = query.Where(s => s.LessonId == lessonId);
                if (taskId is not null) query = query.Where(s => s.TaskId == taskId);
                return (await query.OrderByDescending(s => s.ReceivedAt).ThenBy(s => s.Id).Skip(start).Take(30).ToArrayAsync()).Select(Info).ToArray();
            });
        });
        api.MapGet("/teacher/sql-journal/{studentId}/{streamId}/changes", (HttpContext ctx, string studentId, string streamId, long? after) =>
        {
            state.Authorize(ctx, "teacher"); var cursor = after ?? 0;
            if (cursor < 0) throw new HttpFault(400, "Некорректная позиция истории.");
            return state.Read(async db =>
            {
                _ = await db.SqlJournalStreams.FindAsync(studentId, streamId) ?? throw new HttpFault(404, "История не найдена.");
                var changes = await db.SqlJournalChanges.AsNoTracking().Where(c => c.StudentId == studentId && c.StreamId == streamId && c.Sequence > cursor).OrderBy(c => c.Sequence).Take(101).ToArrayAsync();
                return new SqlJournalEvents(changes.Take(100).Select(c => new SqlJournalEvent(Wire.Read<SqlTextChange>(c.ChangeJson), c.ReceivedAt, c.Actor)).ToArray(),
                    changes.Length > 100, changes.Length == 0 ? cursor : changes[Math.Min(99, changes.Length - 1)].Sequence);
            });
        });
        api.MapGet("/teacher/sql-journal/{studentId}/{streamId}/state", (HttpContext ctx, string studentId, string streamId, long sequence) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(async db =>
            {
                var row = await db.SqlJournalStreams.FindAsync(studentId, streamId) ?? throw new HttpFault(404, "История не найдена.");
                if (sequence < 0 || sequence > row.Sequence) throw new HttpFault(400, "Такого шага нет.");
                var checkpoint = await db.SqlJournalChanges.AsNoTracking().Where(c => c.StudentId == studentId && c.StreamId == streamId && c.Sequence <= sequence && c.Checkpoint != null).OrderByDescending(c => c.Sequence).FirstOrDefaultAsync();
                var first = checkpoint?.Sequence ?? 0; var code = checkpoint?.Checkpoint ?? row.BaseCode;
                var changes = await db.SqlJournalChanges.AsNoTracking().Where(c => c.StudentId == studentId && c.StreamId == streamId && c.Sequence > first && c.Sequence <= sequence).OrderBy(c => c.Sequence).ToArrayAsync();
                foreach (var change in changes) code = new SqlTextJournal(1, row.Id, row.StartedAt, code, [Wire.Read<SqlTextChange>(change.ChangeJson) with { Sequence = 1 }]).Replay();
                return new SqlJournalText(row.Id, sequence, code);
            });
        });
        api.MapGet("/teacher/sql-journal/{studentId}/{streamId}/changes/{sequence:long}/execution", (HttpContext ctx, string studentId, string streamId, long sequence) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(async db =>
            {
                var stream = await db.SqlJournalStreams.FindAsync(studentId, streamId) ?? throw new HttpFault(404, "История не найдена.");
                var step = await db.SqlJournalChanges.FindAsync(studentId, streamId, sequence) ?? throw new HttpFault(404, "Шаг истории не найден.");
                var change = Wire.Read<SqlTextChange>(step.ChangeJson);
                var reference = change.Reference;
                if (reference?.OperationId is not { } operationId) throw new HttpFault(404, "У шага нет ссылки на запуск.");
                var evidence = await db.SqlExecutionEvidence.SingleOrDefaultAsync(e => e.StudentId == studentId && e.OperationId == operationId);
                if (evidence is null) return new SqlJournalExecutionLink("unconfirmed");
                var attempt = await db.Attempts.FindAsync(evidence.AttemptId);
                if (attempt is null || attempt.StudentId != studentId || attempt.TaskId != stream.TaskId || attempt.LessonId != stream.LessonId ||
                    attempt.WorkScope != stream.WorkScope || SqlSyncHash.Text(attempt.Code ?? "") != reference.CodeHash || evidence.Check != change.Kind.StartsWith("check", StringComparison.Ordinal))
                    return new SqlJournalExecutionLink("mismatch");
                return new SqlJournalExecutionLink("available", evidence.AttemptId);
            });
        });
    }
}
