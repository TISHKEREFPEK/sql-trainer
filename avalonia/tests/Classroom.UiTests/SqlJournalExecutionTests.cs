using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlJournalExecutionTests
{
    [Fact]
    public async Task LostAckAndRetryLinkExactFrozenOperationWhileEditorHasAnotherCode()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var root = Path.Combine(Path.GetTempPath(), "classroom-journal-links-" + Guid.NewGuid().ToString("N"));
        using var local = new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId); local.Observe(lesson, task);
        local.Edit(lesson, task.WorkScope, task.Id, "SELECT FROM", 0);
        var pending = new PendingExecution(task.Id, "SELECT FROM", true, lesson, task.WorkScope); local.Remember(pending);
        await local.Drain(f.Student);
        var prefix = $"teacher/sql-journal/{f.StudentId}/{local.Find(lesson, task.WorkScope, task.Id)!.Journal!.StreamId}";
        Assert.Equal("unconfirmed", (await f.Admin.Get<SqlJournalExecutionLink>(prefix + "/changes/2/execution")).Availability);
        var result = await pending.Send(f.Student); Assert.NotNull(result.Error);
        local.Edit(lesson, task.WorkScope, task.Id, "SELECT 800;", 0); local.Remember(pending);
        await local.Drain(f.Student); // The old receipt supplies the result without another execution.
        var work = local.Find(lesson, task.WorkScope, task.Id)!; Assert.Null(work.Execution);
        var references = work.Journal!.Changes.Where(c => c.Reference?.OperationId == pending.Request.OperationId).ToArray();
        Assert.Equal(new[] { "check", "check-retry", "check-result" }, references.Select(c => c.Kind));
        var ids = new List<string>();
        foreach (var step in references)
        {
            var link = await f.Admin.Get<SqlJournalExecutionLink>(prefix + "/changes/" + step.Sequence + "/execution"); Assert.Equal("available", link.Availability); ids.Add(link.AttemptId!);
        }
        Assert.Single(ids.Distinct()); var attempt = await f.Admin.Get<SqlReviewAttempt>("teacher/sql-review/attempts/" + ids[0]);
        Assert.Equal("SELECT FROM", attempt.Code); Assert.Equal(result.Error, attempt.OriginalResult!.Error);
        Assert.Equal("SELECT 800;", work.Journal.Replay((int)references[1].Sequence));
        Assert.Equal(1, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/{task.Id}")).Record.Checks);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<SqlJournalExecutionLink>(prefix + "/changes/2/execution"))).Status);
        // A claimed hash mismatch must not open an unrelated confirmed attempt.
        local.Mark(lesson, task.WorkScope, task.Id, "check", new(pending.Request.OperationId, SqlSyncHash.Text("different SQL"))); await local.Drain(f.Student);
        Assert.Equal("mismatch", (await f.Admin.Get<SqlJournalExecutionLink>(prefix + "/changes/" + local.Find(lesson, task.WorkScope, task.Id)!.Journal!.Changes.Length + "/execution")).Availability);
        var otherId = (await f.Student.Get<CatalogueDto>("catalog")).Tasks.First(t => t.Id != task.Id && t.Project is null).Id;
        var other = await f.Student.Get<StudentTask>("tasks/" + otherId); local.Observe(lesson, other);
        local.Mark(lesson, other.WorkScope, other.Id, "check", new(pending.Request.OperationId, SqlSyncHash.Text(pending.Request.Code))); await local.Drain(f.Student);
        var otherStream = local.Find(lesson, other.WorkScope, other.Id)!.Journal!;
        Assert.Equal("mismatch", (await f.Admin.Get<SqlJournalExecutionLink>($"teacher/sql-journal/{f.StudentId}/{otherStream.StreamId}/changes/1/execution")).Availability);
        var backup = await f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups");
        await f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        Assert.Equal(ids[0], (await f.Admin.Get<SqlJournalExecutionLink>(prefix + "/changes/2/execution")).AttemptId);
    }
    [Fact]
    public async Task ExistingJsonWithoutOptionalReferenceRemainsAnIdenticalRetransmission()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database");
        var journal = SqlTextJournal.Record(null, "SELECT 0;", "SELECT 1;", "student", "edit", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var batch = new SqlJournalBatch(journal.StreamId, f.Student.LessonId!, task.WorkScope, task.Id, journal.StartedAt, journal.BaseCode, 1, journal.Changes);
        await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DatabasePath, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE SqlJournalChanges SET ChangeJson=$json";
            command.Parameters.AddWithValue("$json", Wire.Write(journal.Changes[0]).Replace(",\"reference\":null", "", StringComparison.Ordinal)); Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.Equal("accepted", (await f.Student.Post<SqlJournalAck>("sql-journal/sync", batch)).Status);
        var invalid = journal.Changes[0] with { Id = Guid.NewGuid().ToString("N"), Reference = new("invalid id", SqlSyncHash.Text("SELECT 1;")) };
        var bad = batch with { StreamId = Guid.NewGuid().ToString("N"), Changes = [invalid] };
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<SqlJournalAck>("sql-journal/sync", bad))).Status);
        Assert.Single(await f.Admin.Get<SqlJournalStreamInfo[]>($"teacher/sql-journal/{f.StudentId}"));
    }
}
