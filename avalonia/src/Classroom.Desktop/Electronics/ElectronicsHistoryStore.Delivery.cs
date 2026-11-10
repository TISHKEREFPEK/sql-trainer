using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed record ElectronicsJournalDeliveryState(string ClientId, string StreamId, long Acknowledged, string? ForkedFrom = null);
public sealed partial class ElectronicsHistoryStore
{
    readonly SemaphoreSlim delivery = new(1, 1);
    ElectronicsJournalDeliveryState? cursor;
    bool checkedHead;
    public bool Pending { get { lock (gate) return !disposed && (!checkedHead || cursor?.Acknowledged != journal.Changes.Length); } }
    public ElectronicsJournalDeliveryState? DeliveryState { get { lock (gate) return cursor; } }
    public async Task<bool> Deliver(ClassroomApi api, string clientId, CancellationToken cancellation = default)
    {
        await delivery.WaitAsync(cancellation);
        try
        {
            void Bound()
            {
                if (api.ProfileId != context.ProfileId || api.Connection.Fingerprint != context.Fingerprint) throw new InvalidDataException("История относится к другому ученику или серверу.");
            }
            Bound(); ElectronicsJournal snapshot; ElectronicsJournalDeliveryState position;
            lock (gate)
            {
                if (disposed) return false;
                if (!Guid.TryParseExact(clientId, "N", out _) || cursor is { } old && old.ClientId != clientId) throw new InvalidDataException("Очередь копии изменилась. История сохранена; требуется восстановить её исходную привязку.");
                if (!Pending) return true;
                snapshot = journal; position = cursor ?? new(clientId, journal.StreamId, 0);
            }
            void Remember(ElectronicsJournalDeliveryState next, bool complete)
            {
                lock (gate)
                {
                    if (disposed) return;
                    Persist(journal, next); cursor = position = next;
                    checkedHead = complete && journal.Changes.Length == snapshot.Changes.Length;
                }
            }
            var requiresSpi=snapshot.Changes.Any(c=>c.SpiIo is not null);
            if(requiresSpi){
                ElectronicsJournalCapabilities capabilities;
                try{capabilities=await api.Get<ElectronicsJournalCapabilities>("electronics-journal/capabilities",cancellation);}
                catch(ApiException e) when(e.Status is 404 or 405 or 501){throw new InvalidDataException("Обновите приложение преподавателя и сервер для истории SPI. Записи сохранены на этом ПК и не подтверждены как переданные.",e);}
                if(capabilities.Features?.Contains(ElectronicsJournalProtocol.SpiSummary)!=true)throw new InvalidDataException("Сервер не поддерживает историю SPI. Обновите приложение преподавателя; записи сохранены на этом ПК.");
            }
            Remember(position, false);
            ElectronicsJournalBatch Batch(ElectronicsChange[] changes) => new(position.StreamId, clientId, context.LessonId, context.AssignmentId, snapshot.StartedAt, snapshot.BaseJson, snapshot.Changes.Length, changes, position.ForkedFrom);
            bool Accepted(ElectronicsJournalAck ack,ElectronicsChange[] sent)
            {
                if (ack.StreamId != position.StreamId || ack.Status is not ("accepted" or "fork") || ack.Sequence < 0 || ack.StateHash?.Length != 64 || !ack.StateHash.All(Uri.IsHexDigit)) throw new InvalidDataException("Некорректное подтверждение истории схемы.");
                if(requiresSpi&&ack.ChangesHash!=SqlSyncHash.Text(Wire.Write(sent)))throw new InvalidDataException("Сервер не подтвердил содержимое SPI-записей. Обновите приложение преподавателя; исходная очередь сохранена на этом ПК.");
                return ack.Status == "accepted" && ack.Sequence <= snapshot.Changes.Length && ack.StateHash ==
                    (ack.Sequence == 0 ? SqlSyncHash.Text(snapshot.BaseJson) : snapshot.Changes[(int)ack.Sequence - 1].AfterHash);
            }
            void Fork() => Remember(new(clientId, Guid.NewGuid().ToString("N"), 0, position.StreamId), false);
            for (var packet = 0; packet < 4; packet++)
            {
                lock (gate) { if (disposed) return false; }
                Bound(); var head = await api.Post<ElectronicsJournalAck>("electronics-journal/sync", Batch([]), cancellation);
                if (!Accepted(head,[])) { Fork(); continue; }
                // A remote head proves project state, not earlier metadata. If
                // a previous receipt was lost, revalidate every unconfirmed SPI
                // packet instead of trusting a later no-op/project-only hash.
                var confirmed=requiresSpi?Math.Min(position.Acknowledged,head.Sequence):head.Sequence;
                var changes = snapshot.Changes.Skip(Math.Max(0, (int)confirmed - 1)).Take(8).ToArray();
                while (changes.Length > 1 && System.Text.Encoding.UTF8.GetByteCount(Wire.Write(Batch(changes))) > 8 * 1024 * 1024) changes = changes.Take(Math.Max(1, changes.Length / 2)).ToArray();
                if (System.Text.Encoding.UTF8.GetByteCount(Wire.Write(Batch(changes))) > 48 * 1024 * 1024) throw new InvalidDataException("Пакет истории слишком велик для передачи. Все исходные данные сохранены на ПК.");
                Bound(); var ack = changes.Length == 0 ? head : await api.Post<ElectronicsJournalAck>("electronics-journal/sync", Batch(changes), cancellation);
                if (!Accepted(ack,changes)) { Fork(); continue; }
                if (changes.Length > 0 && (requiresSpi?ack.Sequence<changes[^1].Sequence:ack.Sequence!=changes[^1].Sequence)) throw new InvalidDataException("Сервер не подтвердил все переданные шаги схемы.");
                var through=requiresSpi&&changes.Length>0?changes[^1].Sequence:ack.Sequence;
                var complete = through == snapshot.Changes.Length; Remember(position with { Acknowledged = through }, complete);
                if (complete) return !Pending;
            }
            return !Pending;
        }
        finally { delivery.Release(); }
    }
}
