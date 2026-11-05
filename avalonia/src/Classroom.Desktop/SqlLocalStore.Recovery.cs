using System.Security.Cryptography;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed record SqlRecoveryCandidate(string File, string ProfileId, string Fingerprint, string SourceHash, string? BackupHash, SqlLocalWork? Backup, string Problem);
public sealed class SqlLocalRecoveryException(SqlRecoveryCandidate candidate) : IOException("Не удалось прочитать локальную SQL-работу. Исходный файл сохранён.")
{
    public SqlRecoveryCandidate Candidate { get; } = candidate;
}

public sealed partial class SqlLocalStore
{
    static void Regular(string file)
    {
        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(Path.GetDirectoryName(file)!).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Восстановление SQL через ссылку не поддерживается.");
    }
    static string FileHash(string file) { Regular(file); using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    static SqlLocalWork ReadWork(string file, string original, string profile, string fingerprint)
    {
        Regular(file);
        if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidDataException("Локальная SQL-работа слишком велика. Файл оставлен без изменений.");
        var envelope = Wire.Read<Envelope>(System.IO.File.ReadAllText(file));
        if (envelope.Version is not (1 or 2 or 3) || envelope.ProfileId != profile || envelope.Fingerprint != fingerprint || envelope.Payload is null || SqlSyncHash.Text(envelope.Payload) != envelope.Hash)
            throw new InvalidDataException("Не удалось подтвердить формат, владельца или целостность локальной работы.");
        var work = Wire.Read<SqlLocalWork>(envelope.Payload);
        if (!Guid.TryParseExact(work.LessonId, "N", out _) || work.Task?.Id is null || work.Code is null || work.Code.Length > 12000 ||
            work.Task.WorkScope != "course" && work.Task.WorkScope != "lesson:" + work.LessonId ||
            Path.GetFileName(original) != Key(work.LessonId, work.Task.WorkScope, work.Task.Id) + ".work.json")
            throw new InvalidDataException("Некорректный контекст локального SQL. Файл оставлен без изменений.");
        if (envelope.Version == 3) work = LoadPages(work, Path.GetDirectoryName(original)!);
        else if (work.JournalIndex is not null) throw new InvalidDataException("Сегменты истории не соответствуют формату работы.");
        if (envelope.Version == 1 && work.Journal is not null || work.Journal is not null && work.Journal.Replay() != work.Code)
            throw new InvalidDataException("SQL-история не соответствует локальному тексту.");
        return work;
    }
    static SqlRecoveryCandidate InspectRecovery(string file, string profile, string fingerprint, string problem)
    {
        SqlLocalWork? backup = null; string? backupHash = null;
        try
        {
            if (System.IO.File.Exists(file + ".backup"))
            {
                backup = ReadWork(file + ".backup", file, profile, fingerprint);
                backupHash = FileHash(file + ".backup");
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { problem += " Резервная копия недоступна: " + e.Message; }
        return new(file, profile, fingerprint, FileHash(file), backupHash, backup, problem);
    }
    public static string RestoreBackup(SqlRecoveryCandidate candidate)
    {
        var file = Path.GetFullPath(candidate.File); var directory = Path.GetDirectoryName(file)!;
        // Only our precise work filename in the bound profile directory is eligible.
        if (Path.GetFileName(directory) != SqlSyncHash.Text(candidate.Fingerprint + ":" + candidate.ProfileId) ||
            Path.GetFileName(file).Length != 74 || !Path.GetFileName(file)[..64].All(Uri.IsHexDigit) || !file.EndsWith(".work.json", StringComparison.Ordinal))
            throw new InvalidDataException("Некорректный путь восстановления SQL.");
        using var lease = new FileStream(Path.Combine(directory, "open.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var backup = file + ".backup";
        if (candidate.Backup is null || candidate.BackupHash is null || FileHash(file) != candidate.SourceHash || FileHash(backup) != candidate.BackupHash)
            throw new IOException("Файлы изменились после просмотра. Повторно откройте восстановление.");
        var recovered = ReadWork(backup, file, candidate.ProfileId, candidate.Fingerprint) with { Dirty = true, PendingDraft = null };
        recovered = recovered with { Journal = SqlTextJournal.Record(recovered.Journal, recovered.Code, recovered.Code, "recovery", "backup-restore", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true) };
        var archive = file + ".damaged-" + Guid.NewGuid().ToString("N");
        var temporary = file + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var stored = StorePages(recovered, directory, new());
                var payload = Wire.Write(stored);
                target.Write(System.Text.Encoding.UTF8.GetBytes(Wire.Write(new Envelope(3, candidate.ProfileId, candidate.Fingerprint, payload, SqlSyncHash.Text(payload)))));
                target.Flush(true);
            }
            if (FileHash(backup) != candidate.BackupHash) throw new IOException("Резервная копия изменилась во время чтения.");
            FileOperation(() => System.IO.File.Copy(file, archive, false));
            if (FileHash(archive) != candidate.SourceHash || FileHash(file) != candidate.SourceHash)
                throw new IOException("Исходная работа изменилась во время восстановления. Файлы сохранены без замены.");
            FileOperation(() => System.IO.File.Move(temporary, file, true));
            return archive;
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
}
