using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed record SqlJournalPageRef(long First, int Count, string Hash);
public sealed record SqlJournalIndex(int Version, string StreamId, long StartedAt, string BaseCode, SqlJournalPageRef[] Pages, SqlTextChange[] Tail);

public sealed partial class SqlLocalStore
{
    sealed record JournalPage(int Version, string Context, string StreamId, SqlTextChange[] Changes);
    readonly Dictionary<string, string> pageCache = new();
    static string PageFile(string directory, string hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Некорректный адрес SQL-сегмента.");
        return Path.Combine(directory, "p-" + hash + ".json");
    }
    static SqlLocalWork StorePages(SqlLocalWork work, string directory, Dictionary<string, string> cache)
    {
        if (work.Journal is not { } journal) return work;
        var context = Key(work.LessonId, work.Task.WorkScope, work.Task.Id);
        var pages = new List<SqlJournalPageRef>();
        var complete = journal.Changes.Length / 64;
        for (var i = 0; i < complete; i++)
        {
            var first = i * 64;
            // Append-only journals retain the preceding immutable changes. A restored
            // branch has different UUIDs at its tip and therefore a different key.
            var cacheKey = context + ":" + journal.StreamId + ":" + first + ":" + journal.Changes[first + 63].Id;
            if (!cache.TryGetValue(cacheKey, out var hash) || !File.Exists(PageFile(directory, hash)))
            {
                var text = Wire.Write(new JournalPage(1, context, journal.StreamId, journal.Changes.Skip(first).Take(64).ToArray()));
                hash = SqlSyncHash.Text(text); var file = PageFile(directory, hash);
                if (File.Exists(file))
                {
                    if (FileHash(file) != hash) throw new InvalidDataException("Повреждён существующий SQL-сегмент. Файл оставлен без изменений.");
                }
                else
                {
                    var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                        { target.Write(System.Text.Encoding.UTF8.GetBytes(text)); target.Flush(true); }
                        FileOperation(() => File.Move(temporary, file));
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                cache[cacheKey] = hash;
            }
            pages.Add(new(first + 1, 64, hash));
        }
        return work with { Journal = null, JournalIndex = new(1, journal.StreamId, journal.StartedAt, journal.BaseCode, pages.ToArray(), journal.Changes.Skip(complete * 64).ToArray()) };
    }
    static SqlLocalWork LoadPages(SqlLocalWork work, string directory)
    {
        if (work.JournalIndex is not { } index) throw new InvalidDataException("В SQL-работе нет индекса истории.");
        if (work.Journal is not null || index.Version != 1 || index.Pages is null || index.Tail is null || index.Tail.Length >= 64)
            throw new InvalidDataException("Некорректный индекс SQL-истории.");
        var changes = new List<SqlTextChange>();
        var context = Key(work.LessonId, work.Task.WorkScope, work.Task.Id);
        foreach (var reference in index.Pages)
        {
            if (reference is null || reference.First != changes.Count + 1 || reference.Count != 64) throw new InvalidDataException("Нарушен порядок SQL-сегментов.");
            var file = PageFile(directory, reference.Hash);
            try
            {
                if (new FileInfo(file).Length > 16 * 1024 * 1024 || FileHash(file) != reference.Hash) throw new InvalidDataException("SQL-сегмент повреждён.");
                var page = Wire.Read<JournalPage>(File.ReadAllText(file));
                if (page.Version != 1 || page.Context != context || page.StreamId != index.StreamId || page.Changes?.Length != 64)
                    throw new InvalidDataException("SQL-сегмент относится к другому контексту.");
                changes.AddRange(page.Changes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { throw new InvalidDataException("Не удалось прочитать SQL-сегмент. История оставлена без изменений.", e); }
        }
        changes.AddRange(index.Tail);
        return work with { Journal = new(1, index.StreamId, index.StartedAt, index.BaseCode, changes.ToArray()), JournalIndex = null };
    }
}
