using Classroom.Domain;
using Classroom.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Classroom.Tests;

public sealed class LessonStorageTests
{
    [Fact]
    public async Task MigrationKeepsLegacyIdentityPasswordAssignmentAndSourceBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "classroom-lesson-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "legacy.sqlite");
        var hash = Passwords.Hash(Guid.NewGuid().ToString("N"));
        var task = Catalog.Data.Tasks[0];
        try
        {
            await using (var db = Open(source))
            {
                await db.Database.MigrateAsync();
                db.Groups.Add(new() { Id = "g", Name = "191" });
                db.Profiles.Add(new() { Id = "s", Login = "legacy-login", PasswordHash = hash, GroupId = "g", Slot = 3 });
                db.Assignments.Add(new() { StudentId = "s", TaskId = task.Id, Json = Catalog.Write(task), Draft = "SELECT 17;", AcceptedCode = "SELECT 42;", CompletedAt = 1234, Checks = 2 });
                await db.SaveChangesAsync();
                await db.GetService<IMigrator>().MigrateAsync("20261023201605_ElectronicsResults");
                await db.Database.CloseConnectionAsync();
            }
            SqliteConnection.ClearAllPools();
            var original = File.ReadAllBytes(source);
            var copy = Path.Combine(dir, "upgrade.sqlite");
            File.Copy(source, copy);
            await using (var upgraded = Open(copy))
            {
                await upgraded.Database.MigrateAsync();
                var student = await upgraded.Profiles.SingleAsync();
                Assert.Equal("s", student.Id); Assert.Equal(hash, student.PasswordHash);
                Assert.Equal("legacy-login", student.Login); Assert.Null(student.FullName);
                Assert.Equal("g", student.GroupId); Assert.Equal(3, student.Slot);
                var assignment = await upgraded.Assignments.SingleAsync();
                Assert.Equal(Catalog.Write(task), assignment.Json); Assert.Equal("SELECT 17;", assignment.Draft);
                Assert.Equal("SELECT 42;", assignment.AcceptedCode); Assert.Equal(1234L, assignment.CompletedAt);
                Assert.Empty(await upgraded.LessonSessions.ToArrayAsync());
                DatabaseHealth.Validate(upgraded);
            }
            Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OneUnfinishedLessonAndMixedGroupParticipationSurviveReopening()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ClassroomDb>().UseSqlite(connection).Options;
        await using (var db = new ClassroomDb(options))
        {
            await db.Database.MigrateAsync();
            Seed(db); await db.SaveChangesAsync();
            db.LessonSessions.Add(new() { Id = "first", GroupId = "a", GroupName = "191", CreatedAt = 100 });
            await db.SaveChangesAsync();
            var duplicate = new LessonSessionEntity { Id = "duplicate", GroupId = "a", GroupName = "191", CreatedAt = 101 };
            db.LessonSessions.Add(duplicate);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.Entry(duplicate).State = EntityState.Detached;
            var first = await db.LessonSessions.SingleAsync(); first.State = "finished"; first.EndedAt = 200;
            await db.SaveChangesAsync();
            db.LessonSessions.Add(new() { Id = "next", GroupId = "a", GroupName = "191", State = "running", CreatedAt = 250, StartedAt = 300, OpenToAllGroups = true });
            db.LessonParticipants.Add(new() { SessionId = "next", StudentId = "s", JoinedAt = 350, LastSeenAt = 400, GroupNameAtEntry = "192" });
            await db.SaveChangesAsync();
        }
        await using var verify = new ClassroomDb(options);
        Assert.Equal(2, await verify.LessonSessions.CountAsync());
        Assert.Equal("b", (await verify.Profiles.SingleAsync(p => p.Id == "s")).GroupId);
        Assert.Equal("192", (await verify.LessonParticipants.SingleAsync()).GroupNameAtEntry);
        Assert.Equal("Иванов Иван", (await verify.Profiles.SingleAsync(p => p.Id == "s")).FullName);
        DatabaseHealth.Validate(verify);
    }

    [Theory]
    [InlineData("dates")]
    [InlineData("participant")]
    [InlineData("name")]
    public async Task HealthRejectsCorruptLessonMetadata(string damage)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new ClassroomDb(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync(); Seed(db); await db.SaveChangesAsync();
        db.LessonSessions.Add(new() { Id = "lesson", GroupId = "a", GroupName = "191", CreatedAt = 100, StartedAt = 200, State = "running" });
        await db.SaveChangesAsync();
        if (damage == "dates") await db.Database.ExecuteSqlRawAsync("UPDATE LessonSessions SET StartedAt=1");
        else if (damage == "name") await db.Database.ExecuteSqlRawAsync("UPDATE Profiles SET FullName='' WHERE Id='s'");
        else await db.Database.ExecuteSqlRawAsync("INSERT INTO LessonParticipants(SessionId,StudentId,JoinedAt,LastSeenAt,GroupNameAtEntry) VALUES('lesson','teacher',250,250,'191')");
        db.ChangeTracker.Clear();
        Assert.Throws<InvalidDataException>(() => DatabaseHealth.Validate(db));
    }

    static ClassroomDb Open(string path) => new(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options);
    static void Seed(ClassroomDb db)
    {
        var hash = Passwords.Hash(Guid.NewGuid().ToString("N"));
        db.Groups.AddRange(new GroupEntity { Id = "a", Name = "191" }, new GroupEntity { Id = "b", Name = "192" });
        db.Profiles.AddRange(new ProfileEntity { Id = "teacher", Login = "teacher", Role = "teacher", PasswordHash = hash },
            new ProfileEntity { Id = "s", Login = "legacy", FullName = "Иванов Иван", GroupId = "b", PasswordHash = hash });
    }
}
