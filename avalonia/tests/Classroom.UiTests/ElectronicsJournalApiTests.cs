using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsJournalApiTests
{
    static async Task<string> Copy(LiveClassroom f, string json, string? assignment = null)
    {
        var client = Guid.NewGuid().ToString("N"); await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 1, json, [], assignment)); return client;
    }
    static ElectronicsJournal Make(int count)
    {
        var project = CircuitProject.Blink(); var json = project.Serialize(); var journal = ElectronicsJournal.Start(json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        for (var i = 1; i <= count; i++) { project.Code = "// шаг " + i + " 😀"; project.Parts[1] = project.Parts[1] with { X = 350 + i }; var next = project.Serialize(); journal = ElectronicsJournal.Record(journal, json, next, "move", journal.StartedAt + i); json = next; }
        return journal;
    }
    [Fact]
    public async Task OrderedImmutableDeliveryReplaysEveryStateAndBackupsPreserveIt()
    {
        await using var f = await LiveClassroom.Start(); var journal = Make(130); var client = await Copy(f, journal.BaseJson);
        var batch = new ElectronicsJournalBatch(journal.StreamId, client, f.Student.LessonId!, null, journal.StartedAt, journal.BaseJson, 130, []);
        for (var start = 0; start < 130; start += 8)
        {
            var packet = batch with { Changes = journal.Changes.Skip(start).Take(8).ToArray() };
            var receipt = await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", packet);
            Assert.Equal(receipt, await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", packet));
        }
        var prefix = $"teacher/electronics-journal/{f.StudentId}/{journal.StreamId}";
        foreach (var n in new[] { 0, 1, 63, 64, 65, 128, 130 }) Assert.Equal(journal.Replay(n), (await f.Admin.Get<ElectronicsJournalState>(prefix + "/state?sequence=" + n)).ProjectJson);
        var page = await f.Admin.Get<ElectronicsJournalEvents>(prefix + "/changes"); Assert.Equal(100, page.Events.Length); Assert.True(page.HasMore); Assert.All(page.Events, e => { Assert.Equal("student", e.Actor); Assert.True(e.ReceivedAt > 0); });
        Assert.Equal(30, (await f.Admin.Get<ElectronicsJournalEvents>(prefix + "/changes?after=" + page.NextSequence)).Events.Length);
        Assert.Equal(journal.BaseJson, (await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/work/{client}")).ProjectJson);
        var backup = await f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups"); await f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password")); Assert.Equal(journal.Replay(), (await f.Admin.Get<ElectronicsJournalState>(prefix + "/state?sequence=130")).ProjectJson);
        var saved = Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "backups", backup.GetProperty("name").GetString()!);
        using (var damaged = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = saved, Pooling = false }.ToString()))
        {
            damaged.Open(); using var change = damaged.CreateCommand(); change.CommandText = "UPDATE ElectronicsJournalStreams SET TipJson='{}'"; change.ExecuteNonQuery();
        }
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore"))).Status);
        Assert.Equal(journal.Replay(), (await f.Admin.Get<ElectronicsJournalState>(prefix + "/state?sequence=130")).ProjectJson);
        await f.Admin.Delete<System.Text.Json.JsonElement>("teacher/students/" + f.StudentId);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT (SELECT COUNT(*) FROM ElectronicsJournalStreams)+(SELECT COUNT(*) FROM ElectronicsJournalChanges)"; Assert.Equal(0L, command.ExecuteScalar());
    }
    [Fact]
    public async Task DivergenceForksButMalformedPacketsAreAtomicAndRolesStayScoped()
    {
        await using var f = await LiveClassroom.Start(); var journal = Make(3); var client = await Copy(f, journal.BaseJson);
        var batch = new ElectronicsJournalBatch(journal.StreamId, client, f.Student.LessonId!, null, journal.StartedAt, journal.BaseJson, 3, journal.Changes);
        var invalid = journal.Changes.ToArray(); invalid[2] = invalid[2] with { AfterHash = new string('0', 64) };
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch with { Changes = invalid }))).Status);
        Assert.Empty(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}"));
        await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch);
        Assert.Equal("fork", (await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch with { KnownSequence = 1, Changes = [] })).Status);
        var different = journal.Changes.ToArray(); different[1] = different[1] with { Id = Guid.NewGuid().ToString("N") };
        Assert.Equal("fork", (await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch with { Changes = different })).Status);
        var branch = Guid.NewGuid().ToString("N"); Assert.Equal("accepted", (await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch with { StreamId = branch, ForkedFrom = journal.StreamId, Changes = different })).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Post<ElectronicsJournalAck>("electronics-journal/sync", batch))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{branch}/changes"))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch with { ClientId = Guid.NewGuid().ToString("N") }))).Status);
    }
    [Fact]
    public async Task ReportLinksRequireExactCopyHashAndOriginalParticipantCanDeliverAfterLessonEnds()
    {
        await using var f = await LiveClassroom.Start(); var project = CircuitProject.Blink().Serialize(); var client = await Copy(f, project); var lesson = f.Student.LessonId!;
        var report = Guid.NewGuid().ToString("N"); var journal = ElectronicsJournal.Start(project, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); journal = ElectronicsJournal.Record(journal, project, project, "report-submitted", journal.StartedAt, force: true, reportId: report, reportHash: SqlSyncHash.Text(project));
        var batch = new ElectronicsJournalBatch(journal.StreamId, client, lesson, null, journal.StartedAt, project, 1, journal.Changes); await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch);
        var prefix = $"teacher/electronics-journal/{f.StudentId}/{journal.StreamId}";
        Assert.Equal("unconfirmed", (await f.Admin.Get<ElectronicsJournalReportLink>(prefix + "/changes/1/report")).Availability);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 1, project, [new(report, "submitted", project, "")]));
        Assert.Equal(report, (await f.Admin.Get<ElectronicsJournalReportLink>(prefix + "/changes/1/report")).ReportId);
        Assert.Equal("legacy-context", (await f.Admin.Get<ElectronicsJournalReportLink>(prefix + "/changes/1/report")).Availability);
        journal = ElectronicsJournal.Record(journal, project, project, "report-submitted", journal.StartedAt, force: true, reportId: report, reportHash: new string('0', 64));
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/finish"); var next = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, OpenToAllGroups: true, FreePractice: true))); await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start"); await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch with { KnownSequence = 2, Changes = journal.Changes.Skip(1).ToArray() });
        Assert.Equal("mismatch", (await f.Admin.Get<ElectronicsJournalReportLink>(prefix + "/changes/2/report")).Availability);
        await f.Admin.Post<System.Text.Json.JsonElement>("teacher/students", new CreateStudentRequest("journal-other", "synthetic-other-password", null)); using var other = new ClassroomApi(f.Admin.Connection); await other.Login(new("student", "journal-other", "synthetic-other-password"));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => other.Post<ElectronicsJournalAck>("electronics-journal/sync", batch))).Status);
    }
    [Fact]
    public async Task AssignedJournalCannotBeReboundToAnotherLessonOrDeviceCopy()
    {
        await using var f = await LiveClassroom.Start(); var lesson = f.Student.LessonId!; var task = Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + task, new ElectronicsTaskRequest("Схема", "Условие"));
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson}/electronics-tasks", new ElectronicsSelectionRequest([task]));
        var assigned = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + task); var client = await Copy(f, assigned.ProjectJson, assigned.Id);
        var batch = new ElectronicsJournalBatch(Guid.NewGuid().ToString("N"), client, lesson, assigned.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), assigned.ProjectJson, 0, []);
        await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch);
        var report = Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 1, assigned.ProjectJson, [new(report, "submitted", assigned.ProjectJson, "", assigned.StateVersion)], assigned.Id));
        var marked = ElectronicsJournal.Record(new(1, batch.StreamId, batch.StartedAt, batch.BaseJson, []), batch.BaseJson, batch.BaseJson, "report-submitted", batch.StartedAt, force: true, reportId: report, reportHash: SqlSyncHash.Text(batch.BaseJson));
        batch = batch with { KnownSequence = 1, Changes = marked.Changes };
        await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch);
        Assert.Equal("available", (await f.Admin.Get<ElectronicsJournalReportLink>($"teacher/electronics-journal/{f.StudentId}/{batch.StreamId}/changes/1/report")).Availability);
        var otherCopy = await Copy(f, assigned.ProjectJson, assigned.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch with { ClientId = otherCopy }))).Status);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/finish"); var next = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, OpenToAllGroups: true, FreePractice: true))); await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start"); await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch with { StreamId = Guid.NewGuid().ToString("N"), LessonId = next.Id }))).Status);
        Assert.Equal("accepted", (await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync", batch)).Status);
    }
}
