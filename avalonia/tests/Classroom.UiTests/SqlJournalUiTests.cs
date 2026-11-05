using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlJournalUiTests
{
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task TeacherBrowsesPagedStepsAndExactReadOnlyCodeWithoutChangingStudent(int width)
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database");
        var root = Path.Combine(Path.GetTempPath(), "classroom-journal-ui-" + Guid.NewGuid().ToString("N"));
        using var local = new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId);
        local.Observe(f.Student.LessonId!, task);
        for (var i = 0; i < 130; i++) local.Edit(f.Student.LessonId!, task.WorkScope, task.Id, $"SELECT {i}; -- текст 😀", 0);
        await local.Drain(f.Student);
        var saved = await f.Student.Get<StudentTask>("tasks/database");
        var window = new MainWindow(false, false, root) { Width = width, Height = 850 };
        using var view = new SqlJournalView(window, f.Admin, f.StudentId, task.Id, task.Title);
        try
        {
            window.Preview(view); window.Show(); await view.Load(); window.UpdateLayout();
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single(); Assert.True(editor.IsReadOnly); Assert.Equal(task.Starter, editor.Text);
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(); Assert.Equal(101, list.ItemCount);
            list.SelectedIndex = 64;
            for (var i = 0; i < 100 && editor.Text != "SELECT 63; -- текст 😀"; i++) await Task.Delay(25);
            Assert.Equal("SELECT 63; -- текст 😀", editor.Text);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), b => b.Text?.Contains("получено:") == true);
            var difference = view.GetVisualDescendants().OfType<Expander>().Single(); difference.IsExpanded = true; window.UpdateLayout();
            Assert.Equal("2", difference.GetVisualDescendants().OfType<TextBox>().First().Text);
            view.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Ещё шаги").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && list.ItemCount != 131; i++) await Task.Delay(25);
            Assert.Equal(131, list.ItemCount);
            list.SelectedIndex = 130;
            for (var i = 0; i < 100 && editor.Text != saved.Draft; i++) await Task.Delay(25);
            Assert.Equal(saved.Draft, editor.Text); window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout();
            Assert.True(editor.Bounds.Width > 200); Assert.True(editor.Bounds.Height > 100);
            using var frame = window.CaptureRenderedFrame();
            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory; Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"sql-journal-{width}.png"));
            Assert.Equal(saved.Draft, (await f.Student.Get<StudentTask>("tasks/database")).Draft);
            Assert.Equal(0, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/{task.Id}")).Record.Checks);
        }
        finally { window.Close(); }
    }
}
