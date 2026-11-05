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
    bool Owned(string path)
    {
        var name = Path.GetFileName(path);
        if (name == identity + ".json.history") return true;
        return name.StartsWith(identity + "-", StringComparison.Ordinal) && name.EndsWith(".json.history", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[(identity.Length + 1)..^13], "N", out _);
    }
    static string Signature(string index, string outbox)
    {
        var a = new FileInfo(index); var b = new FileInfo(outbox);
        return $"{a.Length}:{a.LastWriteTimeUtc.Ticks}:{b.Length}:{b.LastWriteTimeUtc.Ticks}";
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
            var candidates = new List<(string Directory, string Index, string Project)>();
            foreach (var folder in Directory.EnumerateDirectories(directory, identity + "*.json.history").Order(StringComparer.Ordinal))
            {
                if (!Owned(folder)) continue; Regular(folder);
                var project = folder[..^8]; if (isOpen(project)) continue;
                foreach (var index in Directory.EnumerateFiles(folder, "c-*.json").Order(StringComparer.Ordinal)) candidates.Add((folder, index, project));
            }
            if (candidates.Count == 0) return new(true);
            for (var checkedCount = 0; checkedCount < candidates.Count; checkedCount++)
            {
                var item = candidates[position++ % candidates.Count]; if (position == int.MaxValue) position = 0;
                var outbox = item.Project + ".sync.json"; Bound();
                if (completed.TryGetValue(item.Index, out var previous) && Environment.TickCount64 - previous.At < 60000 &&
                    File.Exists(outbox) && previous.Signature == Signature(item.Index, outbox)) continue;
                try
                {
                    using var history = ElectronicsHistoryStore.OpenExisting(item.Directory, Path.GetFileName(item.Index), profileId, fingerprint);
                    var expectedProject = identity + (history.Context.AssignmentId is { } assigned ? "-" + assigned : "") + ".json";
                    if (Path.GetFileName(item.Project) != expectedProject) throw new InvalidDataException("История и имя персональной копии не совпадают. Исходники сохранены.");
                    string? message = null; ElectronicsSync? sync = null;
                    using (sync = new ElectronicsSync(api, outbox, text => message = text, startTimer: false, assignmentId: history.Context.AssignmentId,
                        journalDelivery: token => { Bound(); return history.Deliver(api, sync!.ClientId, token); }, journalPending: () => history.Pending, requireExisting: true))
                    {
                        if (history.DeliveryState is { } binding && binding.ClientId != sync.ClientId)
                            throw new InvalidDataException("Очередь копии изменилась. История сохранена; требуется восстановить её исходную привязку.");
                        Bound(); using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); limit.CancelAfter(TimeSpan.FromSeconds(10));
                        var done = await sync.Flush(limit.Token); Bound();
                        if (done) completed[item.Index] = (Signature(item.Index, outbox), Environment.TickCount64);
                        return new(done, done ? null : message);
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
