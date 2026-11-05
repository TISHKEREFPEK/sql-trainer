using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlActionHistoryUiTests
{
    static void Click(Control root, string title) => root.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task Until(Func<bool> ready) { for (var i = 0; i < 150 && !ready(); i++) await Task.Delay(25); Assert.True(ready()); }
    [AvaloniaFact]
    public async Task NativeWindowNavigationHintsAndUndoAreRecordedInTheirOriginalWork()
    {
        await using var f = await LiveClassroom.Start(); var root = Path.Combine(Path.GetTempPath(), "classroom-action-history-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(false, false, root) { Width = 1100, Height = 850 }; using var view = new StudentScreen(window, f.Student);
        window.Preview(view); window.Show(); await view.Load("database"); window.UpdateLayout();
        SqlTextJournal History() => window.SqlStore(f.Student).Find(f.Student.LessonId!, "course", "database")!.Journal!;
        try
        {
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single(); editor.Document.UndoStack.ClearAll();
            var before = editor.Text; editor.Document.Insert(editor.Document.TextLength, "\n-- проба 😀"); var changed = editor.Text;
            editor.TextArea.Focus(); window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.Control); Assert.Equal(before, editor.Text);
            ApplicationCommands.Redo.Execute(null, editor.TextArea); Assert.Equal(changed, editor.Text);
            Assert.Contains(History().Changes, c => c.Kind == "undo"); Assert.Contains(History().Changes, c => c.Kind == "redo");
            window.WindowState = WindowState.Minimized; window.WindowState = WindowState.Normal;
            Assert.Single(History().Changes, c => c.Kind == "window-minimize"); Assert.Single(History().Changes, c => c.Kind == "window-restore");
            Click(view, "Открыть подсказку"); await Until(() => History().Changes.Any(c => c.Kind == "hint-opened"));
            Assert.Equal(0, History().Changes.First(c => c.Kind == "hint-opened").Reference!.HintIndex);
            Click(view, "Справочник"); await Until(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Назад"));
            Assert.Contains(History().Changes, c => c.Kind == "dictionary-open"); Assert.Contains(History().Changes, c => c.Kind == "work-leave");
            var count = History().Changes.Count(c => c.Kind == "window-minimize"); window.WindowState = WindowState.Minimized; window.WindowState = WindowState.Normal;
            Assert.Equal(count, History().Changes.Count(c => c.Kind == "window-minimize"));
            Click(window, "Назад"); window.UpdateLayout();
            Assert.Contains(History().Changes, c => c.Kind == "dictionary-close"); Assert.True(History().Changes.Count(c => c.Kind == "work-open") >= 2);
            Assert.Equal(changed, editor.Text); Assert.Equal(changed, History().Replay());
            await window.SqlStore(f.Student).Drain(f.Student);
            var events = await f.Admin.Get<SqlJournalEvents>($"teacher/sql-journal/{f.StudentId}/{History().StreamId}/changes");
            Assert.Contains(events.Events, e => e.Change.Kind == "window-minimize");
            Assert.Equal(0, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Record.Violations);
        }
        finally { view.Dispose(); window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task JournalOpensExactAttemptAndReturnsToSelectedStep(int width)
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!;
        var root = Path.Combine(Path.GetTempPath(), "classroom-linked-attempt-" + Guid.NewGuid().ToString("N"));
        using var local = new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId); local.Observe(lesson, task); local.Edit(lesson, task.WorkScope, task.Id, "SELECT FROM", 0);
        var execution = new PendingExecution(task.Id, "SELECT FROM", true, lesson, task.WorkScope); local.Remember(execution); var result = await execution.Send(f.Student); local.Acknowledge(execution, result); await local.Drain(f.Student);
        var window = new MainWindow(true, false) { Width = width, Height = 850 }; using var view = new SqlJournalView(window, f.Admin, f.StudentId, task.Id, task.Title);
        try
        {
            window.Preview(view); window.Show(); await view.Load(); var list = view.GetVisualDescendants().OfType<ListBox>().Single(); list.SelectedIndex = 2;
            await Until(() => view.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Открыть запуск SQL" && b.IsVisible));
            Click(view, "Открыть запуск SQL"); await Until(() => view.GetVisualDescendants().OfType<SqlAttemptReviewView>().Any());
            window.UpdateLayout();
            var review = view.GetVisualDescendants().OfType<SqlAttemptReviewView>().Single(); Assert.Equal("SELECT FROM", review.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            Assert.Equal(1, review.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex);
            Assert.Contains(review.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == result.Error);
            Click(view, "К истории SQL"); window.UpdateLayout(); await Task.Delay(100);
            Assert.Equal(2, list.SelectedIndex); Assert.True(view.GetVisualDescendants().OfType<TextEditor>().Single().IsReadOnly);
            using var frame = window.CaptureRenderedFrame(); var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory; Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"sql-history-linked-{width}.png"));
            Assert.Equal(1, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/{task.Id}")).Record.Checks);
        }
        finally { window.Close(); }
    }
}
