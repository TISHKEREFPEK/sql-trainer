using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Domain;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlLocalStoreTests
{
    static string Root() => Path.Combine(Path.GetTempPath(), "classroom-sql-store-" + Guid.NewGuid().ToString("N"));
    static SqlLocalStore Store(string root, LiveClassroom f) => new(root, f.Student.Connection.Fingerprint, f.StudentId);
    [Fact]
    public async Task DeliveryChecksOriginalMembershipAndIdempotenceWithoutExecutingOrLockingNewLesson()
    {
        await using var f = await LiveClassroom.Start();
        var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var execute = new PendingExecution(task.Id, "SELECT 5;", false, lesson, task.WorkScope); await execute.Send(f.Student);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/finish");
        var next = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, OpenToAllGroups: true, FreePractice: true)));
        await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start"); await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        await f.Admin.Post<System.Text.Json.JsonElement>("teacher/students", new CreateStudentRequest("other-device", "synthetic-other-password", null));
        using var other = new ClassroomApi(f.Admin.Connection); await other.Login(new("student", "other-device", "synthetic-other-password"));
        var draft = new SqlDraftSync(Guid.NewGuid().ToString("N"), task.Id, lesson, task.WorkScope, SqlSyncHash.Text(task.Draft), "SELECT 90;", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => other.Put<SqlDraftReceipt>("sql-sync/draft", draft))).Status);
        Assert.False((await other.Post<SqlExecutionReceipt>("sql-sync/receipt", new SqlExecutionReceiptRequest(task.Id, execute.Request))).Found);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<SqlDraftReceipt>("sql-sync/draft", draft with { TaskId = "never-opened" }))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<SqlDraftReceipt>("sql-sync/draft", draft with { WorkScope = "lesson:" + next.Id }))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Admin.Put<SqlDraftReceipt>("sql-sync/draft", draft))).Status);
        var saved = await f.Student.Put<SqlDraftReceipt>("sql-sync/draft", draft);
        Assert.Equal("saved", saved.Status); Assert.Equal(saved, await f.Student.Put<SqlDraftReceipt>("sql-sync/draft", draft));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<SqlDraftReceipt>("sql-sync/draft", draft with { Code = "SELECT 91;" }))).Status);
        var definition = Catalog.Data.Tasks.Single(t => t.Id == task.Id);
        await f.Admin.Put<System.Text.Json.JsonElement>("teacher/tasks/database", new SaveTaskRequest(Catalog.Write(definition), true));
        var action = new SqlActionSync(Guid.NewGuid().ToString("N"), task.Id, lesson, task.WorkScope, "copy", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10000);
        await f.Student.Post<System.Text.Json.JsonElement>("sql-sync/action", action); await f.Student.Post<System.Text.Json.JsonElement>("sql-sync/action", action);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<System.Text.Json.JsonElement>("sql-sync/action", action with { Action = "cut" }))).Status);
        var detail = await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database");
        Assert.Single(detail.Events, e => e.Action == "copy"); Assert.Equal(0, (await f.Student.Get<StudentTask>("tasks/database")).LockedUntil);
    }
    [Fact]
    public async Task FailedAtomicWritePreservesLastFileAndCorruptionIsNotOverwritten()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var root = Root();
        string file;
        using (var local = Store(root, f))
        {
            local.Observe(f.Student.LessonId!, task);
            file = Assert.Single(Directory.GetFiles(root, "*.work.json", SearchOption.AllDirectories));
            var before = File.ReadAllText(file); Directory.CreateDirectory(file + ".backup");
            Assert.ThrowsAny<IOException>(() => local.Edit(f.Student.LessonId!, task.WorkScope, task.Id, "SELECT 555;", 4));
            Assert.Equal(before, File.ReadAllText(file)); Assert.Equal(task.Starter, local.Find(f.Student.LessonId!, task.WorkScope, task.Id)!.Code);
        }
        File.WriteAllText(file, "{}");
        Assert.Throws<InvalidDataException>(() => Store(root, f)); Assert.Equal("{}", File.ReadAllText(file));
    }
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task NativeConflictChoiceShowsBothVersionsAndKeepsTheRejectedOne(int width)
    {
        await using var f = await LiveClassroom.Start();
        var window = new MainWindow(false, false, Root()) { Width = width, Height = 900 };
        using var view = new StudentScreen(window, f.Student);
        try
        {
            window.Preview(view); window.Show(); await view.Load("database"); window.UpdateLayout();
            view.GetVisualDescendants().OfType<TextEditor>().Single().Text = "SELECT 71 AS local;";
            await f.Student.Put<System.Text.Json.JsonElement>("tasks/database/draft", new DraftRequest("SELECT 82 AS other_device;", f.Student.LessonId, "course"));
            await view.SaveDraft(); window.UpdateLayout();
            var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Разрешить конфликт черновика"); Assert.True(button.IsVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), b => b.IsReadOnly && b.Text == "SELECT 71 AS local;");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), b => b.IsReadOnly && b.Text == "SELECT 82 AS other_device;");
            using (var frame = window.CaptureRenderedFrame())
            {
                var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory; Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"sql-draft-conflict-{width}.png"));
            }
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Взять вариант у преподавателя").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && view.GetVisualDescendants().OfType<TextEditor>().Single().Text != "SELECT 82 AS other_device;"; i++) await Task.Delay(25);
            Assert.Equal("SELECT 82 AS other_device;", view.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            var local = window.SqlStore(f.Student).Find(f.Student.LessonId!, "course", "database")!;
            Assert.Equal("SELECT 71 AS local;", Assert.Single(local.Resolutions!).LocalCode);
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task ConflictingDevicesKeepBothDraftsAndPersistTheExplicitChoice()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var rootA = Root(); var rootB = Root();
        using var a = Store(rootA, f); a.Observe(lesson, task); a.Edit(lesson, task.WorkScope, task.Id, "SELECT 71;", 5);
        using (var b = Store(rootB, f))
        {
            b.Observe(lesson, task); b.Edit(lesson, task.WorkScope, task.Id, "SELECT 82;", 7);
            await a.Drain(f.Student); await b.Drain(f.Student);
            var conflict = b.Find(lesson, task.WorkScope, task.Id)!;
            Assert.Equal("SELECT 82;", conflict.Code); Assert.Equal("SELECT 71;", conflict.Conflict!.ServerCode);
            Assert.Equal("SELECT 71;", (await f.Student.Get<StudentTask>("tasks/database")).Draft);
        }
        using var restored = Store(rootB, f); var work = restored.Find(lesson, task.WorkScope, task.Id)!;
        Assert.Equal(7, work.Caret); Assert.NotNull(work.Conflict);
        restored.Resolve(lesson, task.WorkScope, task.Id, true); await restored.Drain(f.Student);
        Assert.Equal("SELECT 82;", (await f.Student.Get<StudentTask>("tasks/database")).Draft);
        var history = Assert.Single(restored.Find(lesson, task.WorkScope, task.Id)!.Resolutions!);
        Assert.Equal("SELECT 71;", history.ServerCode); Assert.Equal("SELECT 82;", history.LocalCode);
        a.Edit(lesson, task.WorkScope, task.Id, "SELECT 93;", 3); await a.Drain(f.Student);
        Assert.Equal("SELECT 82;", a.Find(lesson, task.WorkScope, task.Id)!.Conflict!.ServerCode);
        await f.Student.Put<System.Text.Json.JsonElement>("tasks/database/draft", new DraftRequest("SELECT 94;", lesson, task.WorkScope));
        a.Resolve(lesson, task.WorkScope, task.Id, false); await a.Drain(f.Student);
        Assert.Equal("SELECT 82;", a.Find(lesson, task.WorkScope, task.Id)!.Code);
        Assert.Equal("SELECT 94;", a.Find(lesson, task.WorkScope, task.Id)!.Conflict!.ServerCode);
        using var other = new SqlLocalStore(Root(), new string('A', 64), f.StudentId);
        await Assert.ThrowsAsync<InvalidDataException>(() => other.Drain(f.Student));
    }
    [Fact]
    public async Task UnacknowledgedExecutionKeepsItsPayloadAndReceiptSurvivesNewLesson()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var definition = Catalog.Data.Tasks.Single(t => t.Id == task.Id); var code = (task.VariantIndex == 1 ? definition : Variants.Create(definition)[task.VariantIndex - 2]).Solution;
        var pending = new PendingExecution(task.Id, code, true, lesson, task.WorkScope); var root = Root();
        using (var local = Store(root, f)) { local.Observe(lesson, task); local.Remember(pending); }
        Assert.True((await pending.Send(f.Student)).Correct); // Lose the HTTP acknowledgement on purpose.
        using var restored = Store(root, f); var retry = restored.Pending(lesson, task.WorkScope, task.Id)!;
        Assert.Equal(pending.Request, retry.Request); Assert.True((await retry.Send(f.Student)).Correct);
        var detail = await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database"); Assert.Equal(1, detail.Record.Checks);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/finish");
        var next = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, OpenToAllGroups: true)));
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{next.Id}/sql-tasks", new LessonSqlSelectionRequest(["database"], ["database"]));
        await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start"); await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        var fresh = await f.Student.Get<StudentTask>("tasks/database"); Assert.False(fresh.Completed);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => retry.Send(f.Student))).Status);
        restored.Edit(lesson, task.WorkScope, task.Id, "-- delayed former lesson", 0);
        restored.Action(lesson, task.WorkScope, task.Id, "opened"); await restored.Drain(f.Student);
        Assert.Null(restored.Pending(lesson, task.WorkScope, task.Id)); Assert.NotNull(restored.Find(lesson, task.WorkScope, task.Id)!.LastExecution);
        Assert.Empty((await f.Student.Get<StudentTask>("tasks/database")).Draft);
        var old = await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database"); Assert.Equal("-- delayed former lesson", old.Draft); Assert.Equal(1, old.Record.Checks);
    }
    [AvaloniaFact]
    public async Task NewWindowRestoresUnsentCodeInsteadOfStarter()
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); const string code = "SELECT 404 AS survives_window;";
        var first = new MainWindow(false, false, root) { Width = 1320, Height = 900 };
        using (var view = new StudentScreen(first, f.Student))
        {
            first.Preview(view); first.Show(); await view.Load("database"); first.UpdateLayout();
            view.GetVisualDescendants().OfType<TextEditor>().Single().Text = code;
            first.Close(); // No server save or debounce tick: only the synchronous local write.
        }
        var second = new MainWindow(false, false, root) { Width = 1320, Height = 900 };
        using var restored = new StudentScreen(second, f.Student);
        try
        {
            second.Preview(restored); second.Show(); await restored.Load("database"); second.UpdateLayout();
            Assert.Equal(code, restored.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            await restored.SaveDraft(); Assert.Equal(code, (await f.Student.Get<StudentTask>("tasks/database")).Draft);
        }
        finally { second.Close(); }
    }
}
