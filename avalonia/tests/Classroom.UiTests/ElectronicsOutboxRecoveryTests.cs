using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

internal sealed record RecoveryWork(string Root, string Project, ElectronicsHistoryContext Context, string Client, string Report, string Initial, string Latest)
{
    public string Queue => Project + ".sync.json";
    public string Header => Directory.GetFiles(Project + ".history", "c-*.json").Single();
}

public sealed class ElectronicsOutboxRecoveryTests
{
    internal static async Task<RecoveryWork> Create(LiveClassroom f)
    {
        var root = Path.Combine(Path.GetTempPath(), "classroom-outbox-recovery-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "electronics", SqlSyncHash.Text(f.Student.Connection.Fingerprint + ":" + f.StudentId) + ".json");
        var context = new ElectronicsHistoryContext(f.StudentId, f.Student.Connection.Fingerprint, f.Student.LessonId!, null);
        var project = CircuitProject.Blink(); var initial = project.Serialize(); var report = Guid.NewGuid().ToString("N"); string client;
        using (var sync = new ElectronicsSync(f.Student, file + ".sync.json", _ => { }, startTimer: false))
        {
            sync.QueueDraft(initial); sync.Record("submitted", initial, "Исходная сдача", reportId: report);
            project.Code = "// последние изменения\nvoid setup(){} void loop(){}"; sync.QueueDraft(project.Serialize()); client = sync.ClientId;
        }
        var latest = project.Serialize(); ElectronicsHistoryStore.Mirror(file, latest);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 2, latest, []));
        using (var history = new ElectronicsHistoryStore(file + ".history", context, initial))
        { history.Record(latest, "code-edit"); Assert.True(await history.Deliver(f.Student, client)); }
        return new(root, file, context, client, report, initial, latest);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRestoreKeepsOriginalIdsAndLatestHistoryWithoutRewritingBackup(bool missing)
    {
        await using var f = await LiveClassroom.Start(); var w = await Create(f);
        var backup = File.ReadAllBytes(w.Queue + ".backup"); var header = File.ReadAllBytes(w.Header); var project = File.ReadAllBytes(w.Project);
        if (missing) File.Delete(w.Queue); else File.WriteAllText(w.Queue, "broken original");
        var error = Assert.Throws<ElectronicsOutboxRecoveryException>(() => ElectronicsOutboxRecovery.Ensure(w.Project, w.Context, null));
        var candidate = error.Candidate; Assert.NotNull(candidate.Backup); Assert.Equal(w.Client, candidate.Backup.ClientId);
        Assert.Equal(w.Report, Assert.Single(candidate.Backup.Reports).Id);
        var archive = ElectronicsOutboxRecovery.Restore(candidate);
        if (missing) Assert.Null(archive); else Assert.Equal("broken original", File.ReadAllText(archive!));
        Assert.Equal(backup, File.ReadAllBytes(w.Queue + ".backup")); Assert.Equal(header, File.ReadAllBytes(w.Header)); Assert.Equal(project, File.ReadAllBytes(w.Project));
        var restored = ElectronicsSync.ReadOutbox(w.Queue, null, w.Client); Assert.True(restored.Dirty); Assert.Equal(w.Initial, Assert.Single(restored.Reports).ProjectJson);
        using (var sync = new ElectronicsSync(f.Student, w.Queue, _ => { }, startTimer: false, requireExisting: true, expectedClientId: w.Client))
        { sync.QueueDraft(w.Latest); Assert.True(await sync.Flush()); Assert.True(await sync.Flush()); }
        var teacher = await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}");
        Assert.Equal(w.Client, Assert.Single(teacher.Works).ClientId); Assert.Equal(w.Report, Assert.Single(teacher.Reports).Id);
        Assert.Equal(w.Initial, (await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/report/{w.Report}")).ProjectJson);
    }
    [Theory]
    [InlineData("source")]
    [InlineData("backup")]
    [InlineData("binding")]
    public async Task ChangesAfterPreviewCancelReplacement(string change)
    {
        await using var f = await LiveClassroom.Start(); var w = await Create(f); File.WriteAllText(w.Queue, "broken");
        var candidate = ElectronicsOutboxRecovery.Inspect(w.Project, w.Context, "test"); Assert.NotNull(candidate.Backup);
        if (change == "source") File.WriteAllText(w.Queue, "changed after preview");
        else if (change == "backup") File.AppendAllText(w.Queue + ".backup", " ");
        else { using var history = new ElectronicsHistoryStore(w.Project + ".history", w.Context, null); history.Record(w.Latest, "window-focus", true); }
        var source = File.ReadAllBytes(w.Queue); var backup = File.ReadAllBytes(w.Queue + ".backup"); var header = File.ReadAllBytes(w.Header);
        Assert.Throws<IOException>(() => ElectronicsOutboxRecovery.Restore(candidate));
        Assert.Equal(source, File.ReadAllBytes(w.Queue)); Assert.Equal(backup, File.ReadAllBytes(w.Queue + ".backup")); Assert.Equal(header, File.ReadAllBytes(w.Header));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(w.Queue)!, "*.damaged-*"));
    }
    [Theory]
    [InlineData("missing-backup")]
    [InlineData("invalid-backup")]
    [InlineData("different-client")]
    [InlineData("invalid-header")]
    public async Task UnverifiedBackupOrBindingNeverResetsOriginal(string damage)
    {
        await using var f = await LiveClassroom.Start(); var w = await Create(f); File.WriteAllText(w.Queue, "broken");
        if (damage == "missing-backup") File.Delete(w.Queue + ".backup");
        else if (damage == "invalid-backup") File.WriteAllText(w.Queue + ".backup", "broken backup");
        else if (damage == "invalid-header") File.WriteAllText(w.Header, "broken header");
        else { var other = ElectronicsSync.ReadOutbox(w.Queue + ".backup", null); other.ClientId = Guid.NewGuid().ToString("N"); File.WriteAllText(w.Queue + ".backup", Wire.Write(other)); }
        var candidate = ElectronicsOutboxRecovery.Inspect(w.Project, w.Context, "test"); Assert.Null(candidate.Backup);
        Assert.Throws<InvalidDataException>(() => ElectronicsOutboxRecovery.Restore(candidate)); Assert.Equal("broken", File.ReadAllText(w.Queue));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(w.Queue)!, "*.damaged-*"));
        Assert.Single((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works);
    }
    [Fact]
    public async Task ForeignOwnerAndHeldLeasePreventRestore()
    {
        await using var f = await LiveClassroom.Start(); var w = await Create(f); File.WriteAllText(w.Queue, "broken");
        var candidate = ElectronicsOutboxRecovery.Inspect(w.Project, w.Context, "test"); Assert.NotNull(candidate.Backup);
        Assert.Throws<InvalidDataException>(() => ElectronicsOutboxRecovery.Restore(candidate with { Context = w.Context with { ProfileId = Guid.NewGuid().ToString("N") } }));
        using (var editor = new ElectronicsHistoryStore(w.Project + ".history", w.Context, null)) Assert.Throws<IOException>(() => ElectronicsOutboxRecovery.Restore(candidate));
        Assert.Equal("broken", File.ReadAllText(w.Queue)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(w.Queue)!, "*.damaged-*"));
    }
}
