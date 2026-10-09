using Microsoft.Data.Sqlite;
using Classroom.Contracts;

namespace Classroom.Storage;
public sealed class Backups(string databasePath)
{
    public string DirectoryPath => Path.Combine(Path.GetDirectoryName(databasePath)!, "backups");

    public BackupDto[] List() => Directory.Exists(DirectoryPath) ? Directory.GetFiles(DirectoryPath, "*.sqlite").OrderDescending().Select(f => new BackupDto(Path.GetFileName(f), new DateTimeOffset(File.GetCreationTimeUtc(f)).ToUnixTimeMilliseconds())).ToArray() : [];
    public string Create(string reason)
    {
        Directory.CreateDirectory(DirectoryPath);
        var target = Path.Combine(DirectoryPath, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{reason}-{Guid.NewGuid():N}.sqlite");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        return target;
    }

    public void Restore(string name)
    {
        if (Path.GetFileName(name) != name || !List().Any(b => b.Name == name))
            throw new InvalidDataException("Резервная копия не найдена.");
        var copy = Path.Combine(DirectoryPath, name);
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        check.Open();
        using var cmd = check.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        if ((string? )cmd.ExecuteScalar() != "ok")
            throw new InvalidDataException("Резервная копия повреждена.");
        using var schema = check.CreateCommand();
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Profiles','Assignments','__EFMigrationsHistory')";
        if (Convert.ToInt32(schema.ExecuteScalar()) != 3)
            throw new InvalidDataException("Это не база Classroom.");
        Create("before-restore");
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[]
        {
            "-wal",
            "-shm"
        }

        )
            if (File.Exists(databasePath + suffix))
                File.Delete(databasePath + suffix);
        var stage = databasePath + ".restore";
        File.Copy(copy, stage, true);
        File.Move(stage, databasePath, true);
    }
}
