using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed record ElectronicsHistoryContext(string ProfileId, string Fingerprint, string LessonId, string? AssignmentId);

// Learning data only: no API instance, passwords, tokens or teacher solutions.
public sealed class ElectronicsHistoryStore : IDisposable
{
    sealed record PageRef(long First, string Hash);
    sealed record Page(int Version, ElectronicsHistoryContext Context, string StreamId, ElectronicsChange[] Changes);
    sealed record Index(int Version, ElectronicsHistoryContext Context, string StreamId, long StartedAt, string BaseJson, PageRef[] Pages, ElectronicsChange[] Tail);
    sealed record Envelope(int Version, string Payload, string Hash);
    readonly string directory, file;
    readonly ElectronicsHistoryContext context;
    readonly FileStream lease;
    readonly object gate = new();
    readonly Dictionary<string, string> pageCache = new();
    ElectronicsJournal journal;
    string json;
    bool disposed;
    public bool Reopened { get; }
    public ElectronicsJournal Journal { get { lock (gate) return journal; } }
    public string ProjectJson { get { lock (gate) return json; } }
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public ElectronicsHistoryStore(string directory, ElectronicsHistoryContext context, string? initialJson)
    {
        if (!Guid.TryParseExact(context.ProfileId, "N", out _) || context.Fingerprint.Length != 64 || !context.Fingerprint.All(Uri.IsHexDigit) ||
            !Guid.TryParseExact(context.LessonId, "N", out _) || context.AssignmentId is { } assignment && !Guid.TryParseExact(assignment, "N", out _))
            throw new InvalidDataException("Некорректный владелец истории схемы.");
        this.directory = Path.GetFullPath(directory); this.context = context;
        Directory.CreateDirectory(this.directory); Regular(this.directory);
        lease = new FileStream(Path.Combine(this.directory, "open.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        file = Path.Combine(this.directory, "c-" + SqlSyncHash.Text(Wire.Write(context)) + ".json");
        try
        {
            Reopened = File.Exists(file);
            journal = Reopened ? Read(file) : ElectronicsJournal.Start(initialJson ?? throw new InvalidDataException("Сохранённая схема повреждена, проверенной истории для этой работы нет. Исходник оставлен без замены."), Now);
            json = journal.Replay();
            if (!Reopened) Persist(journal);
        }
        catch { lease.Dispose(); throw; }
    }
    static void Regular(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("История схемы через файловую ссылку не поддерживается.");
    }
    static string HashFile(string path)
    { Regular(path); using var source = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)); }
    string PageFile(string hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Некорректный адрес сегмента истории схемы.");
        return Path.Combine(directory, "p-" + hash + ".json");
    }
    ElectronicsJournal Read(string source)
    {
        try
        {
            Regular(source);
            if (new FileInfo(source).Length > 64 * 1024 * 1024) throw new InvalidDataException("Слишком большой индекс истории схемы.");
            var envelope = Wire.Read<Envelope>(File.ReadAllText(source));
            if (envelope.Version != 1 || envelope.Payload is null || SqlSyncHash.Text(envelope.Payload) != envelope.Hash) throw new InvalidDataException("Повреждён индекс истории схемы.");
            var index = Wire.Read<Index>(envelope.Payload);
            if (index.Version != 1 || index.Context != context || index.Pages is null || index.Tail is null || index.Tail.Length >= 64) throw new InvalidDataException("История относится к другой работе.");
            var changes = new List<ElectronicsChange>();
            foreach (var reference in index.Pages)
            {
                if (reference is null || reference.First != changes.Count + 1) throw new InvalidDataException("Нарушен порядок сегментов схемы.");
                var pageFile = PageFile(reference.Hash);
                if (new FileInfo(pageFile).Length > 64 * 1024 * 1024 || HashFile(pageFile) != reference.Hash) throw new InvalidDataException("Повреждён сегмент истории схемы.");
                var page = Wire.Read<Page>(File.ReadAllText(pageFile));
                if (page.Version != 1 || page.Context != context || page.StreamId != index.StreamId || page.Changes?.Length != 64) throw new InvalidDataException("Некорректный сегмент истории схемы.");
                changes.AddRange(page.Changes);
            }
            changes.AddRange(index.Tail);
            return new(1, index.StreamId, index.StartedAt, index.BaseJson, changes.ToArray());
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { throw new InvalidDataException("Не удалось прочитать историю схемы. Исходные файлы сохранены без замены. " + e.Message, e); }
    }
    static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && (e.HResult & 0xffff) is 5 or 32 or 33 && attempt < 5) { Thread.Sleep(10 * (attempt + 1)); }
        }
    }
    static void Atomic(string file, string text, bool backup)
    {
        Regular(Path.GetDirectoryName(file)!);
        if (File.Exists(file)) Regular(file);
        if (backup && File.Exists(file + ".backup")) Regular(file + ".backup");
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { target.Write(System.Text.Encoding.UTF8.GetBytes(text)); target.Flush(true); }
            if (backup && File.Exists(file)) Retry(() => File.Copy(file, file + ".backup", true));
            Retry(() => File.Move(temporary, file, backup));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    void Persist(ElectronicsJournal next)
    {
        var pages = new List<PageRef>(); var complete = next.Changes.Length / 64;
        for (var i = 0; i < complete; i++)
        {
            var start = i * 64; var key = next.StreamId + ":" + start + ":" + next.Changes[start + 63].Id;
            if (!pageCache.TryGetValue(key, out var hash))
            {
                var text = Wire.Write(new Page(1, context, next.StreamId, next.Changes.Skip(start).Take(64).ToArray())); hash = SqlSyncHash.Text(text); var page = PageFile(hash);
                if (System.Text.Encoding.UTF8.GetByteCount(text) > 64 * 1024 * 1024) throw new IOException("Сегмент истории схемы слишком велик. Новое изменение не записано; исходные файлы сохранены.");
                if (File.Exists(page)) { if (HashFile(page) != hash) throw new InvalidDataException("Повреждён прежний сегмент схемы. Исходник не заменён."); }
                else Atomic(page, text, false);
                pageCache[key] = hash;
            }
            else if (!File.Exists(PageFile(hash))) throw new InvalidDataException("Пропал сегмент истории схемы. Новое изменение не записано.");
            pages.Add(new(start + 1, hash));
        }
        var payload = Wire.Write(new Index(1, context, next.StreamId, next.StartedAt, next.BaseJson, pages.ToArray(), next.Changes.Skip(complete * 64).ToArray()));
        var envelope = Wire.Write(new Envelope(1, payload, SqlSyncHash.Text(payload)));
        if (System.Text.Encoding.UTF8.GetByteCount(envelope) > 64 * 1024 * 1024) throw new IOException("Индекс истории схемы заполнен. Новое изменение не записано; сохраните работу отдельно.");
        Atomic(file, envelope, true);
    }
    public void Record(string nextJson, string kind, bool force = false, string? gestureId = null, string? reportId = null, string? reportHash = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = ElectronicsJournal.Record(journal, json, nextJson, kind, Now, force: force, gestureId: gestureId, reportId: reportId, reportHash: reportHash);
            if (ReferenceEquals(next, journal)) return;
            Persist(next); journal = next; json = nextJson;
        }
    }
    internal static void Mirror(string file, string json)
    { Directory.CreateDirectory(Path.GetDirectoryName(file)!); Atomic(file, json, true); }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lease.Dispose(); } }
}
