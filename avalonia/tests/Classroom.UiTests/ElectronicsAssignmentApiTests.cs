using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsAssignmentApiTests
{
    static async Task<string> Create(LiveClassroom f, string title, string? starter = null)
    {
        var id = Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + id, new ElectronicsTaskRequest(title, "Соберите схему и подготовьте код.", starter));
        return id;
    }
    static async Task<LessonView> Issue(LiveClassroom f, params string[] ids)
    {
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks", new ElectronicsSelectionRequest(ids));
        return lesson;
    }

    [Fact]
    public async Task IssuedConditionAndStarterStayFrozenAndStudentsHaveSeparateAssignments()
    {
        await using var f = await LiveClassroom.Start();
        var initial = CircuitProject.Blink(); initial.Code = "// начальная программа";
        var id = await Create(f, "Практика с Uno", initial.Serialize());
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id))).Status);
        var lesson = await Issue(f, id);
        var newer = initial with { Code = "// новая редакция" };
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + id, new ElectronicsTaskRequest("Новый заголовок", "Другое условие", newer.Serialize(), ExpectedRevision: 1));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + id, new ElectronicsTaskRequest("Устаревшая правка", "Условие", ExpectedRevision: 1)))).Status);
        var work = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id);
        Assert.Equal("Практика с Uno", work.Title); Assert.Equal(1, work.Revision);
        Assert.Equal(initial.Code, CircuitProject.Parse(work.ProjectJson).Code);
        Assert.Equal(work.Id, (await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id)).Id);
        await f.Admin.Post<JsonElement>("teacher/students", new CreateStudentRequest("other-circuit", "synthetic-other-password", null));
        using var other = new ClassroomApi(f.Admin.Connection); await other.Login(new("student", "other-circuit", "synthetic-other-password"));
        var second = await other.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id);
        Assert.NotEqual(work.Id, second.Id); Assert.Equal(initial.Serialize(), second.ProjectJson);
        var edited = initial with { Code = "// работа первого ученика" };
        var request = new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, edited.Serialize(), [], work.Id);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", request);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => other.Put<ElectronicsSyncReceipt>("electronics/work", request))).Status);
        Assert.Equal(initial.Serialize(), (await other.Get<ElectronicsAssignmentDto>("electronics/assignments/" + second.Id)).ProjectJson);
        Assert.Equal(edited.Serialize(), (await f.Student.Get<ElectronicsAssignmentDto>("electronics/assignments/" + work.Id)).ProjectJson);
        await f.Admin.Delete<JsonElement>("teacher/electronics/tasks/" + id);
        Assert.Empty(await f.Admin.Get<ElectronicsTaskSummary[]>("teacher/electronics/tasks"));
        Assert.Equal(work.Id, (await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id)).Id);
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks", new ElectronicsSelectionRequest([]));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id))).Status);
        Assert.False((await f.Student.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + work.Id + "/status")).Available);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", request with { Sequence = 2, ProjectJson = newer.Serialize() });
        Assert.Equal(newer.Serialize(), (await f.Student.Get<ElectronicsAssignmentDto>("electronics/assignments/" + work.Id)).ProjectJson);
    }

    [Fact]
    public async Task SubmissionIsImmutableAndRetrySafeAndPreservedAcrossRestore()
    {
        await using var f = await LiveClassroom.Start();
        var id = await Create(f, "Сдать схему", CircuitProject.Blink().Serialize()); await Issue(f, id);
        var assignment = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id);
        var client = Guid.NewGuid().ToString("N"); var reportId = Guid.NewGuid().ToString("N");
        var original = CircuitProject.Parse(assignment.ProjectJson); original.Code = "// сданная версия";
        var request = new ElectronicsSyncRequest(client, 1, original.Serialize(), [new(reportId, "submitted", original.Serialize(), "")], assignment.Id);
        var receipt = await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", request);
        Assert.Equal("awaiting-review", receipt.AssignmentState); Assert.False(receipt.CanSubmit); Assert.Equal(2, receipt.AssignmentVersion);
        Assert.Equal(receipt.AssignmentVersion, (await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", request)).AssignmentVersion);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<ElectronicsSyncReceipt>("electronics/work", request with { Reports = [new(Guid.NewGuid().ToString("N"), "submitted", original.Serialize(), "")] }))).Status);
        var changed = original with { Code = "// продолжение работы после сдачи" };
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", request with { Sequence = 2, ProjectJson = changed.Serialize(), Reports = [] });
        var snapshotPath = $"teacher/electronics/{f.StudentId}/report/{reportId}";
        Assert.Equal(original.Serialize(), (await f.Admin.Get<ElectronicsSnapshot>(snapshotPath)).ProjectJson);
        Assert.Equal(changed.Serialize(), (await f.Student.Get<ElectronicsAssignmentDto>("electronics/assignments/" + assignment.Id)).ProjectJson);
        Assert.Equal("awaiting-review", (await f.Student.Get<StudentHomeDto>("student/home")).Electronics!.Single().State);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<ElectronicsTaskSummary[]>("teacher/electronics/tasks"))).Status);
        var backup = await f.Admin.Post<JsonElement>("teacher/backups");
        await f.Admin.Post<JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        Assert.Equal(original.Serialize(), (await f.Admin.Get<ElectronicsSnapshot>(snapshotPath)).ProjectJson);
    }

    [Fact]
    public async Task EmptyStarterAndManyDifferentAssignmentsDoNotShareDraftsOrDeviceQuota()
    {
        await using var f = await LiveClassroom.Start();
        var ids = new List<string>(); for (var i = 0; i < 11; i++) ids.Add(await Create(f, "Схема " + i));
        await Issue(f, ids.ToArray());
        var client = Guid.NewGuid().ToString("N");
        foreach (var id in ids)
        {
            var assignment = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id);
            var empty = CircuitProject.Parse(assignment.ProjectJson); Assert.Empty(empty.Parts); Assert.Empty(empty.Code);
            await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, assignment.ProjectJson, [], assignment.Id));
        }
        var first = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + ids[0]);
        var second = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + ids[1]);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 1, first.ProjectJson, [], first.Id));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 2, second.ProjectJson, [], second.Id)))).Status);
        Assert.Equal(11, (await f.Student.Get<StudentHomeDto>("student/home")).Electronics!.Length);
    }
}
