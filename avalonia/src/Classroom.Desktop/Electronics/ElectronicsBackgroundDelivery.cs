using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

internal sealed record ElectronicsBackgroundResult(bool Complete, string? Error = null);

// A session owns no credentials on disk. Closed editors are opened only under
// their normal history lease; the editor and this dispatcher never write together.
internal sealed class ElectronicsBackgroundDelivery : IAsyncDisposable
{
    readonly ClassroomApi api;
    readonly string directory, profileId, fingerprint, token, identity;
    readonly Func<string, bool> isOpen;
    readonly CancellationTokenSource lifetime = new();
    readonly Dictionary<string, (string Signature, long At)> completed = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();
    Task<ElectronicsBackgroundResult>? running;
    int position;
    bool stopped;

    public ElectronicsBackgroundDelivery(string root, ClassroomApi api, Func<string, bool>? isOpen = null)
    {
        this.api = api; profileId = api.ProfileId ?? throw new InvalidDataException("Сначала войдите в ученический профиль.");
        token = api.Token ?? throw new InvalidDataException("Сначала войдите в ученический профиль."); fingerprint = api.Connection.Fingerprint;
        identity = SqlSyncHash.Text(fingerprint + ":" + profileId);
        directory = Path.Combine(Path.GetFullPath(root), "electronics"); this.isOpen = isOpen ?? (_ => false);
    }
    void Bound()
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if (api.ProfileId != profileId || api.Connection.Fingerprint != fingerprint || api.Token != token)
            throw new OperationCanceledException("Ученический сеанс сменился.");
    }
    static void Regular(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Фоновая отправка через файловую ссылку не поддерживается. Исходники сохранены.");
    }
    bool OwnedProject(string path, out string? assignment)
    {
        assignment = null; var name = Path.GetFileName(path);
        if (name == identity + ".json") return true;
        if (!name.StartsWith(identity + "-", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[(identity.Length + 1)..^5], "N", out var id)) return false;
        assignment = id.ToString("N"); return true;
    }
    static string Signature(string? index, string outbox)
    {
        var a = index is null ? null : new FileInfo(index); var b = new FileInfo(outbox);
        return $"{a?.Length}:{a?.LastWriteTimeUtc.Ticks}:{b.Length}:{b.LastWriteTimeUtc.Ticks}";
    }
    public Task<ElectronicsBackgroundResult> DrainOnce()
    {
        lock (gate)
        {
            if (stopped) return Task.FromResult(new ElectronicsBackgroundResult(false));
            if (running is { IsCompleted: false }) return running;
            return running = Run();
        }
    }
    async Task<ElectronicsBackgroundResult> Run()
    {
        try
        {
            Bound(); if (!Directory.Exists(directory)) return new(true);
            Regular(Path.GetDirectoryName(directory)!); Regular(directory);
            // Only direct, deterministically named files of the authenticated pupil.
            // No recursion through imported projects, backups or other profiles.
            var candidates = new List<(string Directory, string? Index, string Project, string? Assignment, string? Error)>();
            var projects = Directory.EnumerateDirectories(directory, identity + "*.json.history").Select(p => p[..^8])
                .Concat(Directory.EnumerateFiles(directory, identity + "*.json.sync.json").Select(p => p[..^10])).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                if (!OwnedProject(project, out var assignment) || isOpen(project)) continue;
                var folder = project + ".history";
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Regular(folder); var indexes = Directory.EnumerateFiles(folder, "c-*.json").Order(StringComparer.Ordinal).ToArray();
                        if (indexes.Length > 0) { foreach (var index in indexes) candidates.Add((folder, index, project, assignment, null)); continue; }
                        // Never turn an incomplete history (pages/backup/temp) into a
                        // purported legacy copy. Keep every fragment for recovery.
                        if (Directory.EnumerateFileSystemEntries(folder).Any(p => Path.GetFileName(p) != "open.lock"))
                            throw new InvalidDataException("Найдены файлы истории без исходного индекса. Требуется восстановить историю; исходники сохранены.");
                    }
                    candidates.Add((folder, null, project, assignment, null));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                { candidates.Add((folder, null, project, assignment, e.Message)); }
            }
            if (candidates.Count == 0) return new(true);
            for (var checkedCount = 0; checkedCount < candidates.Count; checkedCount++)
            {
                var item = candidates[position++ % candidates.Count]; if (position == int.MaxValue) position = 0;
                var outbox = item.Project + ".sync.json"; var key = item.Index ?? outbox; Bound();
                if (item.Error is null && completed.TryGetValue(key, out var previous) && Environment.TickCount64 - previous.At < 60000 &&
                    File.Exists(outbox) && previous.Signature == Signature(item.Index, outbox)) continue;
                try
                {
                    if (item.Error is { } error) throw new InvalidDataException(error);
                    using var history = item.Index is null ? null : ElectronicsHistoryStore.OpenExisting(item.Directory, Path.GetFileName(item.Index), profileId, fingerprint);
                    if (history is not null && history.Context.AssignmentId != item.Assignment) throw new InvalidDataException("История и имя персональной копии не совпадают. Исходники сохранены.");
                    if (history is null) { Directory.CreateDirectory(item.Directory); Regular(item.Directory); }
                    using var legacyLease = history is null ? new FileStream(Path.Combine(item.Directory, "open.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : null;
                    ElectronicsSync? sync = null;
                    using (sync = new ElectronicsSync(api, outbox, _ => { }, startTimer: false, assignmentId: item.Assignment,
                        journalDelivery: history is null ? null : token => { Bound(); return history.Deliver(api, sync!.ClientId, token); }, journalPending: history is null ? null : () => history.Pending, requireExisting: true))
                    {
                        if (history?.DeliveryState is { } binding && binding.ClientId != sync.ClientId)
                            throw new InvalidDataException("Очередь копии изменилась. История сохранена; требуется восстановить её исходную привязку.");
                        Bound(); using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); limit.CancelAfter(TimeSpan.FromSeconds(10));
                        var done = await sync.Flush(limit.Token); Bound();
                        if (done) completed[key] = (Signature(item.Index, outbox), Environment.TickCount64);
                        return new(done, done ? null : sync.DeliveryError);
                    }
                }
                // An editor in another window has the directory lease. Retry later.
                catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { continue; }
            }
            return new(true);
        }
        catch (OperationCanceledException) { return new(false); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return new(false, "Не удалось отправить сохранённую электронную работу: " + e.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        Task<ElectronicsBackgroundResult>? pending;
        lock (gate) { if (stopped) return; stopped = true; lifetime.Cancel(); pending = running; }
        if (pending is not null) await pending;
        lifetime.Dispose();
    }
}
