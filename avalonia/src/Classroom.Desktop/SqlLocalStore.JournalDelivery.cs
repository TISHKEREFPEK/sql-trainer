using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed record SqlJournalDeliveryState(string StreamId, long Acknowledged, string? ForkedFrom = null);

public sealed partial class SqlLocalStore
{
    // Recheck the server head after opening a profile, even when the disk copy
    // claims to be fully acknowledged: it may be an older restored backup.
    readonly HashSet<string> checkedJournals = new();
    async Task DeliverJournal(ClassroomApi api, SqlLocalWork item, CancellationToken cancellation)
    {
        var key = Key(item.LessonId, item.Task.WorkScope, item.Task.Id);
        SqlLocalWork Current() => Find(item.LessonId, item.Task.WorkScope, item.Task.Id)!;
        SqlTextJournal journal; SqlJournalDeliveryState cursor;
        lock (gate)
        {
            if (disposed || Current().Journal is not { } history) return;
            journal = history; cursor = Current().JournalDelivery ?? new(journal.StreamId, 0);
            if (checkedJournals.Contains(key) && cursor.Acknowledged == journal.Changes.LongLength) return;
        }
        SqlJournalBatch Batch(SqlTextChange[] changes) => new(cursor.StreamId, item.LessonId, item.Task.WorkScope, item.Task.Id,
            journal.StartedAt, journal.BaseCode, journal.Changes.LongLength, changes, cursor.ForkedFrom);
        void Remember(SqlJournalDeliveryState next, bool complete)
        {
            lock (gate)
            {
                if (disposed) return;
                var current = Current();
                Save(current with { JournalDelivery = next }); cursor = next;
                if (complete && current.Journal?.Changes.Length == journal.Changes.Length) checkedJournals.Add(key);
                else checkedJournals.Remove(key);
            }
        }
        bool Accepted(SqlJournalAck ack)
        {
            if (ack.StreamId != cursor.StreamId || ack.Status is not ("accepted" or "fork") || ack.Sequence < 0 ||
                ack.StateHash is null || ack.StateHash.Length != 64 || !ack.StateHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Некорректное подтверждение SQL-истории.");
            return ack.Status == "accepted" && ack.Sequence <= journal.Changes.Length &&
                ack.StateHash == SqlSyncHash.Text(journal.Replay((int)ack.Sequence));
        }
        void Fork() => Remember(new(Guid.NewGuid().ToString("N"), 0, cursor.StreamId), false);

        // A bounded turn lets drafts and other works progress during a large upload.
        for (var packet = 0; packet < 4; packet++)
        {
            lock (gate) { if (disposed) return; }
            Bound(api);
            var head = await api.Post<SqlJournalAck>("sql-journal/sync", Batch([]), cancellation);
            if (!Accepted(head)) { Fork(); continue; }
            // Include the last accepted event as an anchor. Matching final text
            // alone is insufficient: divergent edits can produce the same SQL.
            var start = Math.Max(0, (int)head.Sequence - 1);
            var changes = journal.Changes.Skip(start).Take(64).ToArray();
            Bound(api);
            var ack = changes.Length == 0 ? head : await api.Post<SqlJournalAck>("sql-journal/sync", Batch(changes), cancellation);
            if (!Accepted(ack)) { Fork(); continue; }
            if (changes.Length > 0 && ack.Sequence != changes[^1].Sequence)
                throw new InvalidDataException("Сервер не подтвердил переданные шаги SQL-истории.");
            var complete = ack.Sequence == journal.Changes.Length;
            Remember(cursor with { Acknowledged = ack.Sequence }, complete);
            if (complete) return;
        }
    }
}
