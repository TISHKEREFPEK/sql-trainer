using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsJournalDeliveryTests
{
    [Fact]
    public async Task PagedHistoryAndAnEditDuringUploadKeepTheNewLocalState()
    {
        await using var f = await LiveClassroom.Start(); var project = CircuitProject.Blink(); var initial = project.Serialize(); var client = Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 1, initial, []));
        var root = Path.Combine(Path.GetTempPath(), "classroom-electronics-many-" + Guid.NewGuid().ToString("N")); var context = new ElectronicsHistoryContext(f.StudentId, f.Student.Connection.Fingerprint, f.Student.LessonId!, null);
        using var local = new ElectronicsHistoryStore(root, context, initial);
        for (var i = 1; i <= 130; i++) { project.Code = "// шаг " + i; local.Record(project.Serialize(), "code-edit"); }
        var sending = local.Deliver(f.Student, client); Assert.False(sending.IsCompleted);
        project.Code = "// новое во время передачи"; var newest = project.Serialize(); local.Record(newest, "code-edit"); await sending;
        Assert.Equal(newest, local.ProjectJson);
        for (var i = 0; i < 10 && local.Pending; i++) await local.Deliver(f.Student, client);
        Assert.False(local.Pending); Assert.Equal(131, local.DeliveryState!.Acknowledged);
        Assert.Equal(newest, (await f.Admin.Get<ElectronicsJournalState>($"teacher/electronics-journal/{f.StudentId}/{local.DeliveryState.StreamId}/state?sequence=131")).ProjectJson);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedDeliveryAndRestoredCopyKeepBothServerBranches(bool sameFinalState)
    {
        await using var f = await LiveClassroom.Start(); var project = CircuitProject.Blink(); var initial = project.Serialize(); var client = Guid.NewGuid().ToString("N"); var lesson = f.Student.LessonId!;
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 1, initial, []));
        var root = Path.Combine(Path.GetTempPath(), "classroom-electronics-delivery-" + Guid.NewGuid().ToString("N")); var context = new ElectronicsHistoryContext(f.StudentId, f.Student.Connection.Fingerprint, lesson, null); string file, older, original;
        using (var local = new ElectronicsHistoryStore(root, context, initial))
        {
            project.Code = "// 1"; local.Record(project.Serialize(), "code-edit"); Assert.True(await local.Deliver(f.Student, client));
            file = Assert.Single(Directory.GetFiles(root, "c-*.json")); older = File.ReadAllText(file); original = local.DeliveryState!.StreamId;
            project.Code = sameFinalState ? "// восстановленная ветвь" : "// 2"; local.Record(project.Serialize(), "code-edit");
            if (!sameFinalState) { project.Code = "// 3"; local.Record(project.Serialize(), "code-edit"); }
            Assert.True(await local.Deliver(f.Student, client));
        }
        File.WriteAllText(file, older); string fork;
        using (var restored = new ElectronicsHistoryStore(root, context, null))
        {
            project.Code = "// восстановленная ветвь"; restored.Record(project.Serialize(), "code-edit"); Assert.True(await restored.Deliver(f.Student, client));
            fork = restored.DeliveryState!.StreamId; Assert.NotEqual(original, fork); Assert.Equal(original, restored.DeliveryState.ForkedFrom);
        }
        using (var reopened = new ElectronicsHistoryStore(root, context, null)) { Assert.True(await reopened.Deliver(f.Student, client)); Assert.False(reopened.Pending); }
        Assert.Equal(2, (await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}")).Length);
        Assert.Equal(sameFinalState ? "// восстановленная ветвь" : "// 3", CircuitProject.Parse((await f.Admin.Get<ElectronicsJournalState>($"teacher/electronics-journal/{f.StudentId}/{original}/state?sequence={(sameFinalState ? 2 : 3)}")).ProjectJson).Code);
        Assert.Equal(project.Serialize(), (await f.Admin.Get<ElectronicsJournalState>($"teacher/electronics-journal/{f.StudentId}/{fork}/state?sequence=2")).ProjectJson);
        var originalSteps = await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{original}/changes");
        var forkSteps = await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{fork}/changes"); Assert.NotEqual(originalSteps.Events[^1].Change.Id, forkSteps.Events[^1].Change.Id);
    }
}
