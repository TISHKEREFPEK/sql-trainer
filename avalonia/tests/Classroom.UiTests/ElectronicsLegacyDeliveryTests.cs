using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsLegacyDeliveryTests
{
    static string Root() => Path.Combine(Path.GetTempPath(), "classroom-legacy-queue-" + Guid.NewGuid().ToString("N"));
    static string ProjectPath(string root, ClassroomApi api, string? assignment = null) => Path.Combine(root, "electronics",
        SqlSyncHash.Text(api.Connection.Fingerprint + ":" + api.ProfileId) + (assignment is null ? "" : "-" + assignment) + ".json");
    internal static string[] SaveLegacy(string path, ClassroomApi api, string? assignment = null, int reports = 1, int? version = null)
    {
        var project = CircuitProject.Blink(); project.Code = "// прежняя работа\nvoid setup(){} void loop(){}";
        var json = project.Serialize(); ElectronicsHistoryStore.Mirror(path, json);
        using var sync = new ElectronicsSync(api, path + ".sync.json", _ => { }, startTimer: false, assignmentId: assignment);
        sync.QueueDraft(json); var ids = Enumerable.Range(0, reports).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var id in ids) sync.Record(assignment is null ? "compiled" : "submitted", json, "Прежний результат", version, id);
        return ids;
    }
    [Fact]
    public async Task OldFreeOutboxDrainsMoreThanOnePacketWithoutInventingHistoryOrChangingTheProject()
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var path = ProjectPath(root, f.Student); var ids = SaveLegacy(path, f.Student, reports: 23);
        Assert.False(Directory.Exists(path + ".history")); var original = File.ReadAllBytes(path);
        var old = Wire.Read<ElectronicsSync.Outbox>(File.ReadAllText(path + ".sync.json"));
        // Exact pre-history shape from 1fc52ab: neither AssignmentId nor
        // ExpectedAssignmentVersion existed in those files.
        File.WriteAllText(path + ".sync.json", Wire.Write(new { old.ClientId, old.Sequence, old.ProjectJson, old.Dirty,
            Reports = old.Reports.Select(r => new { r.Id, r.Kind, r.ProjectJson, r.Detail }).ToArray() }));
        await using (var queue = new ElectronicsBackgroundDelivery(root, f.Student))
        {
            var first = await queue.DrainOnce(); Assert.False(first.Complete); Assert.Null(first.Error);
            Assert.Equal(13, Wire.Read<ElectronicsSync.Outbox>(File.ReadAllText(path + ".sync.json")).Reports.Count);
            for (var i = 0; i < 4; i++) Assert.Null((await queue.DrainOnce()).Error);
        }
        var saved = Wire.Read<ElectronicsSync.Outbox>(File.ReadAllText(path + ".sync.json"));
        Assert.Equal(old.ClientId, saved.ClientId); Assert.Equal(old.Sequence, saved.Sequence); Assert.Empty(saved.Reports); Assert.False(saved.Dirty);
        var backup = Wire.Read<ElectronicsSync.Outbox>(File.ReadAllText(path + ".sync.json.backup"));
        Assert.Equal(old.ClientId, backup.ClientId); Assert.Equal(3, backup.Reports.Count); Assert.All(backup.Reports, r => Assert.Contains(r.Id, ids));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("open.lock", Path.GetFileName(Assert.Single(Directory.GetFiles(path + ".history"))));
        Assert.Empty(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}"));
        await using (var restarted = new ElectronicsBackgroundDelivery(root, f.Student)) Assert.True((await restarted.DrainOnce()).Complete);
        var teacher = await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}");
        Assert.Equal(old.ClientId, Assert.Single(teacher.Works).ClientId); Assert.Equal(ids.Order(), teacher.Reports.Select(r => r.Id).Order());
    }
    [Fact]
    public async Task OldAssignedSubmissionKeepsItsOriginalLessonAndPermissionAfterANewSqlOnlyLessonStarts()
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var lesson = f.Student.LessonId!; var task = Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + task, new ElectronicsTaskRequest("Прежняя схема", "Условие"));
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson}/electronics-tasks", new ElectronicsSelectionRequest([task]));
        var assigned = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + task); var path = ProjectPath(root, f.Student, assigned.Id);
        var id = Assert.Single(SaveLegacy(path, f.Student, assigned.Id, version: assigned.StateVersion));
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/finish");
        var current = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, AllowElectronics: false, OpenToAllGroups: true)));
        await f.Admin.Post<LessonView>($"teacher/lessons/{current.Id}/start"); await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        await using (var queue = new ElectronicsBackgroundDelivery(root, f.Student)) Assert.True((await queue.DrainOnce()).Complete);
        var item = Assert.Single((await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics")).Items);
        Assert.Equal(id, item.ReportId); Assert.Equal(assigned.Id, item.AssignmentId); Assert.Equal(lesson, item.LessonId);
        var backup = Wire.Read<ElectronicsSync.Outbox>(File.ReadAllText(path + ".sync.json.backup")); Assert.Equal(assigned.StateVersion, Assert.Single(backup.Reports).ExpectedAssignmentVersion);
        Assert.Empty(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}"));
    }
    [Theory]
    [InlineData("p-fragment.json")]
    [InlineData("c-index.json.backup")]
    [InlineData("c-index.json.tmp-interrupted")]
    public async Task IncompleteHistoryIsNotMisidentifiedAsALegacyQueue(string fragmentName)
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var path = ProjectPath(root, f.Student); SaveLegacy(path, f.Student);
        Directory.CreateDirectory(path + ".history"); var fragment = Path.Combine(path + ".history", fragmentName); File.WriteAllText(fragment, "original fragment");
        var original = File.ReadAllBytes(path + ".sync.json");
        await using var queue = new ElectronicsBackgroundDelivery(root, f.Student); var result = await queue.DrainOnce();
        Assert.False(result.Complete); Assert.Contains("без исходного индекса", result.Error); Assert.Equal("original fragment", File.ReadAllText(fragment));
        Assert.Equal(original, File.ReadAllBytes(path + ".sync.json")); Assert.Empty((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works);
    }
    [Theory]
    [InlineData("missing-client")]
    [InlineData("wrong-assignment")]
    [InlineData("empty-snapshot")]
    public async Task IncompleteLegacyIdentityOrWrongTargetCannotCreateARandomNewServerCopy(string damage)
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var path = ProjectPath(root, f.Student); SaveLegacy(path, f.Student);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path + ".sync.json"))!.AsObject();
        if (damage == "missing-client") node.Remove("clientId");
        else if (damage == "wrong-assignment") node["assignmentId"] = Guid.NewGuid().ToString("N");
        else node["projectJson"] = "";
        var original = node.ToJsonString(); File.WriteAllText(path + ".sync.json", original);
        await using var queue = new ElectronicsBackgroundDelivery(root, f.Student); var result = await queue.DrainOnce();
        Assert.False(result.Complete); Assert.NotNull(result.Error); Assert.Equal(original, File.ReadAllText(path + ".sync.json"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"));
        Assert.Empty((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works);
    }
}
