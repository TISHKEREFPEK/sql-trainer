using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlExecutionEvidenceTests
{
    internal static string Solution(StudentTask task)
    {
        var definition = Catalog.Data.Tasks.Single(t => t.Id == task.Id);
        return (task.VariantIndex == 1 ? definition : Variants.Create(definition)[task.VariantIndex - 2]).Solution;
    }
    internal static string LastAttempt(LiveClassroom f, string taskId)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT Id FROM Attempts WHERE StudentId=$student AND TaskId=$task ORDER BY At DESC,Id DESC LIMIT 1";
        command.Parameters.AddWithValue("$student", f.StudentId); command.Parameters.AddWithValue("$task", taskId); return (string)command.ExecuteScalar()!;
    }
    static long Count(LiveClassroom f, string table)
    {
        Assert.Contains(table, new[] { "Attempts", "SqlExecutionStates", "SqlExecutionEvidence" });
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table; return (long)command.ExecuteScalar()!;
    }
    [Fact]
    public async Task ExactAttemptIsIdempotentFrozenAndReplaysWithoutChangingStudentResults()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database");
        var request = new ExecuteRequest(Guid.NewGuid().ToString(), Solution(task), true, f.Student.LessonId, task.WorkScope);
        var result = await f.Student.Post<ExecutionDto>("tasks/database/execute", request); Assert.True(result.Correct);
        Assert.Equal(Wire.Write(result), Wire.Write(await f.Student.Post<ExecutionDto>("tasks/database/execute", request)));
        Assert.Equal(1, Count(f, "Attempts")); Assert.Equal(1, Count(f, "SqlExecutionEvidence")); Assert.Equal(1, Count(f, "SqlExecutionStates"));
        var id = LastAttempt(f, task.Id); var route = "teacher/sql-review/attempts/" + id;
        var original = await f.Admin.Get<SqlReviewAttempt>(route);
        Assert.Equal("exact", original.StateAvailability); Assert.Equal(request.Code, original.Code); Assert.Equal(task.Prompt, original.Prompt);
        Assert.Equal(Wire.Write(result), Wire.Write(original.OriginalResult));
        var catalogue = await f.Admin.Get<TeacherTaskDto>("teacher/tasks/database");
        var updated = Catalog.Read(catalogue.TaskJson) with { Title = "Изменённое название каталога" };
        await f.Admin.Put<TeacherTaskDto>("teacher/tasks/database", new SaveTaskRequest(Catalog.Write(updated), catalogue.Restricted));
        Assert.Equal(Wire.Write(original), Wire.Write(await f.Admin.Get<SqlReviewAttempt>(route)));
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause");
        var replay = await f.Admin.Post<ExecutionDto>(route + "/execute", new SqlReviewRun(request.Code));
        Assert.Null(replay.Error); Assert.Equal(Wire.Write(result.Output), Wire.Write(replay.Output));
        Assert.False(replay.Correct); // sandbox execution never grants a pupil result
        var attack = await f.Admin.Post<ExecutionDto>(route + "/execute", new SqlReviewRun("SELECT load_extension('forbidden');")); Assert.NotNull(attack.Error);
        var schema = await f.Admin.Post<ExecutionDto>(route + "/execute", new SqlReviewRun("SELECT name FROM sqlite_master WHERE type='table';"));
        Assert.DoesNotContain(schema.Output.Values, row => row[0].GetString() == "Profiles");
        Assert.Equal(1, Count(f, "Attempts")); Assert.Equal(1, Count(f, "SqlExecutionEvidence"));
        var stats = await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database"); Assert.Equal(1, stats.Record.Checks); Assert.Equal(0, stats.Record.Errors);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<SqlReviewAttempt>(route))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<ExecutionDto>(route + "/execute", new SqlReviewRun(request.Code)))).Status);
    }
    [Fact]
    public async Task ProjectAttemptKeepsItsOwnBeforeStateAcrossLaterStepsBackupAndProfileDeletion()
    {
        await using var f = await LiveClassroom.Start(); var chain = Catalog.Data.Tasks.Where(t => t.Project == "library").ToArray();
        string? savedId = null, code = null; ExecutionDto? expected = null;
        for (var i = 0; i < chain.Length; i++)
        {
            var task = await f.Student.Get<StudentTask>("tasks/" + chain[i].Id);
            var answer = Solution(task);
            var result = await f.Student.Post<ExecutionDto>($"tasks/{task.Id}/execute", new ExecuteRequest(Guid.NewGuid().ToString(), answer, true, f.Student.LessonId, task.WorkScope));
            Assert.True(result.Correct, result.Error);
            if (i == 1) { savedId = LastAttempt(f, task.Id); code = answer; expected = result; }
        }
        Assert.NotNull(savedId); var route = "teacher/sql-review/attempts/" + savedId;
        var source = await f.Admin.Get<SqlReviewAttempt>(route); Assert.Equal("exact", source.StateAvailability);
        var beforeChecks = (await f.Admin.Get<TeacherOverview>("teacher/overview")).Attempts.Length;
        var replay = await f.Admin.Post<ExecutionDto>(route + "/execute", new SqlReviewRun(code!)); Assert.Null(replay.Error);
        Assert.Equal(Wire.Write(expected!.Tables), Wire.Write(replay.Tables));
        var denied = await f.Admin.Post<ExecutionDto>(route + "/execute", new SqlReviewRun("ATTACH DATABASE 'arbitrary-file.sqlite' AS outside;")); Assert.Contains("authorized", denied.Error!);
        Assert.Equal(beforeChecks, (await f.Admin.Get<TeacherOverview>("teacher/overview")).Attempts.Length);
        var backup = await f.Admin.Post<JsonElement>("teacher/backups");
        await f.Admin.Post<JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        Assert.Equal(Wire.Write(source), Wire.Write(await f.Admin.Get<SqlReviewAttempt>(route)));
        Assert.Null((await f.Admin.Post<ExecutionDto>(route + "/execute", new SqlReviewRun(code!))).Error);
        await f.Admin.Delete<JsonElement>("teacher/students/" + f.StudentId);
        Assert.Equal(0, Count(f, "Attempts")); Assert.Equal(0, Count(f, "SqlExecutionEvidence")); Assert.Equal(0, Count(f, "SqlExecutionStates"));
    }
    [Fact]
    public async Task FailedSqlHasBeforeStateButLegacyAttemptNeverInventsOne()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database");
        var result = await f.Student.Post<ExecutionDto>("tasks/database/execute", new ExecuteRequest(Guid.NewGuid().ToString(), "SELECT FROM", true, f.Student.LessonId, task.WorkScope)); Assert.NotNull(result.Error);
        var id = LastAttempt(f, task.Id); var route = "teacher/sql-review/attempts/" + id;
        var source = await f.Admin.Get<SqlReviewAttempt>(route); Assert.Equal("exact", source.StateAvailability); Assert.Equal(result.Error, source.OriginalResult!.Error);
        await f.Student.Post<ExecutionDto>("tasks/database/execute", new ExecuteRequest(Guid.NewGuid().ToString(), "SELECT FROM", true, f.Student.LessonId, task.WorkScope));
        Assert.Equal(2, Count(f, "SqlExecutionEvidence")); Assert.Equal(1, Count(f, "SqlExecutionStates"));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DatabasePath, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM SqlExecutionEvidence WHERE AttemptId=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        }
        var legacy = await f.Admin.Get<SqlReviewAttempt>(route); Assert.Equal("legacy", legacy.StateAvailability); Assert.Null(legacy.Prompt); Assert.Null(legacy.OriginalResult);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Post<ExecutionDto>(route + "/execute", new SqlReviewRun("SELECT 1")))).Status);
    }
}
