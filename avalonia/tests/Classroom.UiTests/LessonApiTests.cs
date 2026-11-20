using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Xunit;

namespace Classroom.UiTests;

public sealed class LessonApiTests
{
    [Fact]
    public async Task AdmissionRegistrationGroupMembershipPauseAndOldTokensAreIndependent()
    {
        await using var f = await LiveClassroom.Start();
        var initial = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Post<LessonView>($"teacher/lessons/{initial.Id}/finish");
        var first = await f.Admin.Post<GroupDto>("teacher/groups", new GroupRequest("191"));
        var second = await f.Admin.Post<GroupDto>("teacher/groups", new GroupRequest("192"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group", new GroupMembershipRequest(first.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name", new StudentNameRequest("Иванов Иван"));
        var other = await f.Admin.Post<JsonElement>("teacher/students", new CreateStudentRequest("second", "synthetic-other-password", second.Id));
        var otherId = other.GetProperty("id").GetString()!;
        await f.Admin.Put<StudentChoice>($"teacher/students/{otherId}/name", new StudentNameRequest("Иванов Иван"));
        using var anonymous = new ClassroomApi(f.Admin.Connection);
        using var named = new ClassroomApi(f.Admin.Connection);
        Assert.False((await anonymous.Get<AdmissionDto>("admission")).AdmissionOpen);
        Assert.Equal(423, (await Assert.ThrowsAsync<ApiException>(() => named.Login(new("student", "ui-test", "synthetic-student-password")))).Status);

        var policy = new LessonPolicyRequest(first.Id);
        var request = new CreateLessonRequest(Guid.NewGuid().ToString("N"), policy);
        var lesson = await f.Admin.Post<LessonView>("teacher/lessons", request);
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest(["database"]));
        Assert.Equal(lesson.Id, (await f.Admin.Post<LessonView>("teacher/lessons", request)).Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Post<LessonView>("teacher/lessons", request with { Id = Guid.NewGuid().ToString("N") }))).Status);
        Assert.Empty((await anonymous.Get<AdmissionDto>("admission")).Groups);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/start");
        Assert.Single((await anonymous.Get<AdmissionDto>("admission")).Groups);
        var roster = await anonymous.Get<StudentChoice[]>($"admission/students?groupId={first.Id}");
        Assert.Equal("Иванов Иван", Assert.Single(roster).Name);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => anonymous.Get<StudentChoice[]>($"admission/students?groupId={second.Id}"))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => named.Login(new("student", "second", "synthetic-other-password")))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => named.LoginStudent(new(first.Id, f.StudentId, "wrong-password")))).Status);
        var registration = new StudentRegistrationRequest(Guid.NewGuid().ToString("N"), "Петров Пётр", "synthetic-new-password");
        Assert.Equal(423, (await Assert.ThrowsAsync<ApiException>(() => anonymous.Post<StudentChoice>("student-registration", registration))).Status);
        policy = policy with { RegistrationOpen = true, AdmissionOpen = false };
        await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy", policy);
        var created = await anonymous.Post<StudentChoice>("student-registration", registration);
        Assert.Equal(created.Id, (await anonymous.Post<StudentChoice>("student-registration", registration)).Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => anonymous.Post<StudentChoice>("student-registration", registration with { FullName = "Другой ученик" }))).Status);
        Assert.Equal(first.Id, (await f.Admin.Get<TeacherOverview>("teacher/overview")).Students.Single(s => s.Id == created.Id).GroupId);
        Assert.Equal(423, (await Assert.ThrowsAsync<ApiException>(() => named.LoginStudent(new(first.Id, f.StudentId, "synthetic-student-password")))).Status);
        policy = policy with { AdmissionOpen = true };
        await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy", policy);
        await named.LoginStudent(new(first.Id, f.StudentId, "synthetic-student-password"));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => named.Get<LessonDashboard>("teacher/lessons"))).Status);
        await named.Get<StudentTask>("tasks/database");
        var paused = await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause");
        Assert.Equal(paused.PausedAt, (await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause")).PausedAt);
        Assert.False((await named.Get<StudentLessonDto>("student/lesson")).CanWork);
        var execution = new ExecuteRequest(Guid.NewGuid().ToString(), "SELECT 1;", false);
        Assert.Equal(423, (await Assert.ThrowsAsync<ApiException>(() => named.Post<ExecutionDto>("tasks/database/execute", execution))).Status);
        Assert.Equal(423, (await Assert.ThrowsAsync<ApiException>(() => named.Post<StudentTask>("tasks/database/hint", new HintRequest(0)))).Status);
        await named.Put<JsonElement>("tasks/database/draft", new DraftRequest("SELECT 17; -- retained"));
        await named.Post<JsonElement>("tasks/database/activity", new ActivityRequest(2));
        Assert.Equal(0, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Record.ActiveSeconds);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/resume");
        Assert.Equal("SELECT 17; -- retained", (await named.Get<StudentTask>("tasks/database")).Draft);
        Assert.Null((await named.Post<ExecutionDto>("tasks/database/execute", execution)).Error);
        policy = policy with { AllowSql = false, OpenToAllGroups = true };
        await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy", policy);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => named.Post<ExecutionDto>("tasks/database/execute", execution with { OperationId = Guid.NewGuid().ToString() }))).Status);
        using var mixed = new ClassroomApi(f.Admin.Connection);
        await mixed.LoginStudent(new(second.Id, otherId, "synthetic-other-password"));
        Assert.Equal(second.Id, (await f.Admin.Get<TeacherOverview>("teacher/overview")).Students.Single(s => s.Id == otherId).GroupId);
        policy = policy with { AllowSql = true, AdmissionOpen = false, OpenToAllGroups = false };
        await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy", policy);
        Assert.Null((await mixed.Post<ExecutionDto>("tasks/database/execute", execution)).Error);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/finish");
        Assert.Equal(423, (await Assert.ThrowsAsync<ApiException>(() => mixed.Post<ExecutionDto>("tasks/database/execute", execution with { OperationId = Guid.NewGuid().ToString() }))).Status);
        var next = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(second.Id)));
        await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start");
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/finish");
        Assert.True((await anonymous.Get<AdmissionDto>("admission")).AdmissionOpen);
        Assert.False((await mixed.Get<StudentLessonDto>("student/lesson")).CanWork);
        Assert.Equal(2, (await f.Admin.Get<LessonDashboard>("teacher/lessons")).History.Length);
    }

    [Fact]
    public async Task RestartRequiresExplicitRestoreAndKeepsPasswordsMembershipAndHistory()
    {
        await using var f = await LiveClassroom.Start();
        var before = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Student.Put<JsonElement>("tasks/database/draft", new DraftRequest("SELECT 71;"));
        await f.Restart();
        using var anonymous = new ClassroomApi(f.Admin.Connection);
        var closed = await anonymous.Get<AdmissionDto>("admission");
        Assert.False(closed.AdmissionOpen); Assert.Empty(closed.Groups);
        Assert.Equal(423, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Login(new("student", "ui-test", "synthetic-student-password")))).Status);
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        var current = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        Assert.Equal(before.Id, current.Id); Assert.False(current.Activated); Assert.Equal(1, current.Participants);
        await f.Admin.Post<LessonView>($"teacher/lessons/{current.Id}/restore");
        await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        Assert.Equal("SELECT 71;", (await f.Student.Get<StudentTask>("tasks/database")).Draft);
        Assert.Equal(1, (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!.Participants);
    }
}
