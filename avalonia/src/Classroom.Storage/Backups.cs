using Microsoft.Data.Sqlite;
using Classroom.Contracts;
using Microsoft.EntityFrameworkCore;

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
        try
        {
            RestoreVerified(name);
        }
        catch (SqliteException e) { throw new InvalidDataException("Резервная копия SQLite повреждена. Рабочая база не заменена.", e); }
    }

    void RestoreVerified(string name)
    {
        if (Path.GetFileName(name) != name || !List().Any(b => b.Name == name))
            throw new InvalidDataException("Резервная копия не найдена.");
        var copy = Path.Combine(DirectoryPath, name);
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        check.Open();
        using var cmd = check.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        if ((string?)cmd.ExecuteScalar() != "ok")
            throw new InvalidDataException("Резервная копия повреждена.");
        using var schema = check.CreateCommand();
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Profiles','Assignments','__EFMigrationsHistory')";
        if (Convert.ToInt32(schema.ExecuteScalar()) != 3)
            throw new InvalidDataException("Это не база Classroom.");
        var stage = databasePath + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stage, Pooling = false }.ToString()))
            {
                destination.Open();
                check.BackupDatabase(destination);
            }
            try
            {
                using var db = new ClassroomDb(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = stage, Pooling = false }.ToString()).Options);
                if (db.Database.GetAppliedMigrations().Except(db.Database.GetMigrations()).Any())
                    throw new InvalidDataException("Копия создана более новой несовместимой версией приложения.");
                db.Database.Migrate();
                DatabaseHealth.Validate(db);
            }
            catch (Exception e) when (e is SqliteException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
            {
                throw new InvalidDataException("Резервная копия содержит неполную схему или некорректные данные. Рабочая база не заменена.", e);
            }
            // Only a fully migrated/validated candidate can reach the live database.
            Create("before-restore");
            SqliteConnection.ClearAllPools();
            using var verified = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stage, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var live = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
            verified.Open();
            live.Open();
            verified.BackupDatabase(live); // SQLite's backup transaction preserves WAL and rolls back on failure.
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(stage + suffix))
                    File.Delete(stage + suffix);
        }
    }
}
