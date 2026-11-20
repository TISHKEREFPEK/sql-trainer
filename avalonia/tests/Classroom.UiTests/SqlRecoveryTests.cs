using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlRecoveryTests
{
    [AvaloniaTheory]
    [InlineData(900)] [InlineData(1320)]
    public async Task ExplicitRecoveryKeepsOriginalAndRechecksRecoveredCodeAgainstServer(int width)
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var root = Path.Combine(Path.GetTempPath(), "classroom-sql-recovery-" + Guid.NewGuid().ToString("N"));
        SqlLocalStore Open() => new(root, f.Student.Connection.Fingerprint, f.StudentId);
        using (var local = Open())
        {
            local.Observe(lesson, task); local.Edit(lesson, task.WorkScope, task.Id, "SELECT 71;", 4);
            local.Edit(lesson, task.WorkScope, task.Id, "SELECT 82;", 5); // The preceding version becomes the backup.
        }
        var file = Assert.Single(Directory.GetFiles(root, "*.work.json", SearchOption.AllDirectories)); File.WriteAllText(file, "broken-original");
        var failure = Assert.Throws<SqlLocalRecoveryException>(() => Open());
        Assert.Equal("SELECT 71;", failure.Candidate.Backup!.Code); Assert.Equal("broken-original", File.ReadAllText(file));
        var completed = false; var window = new MainWindow(false, false) { Width = width, Height = 900 };
        await window.Safe(() => { using var blocked = Open(); return Task.CompletedTask; });
        window.Show(); window.UpdateLayout();
        Assert.Single(window.GetVisualDescendants().OfType<SqlRecoveryView>());
        Assert.Equal("broken-original", File.ReadAllText(file));
        var view = new SqlRecoveryView(window, failure.Candidate, () => { completed = true; return Task.CompletedTask; });
        window.Preview(view); window.Show(); window.UpdateLayout();
        try
        {
            Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(), t => t.IsReadOnly && t.Text == "SELECT 71;");
            using (var frame = window.CaptureRenderedFrame())
            {
                var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory; Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"sql-recovery-{width}.png"));
            }
            view.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Восстановить предыдущую копию").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(completed);
            Assert.Equal("broken-original", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.damaged-*"))));
            using var restored = Open(); var work = restored.Find(lesson, task.WorkScope, task.Id)!;
            Assert.Equal("SELECT 71;", work.Code); Assert.True(work.Dirty);
            await f.Student.Put<System.Text.Json.JsonElement>("tasks/database/draft", new DraftRequest("SELECT 93;", lesson, task.WorkScope));
            await restored.Drain(f.Student); Assert.Equal("SELECT 93;", restored.Find(lesson, task.WorkScope, task.Id)!.Conflict!.ServerCode);
            Assert.Equal("SELECT 71;", restored.Find(lesson, task.WorkScope, task.Id)!.Code);
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task ChangedOrForeignBackupCannotReplaceDamagedWork()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database");
        var root = Path.Combine(Path.GetTempPath(), "classroom-sql-recovery-" + Guid.NewGuid().ToString("N"));
        SqlLocalStore Open() => new(root, f.Student.Connection.Fingerprint, f.StudentId);
        using (var local = Open()) { local.Observe(f.Student.LessonId!, task); local.Edit(f.Student.LessonId!, task.WorkScope, task.Id, "SELECT 1;", 0); }
        var file = Assert.Single(Directory.GetFiles(root, "*.work.json", SearchOption.AllDirectories)); File.WriteAllText(file, "broken");
        var failure = Assert.Throws<SqlLocalRecoveryException>(() => Open());
        var originalBackup = File.ReadAllText(file + ".backup");
        File.AppendAllText(file + ".backup", "changed");
        Assert.Throws<IOException>(() => SqlLocalStore.RestoreBackup(failure.Candidate)); Assert.Equal("broken", File.ReadAllText(file));
        var unavailable = Assert.Throws<SqlLocalRecoveryException>(() => Open()); Assert.Null(unavailable.Candidate.Backup);
        Assert.Throws<IOException>(() => SqlLocalStore.RestoreBackup(unavailable.Candidate)); Assert.Equal("broken", File.ReadAllText(file));
        var foreign = System.Text.Json.Nodes.JsonNode.Parse(originalBackup)!; foreign["profileId"] = "different-student";
        File.WriteAllText(file + ".backup", foreign.ToJsonString());
        var denied = Assert.Throws<SqlLocalRecoveryException>(() => Open()); Assert.Null(denied.Candidate.Backup);
        Assert.Throws<IOException>(() => SqlLocalStore.RestoreBackup(denied.Candidate)); Assert.Equal("broken", File.ReadAllText(file));
    }
}
