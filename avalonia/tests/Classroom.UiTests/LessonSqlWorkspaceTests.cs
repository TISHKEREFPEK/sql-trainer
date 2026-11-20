using System.Security.Cryptography;
using System.Text;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Classroom.UiTests;

public sealed class LessonSqlWorkspaceTests
{
    static string Solution(StudentTask task)
    {
        var definition = Catalog.Data.Tasks.Single(t => t.Id == task.Id);
        return (task.VariantIndex == 1 ? definition : Variants.Create(definition)[task.VariantIndex - 2]).Solution;
    }
    static async Task<LessonView> Start(LiveClassroom f, string[] tasks, bool fresh)
    {
        if ((await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current is { } current)
            await f.Admin.Post<LessonView>($"teacher/lessons/{current.Id}/finish");
        var lesson = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, OpenToAllGroups: true)));
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest(tasks, fresh ? tasks : []));
        lesson = await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/start");
        await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        return lesson;
    }
    static Task<ExecutionDto> Check(LiveClassroom f, StudentTask task, string code) => f.Student.Post<ExecutionDto>($"tasks/{task.Id}/execute",
        new ExecuteRequest(Guid.NewGuid().ToString(), code, true, f.Student.LessonId, task.WorkScope));
    static string? SnapshotHash(LiveClassroom f, string project, string? lessonId = null)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = lessonId is null ? "SELECT Base64 FROM Snapshots WHERE StudentId=$s AND Project=$p"
            : "SELECT Base64 FROM LessonSqlSnapshots WHERE StudentId=$s AND Project=$p AND LessonId=$l";
        command.Parameters.AddWithValue("$s", f.StudentId); command.Parameters.AddWithValue("$p", project);
        if (lessonId is not null) command.Parameters.AddWithValue("$l", lessonId);
        return command.ExecuteScalar() is string value ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))) : null;
    }

    [Fact]
    public async Task FreshSolutionHasItsOwnDraftResultAndOldContextCannotOverwriteIt()
    {
        await using var f = await LiveClassroom.Start();
        var old = await f.Student.Get<StudentTask>("tasks/database");
        var oldLesson = f.Student.LessonId!;
        await f.Student.Put<System.Text.Json.JsonElement>("tasks/database/draft", new DraftRequest("-- прежний черновик", oldLesson, old.WorkScope));
        Assert.True((await Check(f, old, Solution(old))).Correct);
        var courseBefore = await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database");
        var pendingOld = new PendingExecution(old.Id, Solution(old), true, oldLesson, old.WorkScope);
        var lesson = await Start(f, ["database"], fresh: true);
        Assert.True((await f.Admin.Get<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks")).Tasks.Single().RequireNewSolution);
        var task = await f.Student.Get<StudentTask>("tasks/database");
        Assert.Equal("lesson:" + lesson.Id, task.WorkScope); Assert.False(task.Completed); Assert.Empty(task.Draft);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => pendingOld.Send(f.Student))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<System.Text.Json.JsonElement>("tasks/database/draft", new DraftRequest("wrong class", oldLesson, old.WorkScope)))).Status);
        var selection = new LessonSqlSelectionRequest(["database"], []);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", selection))).Status);
        await f.Student.Put<System.Text.Json.JsonElement>("tasks/database/draft", new DraftRequest("-- новый черновик", lesson.Id, task.WorkScope));
        Assert.False((await Check(f, task, "SELECT 1 AS wrong_result;")).Correct);
        var path = $"teacher/lessons/{lesson.Id}/students/{f.StudentId}/sql/database";
        var progress = await f.Admin.Get<LessonSqlWorkDetail>(path);
        Assert.Equal(1, progress.Checks); Assert.Equal(1, progress.Errors); Assert.Null(progress.CompletedAt);
        Assert.Equal(courseBefore.AcceptedCode, progress.PreviousAcceptedCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<LessonSqlWorkDetail>(path))).Status);
        var newCode = Solution(task) + "\n-- решение новой пары";
        Assert.True((await Check(f, task, newCode)).Correct);
        var result = await f.Admin.Get<LessonSqlWorkDetail>(path);
        Assert.Equal(newCode, result.AcceptedCode); Assert.NotNull(result.CompletedAt); Assert.Equal(2, result.Checks);
        var courseAfter = await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database");
        Assert.Equal(courseBefore.AcceptedCode, courseAfter.AcceptedCode); Assert.Equal(courseBefore.Draft, courseAfter.Draft);
        Assert.Equal(courseBefore.Record.CompletedAt, courseAfter.Record.CompletedAt);
        await f.Restart(); await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/restore");
        await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        Assert.Equal("-- новый черновик", (await f.Student.Get<StudentTask>("tasks/database")).Draft);
        Assert.Equal(newCode, (await f.Admin.Get<LessonSqlWorkDetail>(path)).AcceptedCode);
        var practiceLesson = await Start(f, [], fresh: false);
        await f.Admin.Put<LessonView>($"teacher/lessons/{practiceLesson.Id}/policy", new LessonPolicyRequest(null, OpenToAllGroups: true, FreePractice: true));
        var oldPractice = await f.Student.Get<StudentTask>("tasks/database");
        Assert.False((await Check(f, oldPractice, "SELECT 1 AS later_wrong_practice;")).Correct);
        var reuse = await Start(f, ["database"], fresh: false);
        Assert.True((await f.Student.Get<StudentTask>("tasks/database")).Completed);
        Assert.Equal(newCode, (await f.Admin.Get<LessonSqlWorkDetail>($"teacher/lessons/{reuse.Id}/students/{f.StudentId}/sql/database")).AcceptedCode);
        Assert.Equal("completed-before", (await f.Student.Get<StudentHomeDto>("student/home")).Required.Single().State);
    }

    [Fact]
    public async Task FreshProjectUsesSeparateDatabaseAndOrderAndKeepsTheSolvedCourseSnapshot()
    {
        await using var f = await LiveClassroom.Start();
        var chain = Catalog.Data.Tasks.Where(t => t.Project == "library").ToArray();
        foreach (var definition in chain)
        {
            var task = await f.Student.Get<StudentTask>("tasks/" + definition.Id);
            Assert.True((await Check(f, task, Solution(task))).Correct);
        }
        var oldHash = SnapshotHash(f, "library"); Assert.NotNull(oldHash);
        var lesson = await Start(f, [chain[0].Id], fresh: true);
        var issued = await f.Admin.Get<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks");
        Assert.Equal(chain.Length, issued.Tasks.Length); Assert.All(issued.Tasks, t => Assert.True(t.RequireNewSolution));
        var last = await f.Student.Get<StudentTask>("tasks/" + chain[^1].Id);
        Assert.False(last.Completed); Assert.Null(SnapshotHash(f, "library", lesson.Id));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Check(f, last, Solution(last)))).Status);
        foreach (var definition in chain)
        {
            var task = await f.Student.Get<StudentTask>("tasks/" + definition.Id);
            Assert.True((await Check(f, task, Solution(task))).Correct);
            Assert.Equal(oldHash, SnapshotHash(f, "library"));
        }
        Assert.NotNull(SnapshotHash(f, "library", lesson.Id));
        var backup = await f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups");
        await f.Admin.Post<System.Text.Json.JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        Assert.Equal(oldHash, SnapshotHash(f, "library")); Assert.NotNull(SnapshotHash(f, "library", lesson.Id));
        Assert.NotNull((await f.Admin.Get<LessonSqlWorkDetail>($"teacher/lessons/{lesson.Id}/students/{f.StudentId}/sql/{chain[^1].Id}")).CompletedAt);
    }

    [Fact]
    public async Task IssuingDuringPracticeRetainsLateCourseDraftWithoutPrefillingNewWork()
    {
        await using var f = await LiveClassroom.Start();
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var course = await f.Student.Get<StudentTask>("tasks/database");
        Assert.Equal("course", course.WorkScope);
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest(["database"], ["database"]));
        var fresh = await f.Student.Get<StudentTask>("tasks/database");
        Assert.NotEqual(course.WorkScope, fresh.WorkScope);
        await f.Student.Put<System.Text.Json.JsonElement>("tasks/database/draft", new DraftRequest("-- late practice", lesson.Id, course.WorkScope));
        Assert.Empty((await f.Student.Get<StudentTask>("tasks/database")).Draft);
        Assert.Equal("-- late practice", (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Draft);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<ExecutionDto>("tasks/database/execute", new ExecuteRequest(Guid.NewGuid().ToString(), Solution(course), true, lesson.Id, course.WorkScope)))).Status);
        await f.Student.Put<System.Text.Json.JsonElement>("tasks/database/draft", new DraftRequest("-- lesson draft", lesson.Id, fresh.WorkScope));
        Assert.Equal("-- lesson draft", (await f.Student.Get<StudentTask>("tasks/database")).Draft);
    }
}
