using System.Security.Cryptography;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed record ElectronicsOutboxCandidate(string ProjectFile, ElectronicsHistoryContext Context, string? SourceHash,
    string? BackupHash, string? BindingsHash, string? ClientId, ElectronicsSync.Outbox? Backup, string Problem);
public sealed class ElectronicsOutboxRecoveryException(ElectronicsOutboxCandidate candidate, string? taskId = null)
    : IOException("Не удалось открыть очередь электронных результатов. Исходные файлы сохранены.")
{
    public ElectronicsOutboxCandidate Candidate { get; } = candidate;
    public string? TaskId { get; } = taskId;
}

public static class ElectronicsOutboxRecovery
{
    static bool Expected(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException;
    static void Regular(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Восстановление через файловую ссылку не поддерживается.");
    }
    static void BoundPath(string project, ElectronicsHistoryContext context)
    {
        if (!Guid.TryParseExact(context.ProfileId, "N", out _) || context.Fingerprint.Length != 64 || !context.Fingerprint.All(Uri.IsHexDigit) ||
            !Guid.TryParseExact(context.LessonId, "N", out _) || context.AssignmentId is { } assigned && !Guid.TryParseExact(assigned, "N", out _)) throw new InvalidDataException("Некорректный контекст восстановления.");
        var name = SqlSyncHash.Text(context.Fingerprint + ":" + context.ProfileId) + (context.AssignmentId is { } id ? "-" + id : "") + ".json";
        if (Path.GetFileName(project) != name || Path.GetFileName(Path.GetDirectoryName(project)) != "electronics") throw new InvalidDataException("Восстановление доступно для исходной персональной копии ученика.");
        Regular(Path.GetDirectoryName(project)!); Regular(Path.GetDirectoryName(Path.GetDirectoryName(project)!)!);
    }
    static FileStream Lease(string project)
    {
        var folder = project + ".history"; Directory.CreateDirectory(folder); Regular(folder);
        var file = Path.Combine(folder, "open.lock"); if (File.Exists(file)) Regular(file);
        return new(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    static string? Hash(string file)
    {
        if (!File.Exists(file)) return null;
        Regular(file); if (new FileInfo(file).Length > 64_000_000) throw new InvalidDataException("Файл слишком велик для восстановления очереди.");
        using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input));
    }
    internal static bool Ensure(string project, ElectronicsHistoryContext context, string? taskId)
    {
        var source = project + ".sync.json"; var folder = project + ".history";
        var known = File.Exists(source) || File.Exists(source + ".backup") || Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(p => Path.GetFileName(p) != "open.lock");
        if (!known) return false; // A genuinely new work has no prior identity to recover.
        try { ElectronicsSync.ReadOutbox(source, context.AssignmentId); }
        catch (Exception e) when (Expected(e)) { throw new ElectronicsOutboxRecoveryException(Inspect(project, context, e.Message), taskId); }
        return true;
    }
    public static ElectronicsOutboxCandidate Inspect(string project, ElectronicsHistoryContext context, string problem)
    {
        project = Path.GetFullPath(project); string? sourceHash = null, backupHash = null, bindingsHash = null, client = null; ElectronicsSync.Outbox? backup = null;
        try
        {
            BoundPath(project, context); using var lease = Lease(project); var source = project + ".sync.json";
            sourceHash = Hash(source); var bindings = ElectronicsHistoryStore.InspectBindings(project + ".history", context);
            bindingsHash = bindings.Hash; client = bindings.ClientId;
            backupHash = Hash(source + ".backup");
            if (backupHash is not null) backup = ElectronicsSync.ReadOutbox(source + ".backup", context.AssignmentId, client);
        }
        catch (Exception e) when (Expected(e)) { backup = null; problem += " Резервную копию нельзя применить: " + e.Message; }
        return new(project, context, sourceHash, backupHash, bindingsHash, client, backup, problem);
    }
    public static string? Restore(ElectronicsOutboxCandidate candidate)
    {
        var project = Path.GetFullPath(candidate.ProjectFile); BoundPath(project, candidate.Context); using var lease = Lease(project);
        var source = project + ".sync.json"; var backupFile = source + ".backup";
        void Unchanged()
        {
            var bindings = ElectronicsHistoryStore.InspectBindings(project + ".history", candidate.Context);
            if (Hash(source) != candidate.SourceHash || Hash(backupFile) != candidate.BackupHash || bindings.Hash != candidate.BindingsHash || bindings.ClientId != candidate.ClientId)
                throw new IOException("Файлы изменились после просмотра. Откройте восстановление заново.");
        }
        if (candidate.Backup is null || candidate.BackupHash is null || candidate.BindingsHash is null) throw new InvalidDataException("Проверенной резервной копии нет.");
        Unchanged(); var restored = ElectronicsSync.ReadOutbox(backupFile, candidate.Context.AssignmentId, candidate.ClientId); restored.Dirty = true;
        var bytes = System.Text.Encoding.UTF8.GetBytes(Wire.Write(restored));
        if (bytes.Length > 64_000_000) throw new InvalidDataException("Восстановленная очередь превышает допустимый размер. Исходники сохранены.");
        var temporary = source + ".restore-" + Guid.NewGuid().ToString("N"); string? archive = null;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(true); }
            Unchanged();
            if (candidate.SourceHash is not null)
            {
                archive = source + ".damaged-" + Guid.NewGuid().ToString("N");
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { input.CopyTo(output); output.Flush(true); }
                if (Hash(archive) != candidate.SourceHash) throw new IOException("Исходный файл изменился во время копирования. Замена отменена.");
            }
            Unchanged(); File.Move(temporary, source, candidate.SourceHash is not null); return archive;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
