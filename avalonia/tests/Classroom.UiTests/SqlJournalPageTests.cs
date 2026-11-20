using System.Text.Json.Nodes;
using Classroom.Contracts;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlJournalPageTests
{
    [Fact]
    public async Task LegacyV1KeepsItsUnsentTextAndStartsHistoryWithoutInventingOlderEdits()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var root = Path.Combine(Path.GetTempPath(), "classroom-history-v1-" + Guid.NewGuid().ToString("N"));
        SqlLocalStore Open() => new(root, f.Student.Connection.Fingerprint, f.StudentId);
        using (var initial = Open()) initial.Observe(lesson, task);
        var file = Assert.Single(Directory.GetFiles(root, "*.work.json", SearchOption.AllDirectories));
        var payload = Wire.Write(new SqlLocalWork(lesson, task, "SELECT 12;", SqlSyncHash.Text(task.Draft), Dirty: true));
        File.WriteAllText(file, Wire.Write(new { Version = 1, ProfileId = f.StudentId, f.Student.Connection.Fingerprint, Payload = payload, Hash = SqlSyncHash.Text(payload) }));
        using (var upgraded = Open())
        {
            var work = upgraded.Observe(lesson, task); Assert.Equal("SELECT 12;", work.Code);
            Assert.Equal("SELECT 12;", work.Journal!.BaseCode); Assert.Empty(work.Journal.Changes);
            upgraded.Edit(lesson, task.WorkScope, task.Id, "SELECT 13;", 0);
        }
        using var reopened = Open(); var journal = reopened.Find(lesson, task.WorkScope, task.Id)!.Journal!;
        Assert.Single(journal.Changes); Assert.Equal("SELECT 12;", journal.Replay(0)); Assert.Equal("SELECT 13;", journal.Replay());
        Assert.Equal(3, (int)JsonNode.Parse(File.ReadAllText(file))!["version"]!);
    }
    [Fact]
    public async Task SealedPagesRemainUnchangedAndEveryStateReplaysAfterReopening()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var root = Path.Combine(Path.GetTempPath(), "classroom-history-pages-" + Guid.NewGuid().ToString("N"));
        var texts = new List<string> { task.Draft.Length > 0 ? task.Draft : task.Starter };
        string firstPage; var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var local = new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId))
        {
            local.Observe(lesson, task);
            for (var i = 1; i <= 64; i++) { texts.Add("SELECT " + i + "; -- текст 😀"); local.Edit(lesson, task.WorkScope, task.Id, texts[^1], 0); }
            firstPage = Assert.Single(Directory.GetFiles(root, "p-*.json", SearchOption.AllDirectories));
            File.SetLastWriteTimeUtc(firstPage, sentinel);
            for (var i = 65; i <= 170; i++) { texts.Add("SELECT " + i + "; -- текст 😀"); local.Edit(lesson, task.WorkScope, task.Id, texts[^1], 0); }
            Assert.Equal(sentinel, File.GetLastWriteTimeUtc(firstPage));
            Assert.Equal(2, Directory.GetFiles(root, "p-*.json", SearchOption.AllDirectories).Length);
            var file = Assert.Single(Directory.GetFiles(root, "*.work.json", SearchOption.AllDirectories));
            var envelope = JsonNode.Parse(File.ReadAllText(file))!; Assert.Equal(3, (int)envelope["version"]!);
            var payload = JsonNode.Parse((string)envelope["payload"]!)!;
            Assert.Null(payload["journal"]); Assert.Equal(42, payload["journalIndex"]!["tail"]!.AsArray().Count);
        }
        using (var reopened = new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId))
        {
            var journal = reopened.Find(lesson, task.WorkScope, task.Id)!.Journal!;
            for (var i = 0; i < texts.Count; i++) Assert.Equal(texts[i], journal.Replay(i));
        }
        File.AppendAllText(firstPage, "tampered");
        Assert.Throws<SqlLocalRecoveryException>(() => new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId));
    }
    [Fact]
    public async Task InlineV2MigratesWithoutChangingHistoryAndBackupRestoresPagedHistory()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var root = Path.Combine(Path.GetTempPath(), "classroom-history-migrate-" + Guid.NewGuid().ToString("N"));
        SqlLocalStore Open() => new(root, f.Student.Connection.Fingerprint, f.StudentId);
        SqlLocalWork old;
        using (var local = Open())
        {
            local.Observe(lesson, task);
            for (var i = 1; i <= 70; i++) local.Edit(lesson, task.WorkScope, task.Id, "SELECT " + i + ";", 0);
            old = local.Find(lesson, task.WorkScope, task.Id)!;
        }
        var file = Assert.Single(Directory.GetFiles(root, "*.work.json", SearchOption.AllDirectories)); var payload = Wire.Write(old);
        File.WriteAllText(file, Wire.Write(new { Version = 2, ProfileId = f.StudentId, f.Student.Connection.Fingerprint, Payload = payload, Hash = SqlSyncHash.Text(payload) }));
        using (var local = Open())
        {
            Assert.Equal(Wire.Write(old.Journal), Wire.Write(local.Find(lesson, task.WorkScope, task.Id)!.Journal));
            local.Edit(lesson, task.WorkScope, task.Id, "SELECT 71;", 0); local.Edit(lesson, task.WorkScope, task.Id, "SELECT 72;", 0);
        }
        File.WriteAllText(file, "broken"); var candidate = Assert.Throws<SqlLocalRecoveryException>(() => Open()).Candidate;
        Assert.Equal("SELECT 71;", candidate.Backup!.Code); SqlLocalStore.RestoreBackup(candidate);
        using var restored = Open(); var work = restored.Find(lesson, task.WorkScope, task.Id)!;
        Assert.Equal("SELECT 71;", work.Journal!.Replay()); Assert.Equal("backup-restore", work.Journal.Changes[^1].Kind);
        Assert.Equal("SELECT 70;", work.Journal.Replay(70));
    }
}
