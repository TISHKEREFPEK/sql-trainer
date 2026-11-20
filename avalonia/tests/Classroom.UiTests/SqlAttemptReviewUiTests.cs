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

public sealed class SqlAttemptReviewUiTests
{
    static async Task WaitFor(Func<bool> ready) { for (var i = 0; i < 150 && !ready(); i++) await Task.Delay(50); Assert.True(ready()); }
    static void Click(Control view, string label) => view.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [AvaloniaFact]
    public async Task TeacherRunsEditsAndRestoresExactAnswerWithoutAddingAttempts()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var code = SqlExecutionEvidenceTests.Solution(task);
        await f.Student.Post<ExecutionDto>("tasks/database/execute", new ExecuteRequest(Guid.NewGuid().ToString(), code, true, f.Student.LessonId, task.WorkScope));
        var id = SqlExecutionEvidenceTests.LastAttempt(f, task.Id);
        var source = await f.Admin.Get<SqlReviewAttempt>("teacher/sql-review/attempts/" + id);
        var window = new MainWindow(true, false) { Width = 1320, Height = 900 }; using var view = new SqlAttemptReviewView(window, f.Admin, source); window.Preview(view); window.Show(); window.UpdateLayout();
        try
        {
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single(); Assert.Equal(code, editor.Text);
            Click(view, "Выполнить SQL"); await WaitFor(() => view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Выполнено"));
            editor.Text = "SELECT FROM"; Click(view, "Выполнить SQL"); await WaitFor(() => view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Ошибка SQL"));
            Click(view, "Вернуть ответ ученика"); Assert.Equal(code, editor.Text);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Ошибка SQL");
            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory; Directory.CreateDirectory(output);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "sql-attempt-review-1320.png"));
            window.Width = 900; await Task.Delay(150); window.UpdateLayout(); Assert.Equal(900, window.Bounds.Width);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "sql-attempt-review-900.png"));
            Assert.Single((await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Events, e => e.Action == "check");
        }
        finally { window.Close(); }
    }
}
