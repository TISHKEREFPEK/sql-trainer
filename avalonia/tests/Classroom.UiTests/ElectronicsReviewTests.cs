using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsReviewTests
{
    static async Task<ElectronicsAssignmentDto> Assigned(LiveClassroom f)
    {
        var id = Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + id, new ElectronicsTaskRequest("Светодиод", "Соберите и объясните работу цепи.", CircuitProject.Blink().Serialize()));
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks", new ElectronicsSelectionRequest([id]));
        return await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + id);
    }
    static string Route(LiveClassroom f, string id) => $"teacher/reviews/electronics/{f.StudentId}/{id}";
    static async Task<ElectronicsReviewDecision> Decide(LiveClassroom f, string reportId, string action, int? grade, string comment = "")
    {
        var detail = await f.Admin.Get<ElectronicsReviewDetail>(Route(f, reportId));
        return await f.Admin.Put<ElectronicsReviewDecision>(Route(f, reportId) + "/decisions/" + Guid.NewGuid().ToString("N"),
            new ElectronicsReviewRequest(action, comment, grade, detail.History.LastOrDefault()?.Id, detail.AssignmentVersion));
    }

    [Fact]
    public async Task QueueIncludesAssignedAndLegacySnapshotsAndReviewHistorySurvivesRestore()
    {
        await using var f = await LiveClassroom.Start();
        var assignment = await Assigned(f); var assignedId = Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, assignment.ProjectJson, [new(assignedId, "submitted", assignment.ProjectJson, "", 1)], assignment.Id));
        var free = CircuitProject.Blink(); free.Name = "Свободная схема"; var legacyId = Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, free.Serialize(), [new(legacyId, "submitted", free.Serialize(), "")]));
        var queue = await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics");
        Assert.Equal(2, queue.PendingCount); Assert.Equal(2, queue.Items.Length);
        Assert.Equal("Светодиод", queue.Items.Single(r => r.ReportId == assignedId).Title);
        var filters = await f.Admin.Get<ElectronicsReviewFilters>("teacher/reviews/electronics/filters");
        Assert.Contains(filters.Lessons, l => l.Id == assignment.LessonId);
        Assert.Equal(2, (await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?groupId=none")).Items.Length);
        Assert.Single((await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?lessonId=" + assignment.LessonId)).Items);
        Assert.Null(queue.Items.Single(r => r.ReportId == legacyId).LessonId);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<ElectronicsReviewQueue>("teacher/reviews/electronics"))).Status);
        await Decide(f, legacyId, "accepted", 4, "Схема верная.");
        var first = await Decide(f, assignedId, "accepted", 5, "Принято.");
        await Decide(f, assignedId, "accepted", 4, "Уточнена оценка после проверки кода.");
        var detail = await f.Student.Get<ElectronicsReviewDetail>("electronics/reviews/" + assignedId);
        Assert.Equal(2, detail.History.Length); Assert.Equal(first, detail.History[0]); Assert.Equal(4, detail.Item.Grade);
        Assert.Equal(assignment.ProjectJson, detail.ProjectJson);
        Assert.Empty((await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics")).Items);
        Assert.Equal(2, (await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?status=all")).Items.Length);
        var backup = await f.Admin.Post<JsonElement>("teacher/backups");
        await f.Admin.Post<JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        var restored = await f.Admin.Get<ElectronicsReviewDetail>(Route(f, assignedId));
        Assert.Equal(detail.History, restored.History); Assert.Equal(detail.ProjectJson, restored.ProjectJson);
    }

    [Fact]
    public async Task DecisionsAndResubmissionsAreOptimisticIdempotentAndBoundToPermission()
    {
        await using var f = await LiveClassroom.Start(); var assignment = await Assigned(f);
        var client = Guid.NewGuid().ToString("N"); var report = Guid.NewGuid().ToString("N");
        var request = new ElectronicsSyncRequest(client, 1, assignment.ProjectJson, [new(report, "submitted", assignment.ProjectJson, "", 1)], assignment.Id);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", request);
        var route = Route(f, report); var id = Guid.NewGuid().ToString("N");
        var decision = new ElectronicsReviewRequest("accepted", "Всё верно.", 5, null, 2);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Put<ElectronicsReviewDecision>(route + "/decisions/" + id, decision with { Grade = 6 }))).Status);
        var original = await f.Admin.Put<ElectronicsReviewDecision>(route + "/decisions/" + id, decision);
        Assert.Equal(original, await f.Admin.Put<ElectronicsReviewDecision>(route + "/decisions/" + id, decision));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Put<ElectronicsReviewDecision>(route + "/decisions/" + id, decision with { Grade = 4 }))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Put<ElectronicsReviewDecision>(route + "/decisions/" + Guid.NewGuid().ToString("N"), decision))).Status);
        await Decide(f, report, "allow-resubmit", null);
        var status = await f.Student.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + assignment.Id + "/status");
        Assert.True(status.CanSubmit); Assert.Equal(4, status.StateVersion); Assert.Equal(5, status.LatestReview!.Grade);
        // An old not-yet-accepted intent cannot consume the new grant.
        var newReport = Guid.NewGuid().ToString("N");
        var stale = request with { Reports = [new(newReport, "submitted", assignment.ProjectJson, "", 1)] };
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<ElectronicsSyncReceipt>("electronics/work", stale))).Status);
        Assert.True((await f.Student.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + assignment.Id + "/status")).CanSubmit);
        // ACK for an existing submission remains valid after grading, without consuming permission.
        Assert.True((await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", request)).CanSubmit);
        var current = stale with { Reports = [new(newReport, "submitted", assignment.ProjectJson, "", status.StateVersion)] };
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", current);
        Assert.False((await f.Student.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + assignment.Id + "/status")).CanSubmit);
        await Decide(f, newReport, "changes-requested", null, "Исправьте подключение.");
        var returned = await f.Student.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + assignment.Id + "/status");
        Assert.Equal("changes-requested", returned.State); Assert.True(returned.CanSubmit);
        // Regrading an older snapshot must not overwrite the state of the newer one.
        await Decide(f, report, "rejected", 2, "Старая версия.");
        Assert.Equal(returned, await f.Student.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + assignment.Id + "/status"));
    }

    [Fact]
    public async Task ForeignStudentCannotReadReviewAndSubmissionIntentKeepsItsVersionOnDisk()
    {
        await using var f = await LiveClassroom.Start(); var assignment = await Assigned(f);
        var directory = Path.Combine(Path.GetTempPath(), "classroom-review-outbox-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "work.sync.json");
        using (var sync = new ElectronicsSync(f.Student, path, _ => { }, false, assignment.Id))
        { sync.QueueDraft(assignment.ProjectJson); sync.Record("submitted", assignment.ProjectJson, expectedAssignmentVersion: assignment.StateVersion); }
        var saved = Wire.Read<ElectronicsSync.Outbox>(File.ReadAllText(path));
        Assert.Equal(1, Assert.Single(saved.Reports).ExpectedAssignmentVersion);
        using (var sync = new ElectronicsSync(f.Student, path, _ => { }, false, assignment.Id)) Assert.True(await sync.Flush());
        var report = saved.Reports[0].Id;
        await f.Admin.Post<JsonElement>("teacher/students", new CreateStudentRequest("review-other", "synthetic-other-password", null));
        using var other = new ClassroomApi(f.Admin.Connection); await other.Login(new("student", "review-other", "synthetic-other-password"));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => other.Get<ElectronicsReviewDetail>("electronics/reviews/" + report))).Status);
        // IDs are scoped to a pupil. Reusing one must not leak another pupil's comment.
        var otherWork = await other.Get<ElectronicsAssignmentDto>("electronics/tasks/" + assignment.TaskId);
        await other.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, otherWork.ProjectJson, [new(report, "submitted", otherWork.ProjectJson, "", 1)], otherWork.Id));
        await Decide(f, report, "accepted", 5, "Комментарий только первому ученику.");
        Assert.Null((await other.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + otherWork.Id + "/status")).LatestReview);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<ElectronicsReviewDecision>(Route(f, report) + "/decisions/" + Guid.NewGuid().ToString("N"), new ElectronicsReviewRequest("accepted", "", 5, null, 2)))).Status);
        // An explicit replacement archives the frozen old intent, including its original version.
        using (var sync = new ElectronicsSync(f.Student, path, _ => { }, false, assignment.Id))
        {
            sync.QueueDraft(assignment.ProjectJson); sync.Record("submitted", assignment.ProjectJson, expectedAssignmentVersion: 2);
            Assert.True(sync.HasStaleSubmission(4)); sync.ArchiveStaleSubmissions(4);
            Assert.False(sync.PendingSubmission); sync.Record("submitted", assignment.ProjectJson, expectedAssignmentVersion: 4);
            Assert.Equal(4, Wire.Read<ElectronicsSync.Outbox>(File.ReadAllText(path)).Reports.Single().ExpectedAssignmentVersion);
            Assert.Single(Directory.GetFiles(directory, "*.superseded-*.json"));
        }
    }
}
