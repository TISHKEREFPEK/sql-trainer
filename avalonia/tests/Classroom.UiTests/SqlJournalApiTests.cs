using Classroom.Client;
using Classroom.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlJournalApiTests
{
    static SqlTextJournal Journal(int count)
    {
        var journal = SqlTextJournal.Start("SELECT 0;", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        for (var i = 1; i <= count; i++) journal = SqlTextJournal.Record(journal, journal.Replay(), "SELECT " + i + "; -- текст 😀", "student", "edit", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return journal;
    }
    [Fact]
    public async Task OrderedIdempotentDeliveryReplaysEveryStateAndSurvivesServerRestart()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var journal = Journal(130);
        SqlJournalBatch Batch(int offset, int take) => new(journal.StreamId, lesson, task.WorkScope, task.Id, journal.StartedAt, journal.BaseCode, 130, journal.Changes.Skip(offset).Take(take).ToArray());
        var first = await f.Student.Post<SqlJournalAck>("sql-journal/sync", Batch(0, 64)); Assert.Equal(64, first.Sequence);
        Assert.Equal(first, await f.Student.Post<SqlJournalAck>("sql-journal/sync", Batch(0, 64)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<SqlJournalAck>("sql-journal/sync", Batch(128, 2)))).Status);
        await f.Student.Post<SqlJournalAck>("sql-journal/sync", Batch(64, 64)); await f.Student.Post<SqlJournalAck>("sql-journal/sync", Batch(128, 2));
        var prefix = $"teacher/sql-journal/{f.StudentId}/{journal.StreamId}";
        foreach (var sequence in new[] { 0, 1, 63, 64, 65, 128, 130 })
            Assert.Equal(journal.Replay(sequence), (await f.Admin.Get<SqlJournalText>($"{prefix}/state?sequence={sequence}")).Code);
        var page = await f.Admin.Get<SqlJournalEvents>(prefix + "/changes"); Assert.Equal(100, page.Events.Length); Assert.True(page.HasMore);
        Assert.Equal(30, (await f.Admin.Get<SqlJournalEvents>(prefix + "/changes?after=" + page.NextSequence)).Events.Length);
        Assert.All(page.Events, e => { Assert.Equal("student", e.Actor); Assert.True(e.ReceivedAt > 0); });
        Assert.Empty((await f.Student.Get<StudentTask>("tasks/database")).Draft);
        await f.Restart(); await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        Assert.Equal(journal.Replay(), (await f.Admin.Get<SqlJournalText>($"{prefix}/state?sequence=130")).Code);
    }
    [Fact]
    public async Task DivergentAndShorterStreamsRequireForkAndCannotChangeAcceptedHistory()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var journal = Journal(3);
        var batch = new SqlJournalBatch(journal.StreamId, f.Student.LessonId!, task.WorkScope, task.Id, journal.StartedAt, journal.BaseCode, 3, journal.Changes);
        await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch);
        Assert.Equal("fork", (await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch with { KnownSequence = 1, Changes = journal.Changes.Take(1).ToArray() })).Status);
        var corrupt = journal.Changes.ToArray(); corrupt[1] = corrupt[1] with { Id = Guid.NewGuid().ToString("N") };
        Assert.Equal("fork", (await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch with { Changes = corrupt })).Status);
        var forkId = Guid.NewGuid().ToString("N");
        Assert.Equal("accepted", (await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch with { StreamId = forkId, ForkedFrom = journal.StreamId, Changes = corrupt })).Status);
        Assert.Equal(2, (await f.Admin.Get<SqlJournalStreamInfo[]>($"teacher/sql-journal/{f.StudentId}")).Length);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<SqlJournalEvents>($"teacher/sql-journal/{f.StudentId}/{forkId}/changes"))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Post<SqlJournalAck>("sql-journal/sync", batch))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<SqlJournalAck>("sql-journal/sync", batch with { TaskId = "missing" }))).Status);
    }
    [Fact]
    public async Task InvalidPacketIsAtomicAndOldLessonDeliveryRequiresOriginalParticipant()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var journal = Journal(3);
        var batch = new SqlJournalBatch(journal.StreamId, f.Student.LessonId!, task.WorkScope, task.Id, journal.StartedAt, journal.BaseCode, 3, journal.Changes);
        var invalid = journal.Changes.ToArray(); invalid[2] = invalid[2] with { AfterHash = new string('0', 64) };
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<SqlJournalAck>("sql-journal/sync", batch with { Changes = invalid }))).Status);
        Assert.Empty(await f.Admin.Get<SqlJournalStreamInfo[]>($"teacher/sql-journal/{f.StudentId}"));
        await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch with { Changes = journal.Changes.Take(1).ToArray() });
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<SqlJournalAck>("sql-journal/sync", batch with { Changes = invalid.Skip(1).ToArray() }))).Status);
        Assert.Equal(1, Assert.Single(await f.Admin.Get<SqlJournalStreamInfo[]>($"teacher/sql-journal/{f.StudentId}")).Sequence);
        await f.Admin.Post<LessonView>($"teacher/lessons/{batch.LessonId}/finish");
        var lesson = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, OpenToAllGroups: true, FreePractice: true)));
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/start"); await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        await f.Admin.Post<System.Text.Json.JsonElement>("teacher/students", new CreateStudentRequest("journal-other", "synthetic-other-password", null));
        using var other = new ClassroomApi(f.Admin.Connection); await other.Login(new("student", "journal-other", "synthetic-other-password"));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => other.Post<SqlJournalAck>("sql-journal/sync", batch))).Status);
        Assert.Equal(3, (await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch)).Sequence);
        // A participant of the new lesson still cannot claim the other pupil's parent.
        await other.Get<StudentTask>("tasks/database");
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => other.Post<SqlJournalAck>("sql-journal/sync", batch with {
            StreamId = Guid.NewGuid().ToString("N"), LessonId = lesson.Id, ForkedFrom = batch.StreamId }))).Status);
    }
    [Fact]
    public async Task BackupRestorePreservesJournalAndDeletingProfileCascadesItsSteps()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var journal = Journal(65);
        var batch = new SqlJournalBatch(journal.StreamId, f.Student.LessonId!, task.WorkScope, task.Id, journal.StartedAt, journal.BaseCode, 65, journal.Changes.Take(64).ToArray());
        await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch);
        await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch with { Changes = journal.Changes.Skip(64).ToArray() });
        var backup = await f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups");
        await f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        Assert.Equal(journal.Replay(), (await f.Admin.Get<SqlJournalText>($"teacher/sql-journal/{f.StudentId}/{journal.StreamId}/state?sequence=65")).Code);
        var saved = Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "backups", backup.GetProperty("name").GetString()!);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = saved, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE SqlJournalStreams SET BaseCode='damaged base'"; command.ExecuteNonQuery();
        }
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore"))).Status);
        Assert.Equal(journal.Replay(), (await f.Admin.Get<SqlJournalText>($"teacher/sql-journal/{f.StudentId}/{journal.StreamId}/state?sequence=65")).Code);
        await f.Admin.Delete<System.Text.Json.JsonElement>("teacher/students/" + f.StudentId);
        Assert.Empty(await f.Admin.Get<SqlJournalStreamInfo[]>($"teacher/sql-journal/{f.StudentId}"));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT (SELECT COUNT(*) FROM SqlJournalStreams)+(SELECT COUNT(*) FROM SqlJournalChanges)";
            Assert.Equal(0L, command.ExecuteScalar()); command.CommandText = "PRAGMA foreign_key_check"; using var rows = command.ExecuteReader(); Assert.False(rows.Read());
        }
        await f.Restart();
    }
}
