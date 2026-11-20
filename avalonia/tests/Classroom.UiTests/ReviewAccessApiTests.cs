using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ReviewAccessApiTests
{
    [Fact]
    public async Task CodeScopesReviewToOnePupilWithoutGrantingTeacherRightsAndWorksDuringPause()
    {
        await using var f = await LiveClassroom.Start();
        var report = Guid.NewGuid().ToString("N"); var json = CircuitProject.Blink().Serialize();
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, json, [new(report, "submitted", json, "")]));
        await f.Admin.Post<JsonElement>("teacher/students", new CreateStudentRequest("scope-other", "synthetic-other-password", null));
        using var other = new ClassroomApi(f.Admin.Connection); await other.Login(new("student", "scope-other", "synthetic-other-password"));
        var code = await f.Admin.Post<ReviewAccessCode>("teacher/review-access", new CreateReviewAccess(f.StudentId));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => other.StartReview(code.Code))).Status);
        var (review, _) = await f.Student.StartReview(code.Code); using var scoped = review;
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.StartReview(code.Code))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => scoped.Get<TeacherOverview>("teacher/overview"))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => scoped.Post<ReviewAccessCode>("teacher/review-access", new CreateReviewAccess(f.StudentId)))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<ElectronicsReviewQueue>("review-access/electronics"))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => scoped.Get<ElectronicsReviewDetail>("review-access/electronics/another-pupil/" + report))).Status);
        var pending = await scoped.Get<ElectronicsReviewQueue>("review-access/electronics?status=all");
        Assert.Equal(f.StudentId, Assert.Single(pending.Items).StudentId);
        var invalidPage = await Assert.ThrowsAsync<ApiException>(() => scoped.Get<ElectronicsReviewQueue>("review-access/electronics?offset=-1"));
        Assert.Equal(400, invalidPage.Status); Assert.Equal("Некорректный фильтр очереди.", invalidPage.Message);
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause");
        var detail = await scoped.Get<ElectronicsReviewDetail>($"review-access/electronics/{f.StudentId}/{report}");
        await scoped.Put<ElectronicsReviewDecision>($"review-access/electronics/{f.StudentId}/{report}/decisions/" + Guid.NewGuid().ToString("N"), new ElectronicsReviewRequest("accepted", "Проверено у ученика.", 4, null, null));
        Assert.Equal(json, detail.ProjectJson);
        Assert.Equal(4, (await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}")).Item.Grade);
        Assert.Empty((await scoped.Get<ElectronicsReviewQueue>("review-access/electronics")).Items);
        await scoped.EndReview();
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => scoped.Get<ReviewAccessStatus>("review-access/status"))).Status);
        Assert.NotNull(await f.Student.Get<StudentLessonDto>("student/lesson"));
    }
    [Fact]
    public async Task RevocationTeacherLogoutAndRestoreInvalidateCodesAndSessions()
    {
        await using var f = await LiveClassroom.Start();
        var code = await f.Admin.Post<ReviewAccessCode>("teacher/review-access", new CreateReviewAccess(f.StudentId));
        var (review, _) = await f.Student.StartReview(code.Code); using var scoped = review;
        await f.Admin.Delete<JsonElement>("teacher/review-access/" + code.Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => scoped.Get<ReviewAccessStatus>("review-access/status"))).Status);
        var unused = await f.Admin.Post<ReviewAccessCode>("teacher/review-access", new CreateReviewAccess(f.StudentId));
        await f.Admin.Delete<JsonElement>("teacher/review-access/" + unused.Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.StartReview(unused.Code))).Status);
        var issued = await f.Admin.Post<ReviewAccessCode>("teacher/review-access", new CreateReviewAccess(f.StudentId));
        var (second, _) = await f.Student.StartReview(issued.Code); using var secondScope = second;
        await f.Admin.Logout();
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => secondScope.Get<ReviewAccessStatus>("review-access/status"))).Status);
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        var backup = await f.Admin.Post<JsonElement>("teacher/backups");
        var beforeRestore = await f.Admin.Post<ReviewAccessCode>("teacher/review-access", new CreateReviewAccess(f.StudentId));
        await f.Admin.Post<JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/restore");
        await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.StartReview(beforeRestore.Code))).Status);
    }
}
