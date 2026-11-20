using System.Diagnostics;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Domain;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlCrashRecoveryTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task KilledWriterReleasesLeaseAndRecoversExactPendingRequestWithoutDuplicateChecks(bool serverAlreadyCommitted)
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database");
        var lessonId = f.Student.LessonId!;
        var definition = Catalog.Data.Tasks.Single(t => t.Id == task.Id);
        var code = (task.VariantIndex == 1 ? definition : Variants.Create(definition)[task.VariantIndex - 2]).Solution;
        var request = new ExecuteRequest(Guid.NewGuid().ToString(), code, true, lessonId, task.WorkScope);
        if (serverAlreadyCommitted) Assert.True((await new PendingExecution(task.Id, request).Send(f.Student)).Correct);
        var root = Path.Combine(Path.GetTempPath(), "classroom-sql-crash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var input = Path.Combine(root, "probe-input.json");
        await File.WriteAllTextAsync(input, Wire.Write(new { Root = root, f.Student.Connection.Fingerprint, ProfileId = f.StudentId, LessonId = lessonId, Task = task, Request = request }));
        var probe = Environment.GetEnvironmentVariable("CLASSROOM_TEST_SANDBOX_PROBE") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Classroom.SandboxProbe", "release", "Classroom.SandboxProbe.dll"));
        Assert.True(File.Exists(probe));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CLASSROOM_DOTNET") ?? "dotnet") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(probe); start.ArgumentList.Add("sql-store"); start.ArgumentList.Add(input);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            for (var i = 0; i < 200 && !File.Exists(input + ".ready") && !process.HasExited; i++) await Task.Delay(50);
            Assert.True(File.Exists(input + ".ready"), process.HasExited ? await output + await error : "SQL probe did not finish its durable write.");
            // The writer still holds its lease: concurrent writers cannot take over.
            Assert.Throws<IOException>(() => new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId));
            process.Kill(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await process.WaitForExitAsync(timeout.Token);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        using var restored = new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId);
        var work = restored.Find(lessonId, task.WorkScope, task.Id)!;
        Assert.Equal(code, work.Code); Assert.True(work.Dirty); Assert.Single(work.Actions!);
        Assert.Equal(request, restored.Pending(lessonId, task.WorkScope, task.Id)!.Request);
        await restored.Drain(f.Student);
        var beforeExplicitRetry = await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database");
        Assert.Equal(serverAlreadyCommitted ? 1 : 0, beforeExplicitRetry.Record.Checks);
        if (serverAlreadyCommitted) Assert.Null(restored.Pending(lessonId, task.WorkScope, task.Id));
        else
        {
            var retry = restored.Pending(lessonId, task.WorkScope, task.Id)!; Assert.Equal(request, retry.Request);
            var result = await retry.Send(f.Student); Assert.True(result.Correct); restored.Acknowledge(retry, result);
        }
        await restored.Drain(f.Student);
        var detail = await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database");
        Assert.Equal(1, detail.Record.Checks); Assert.Equal(code, detail.AcceptedCode);
        Assert.Empty(restored.Find(lessonId, task.WorkScope, task.Id)!.Actions!);
    }
}
