using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Domain;
using Xunit;

namespace Classroom.UiTests;

public sealed class LessonWorkspaceUiTests
{
    static async Task WaitFor(Func<bool> predicate)
    {
        for (var i = 0; i < 150 && !predicate(); i++) await Task.Delay(50);
        Assert.True(predicate());
    }
    [AvaloniaFact]
    public async Task TeacherCanSelectNewSolutionAndProjectModeAppliesToTheWholeChain()
    {
        await using var f = await LiveClassroom.Start();
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var window = new MainWindow(false, false) { Width = 900, Height = 900 };
        var picker = new LessonTaskPicker(window, f.Admin);
        window.Preview(picker); window.Show();
        try
        {
            await picker.Refresh(lesson.Id, (await f.Admin.Get<TeacherOverview>("teacher/overview")).Tasks);
            var first = Catalog.Data.Tasks.First(t => t.Project == "library");
            var check = picker.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is TextBlock text && text.Text!.StartsWith(first.Title + " · ", StringComparison.Ordinal));
            check.IsChecked = true;
            check.FindAncestorOfType<Grid>()!.Children.OfType<ComboBox>().Single().SelectedIndex = 1;
            picker.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Сохранить выдачу").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LessonSqlSelection? result = null;
            for (var i = 0; i < 100 && result?.Tasks.Length is not > 0; i++)
            {
                result = await f.Admin.Get<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks");
                if (result.Tasks.Length == 0) await Task.Delay(50);
            }
            Assert.Equal(Catalog.Data.Tasks.Count(t => t.Project == "library"), result!.Tasks.Length);
            Assert.All(result.Tasks, task => Assert.True(task.RequireNewSolution));
            await WaitFor(() => !picker.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("не сохранено") == true));
            picker.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "Поиск SQL-задания").Text = first.Title;
            window.UpdateLayout();
            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(output);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "teacher-sql-modes-900.png"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReissuingOpenPracticeReturnsHomeAndKeepsBothEditorsDraftsSeparate()
    {
        await using var f = await LiveClassroom.Start();
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var login = await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var window = new MainWindow(false, false) { Width = 1320, Height = 900 };
        using var view = new StudentScreen(window, f.Student);
        typeof(MainWindow).GetField("api", flags)!.SetValue(window, f.Student);
        typeof(MainWindow).GetField("login", flags)!.SetValue(window, login);
        typeof(MainWindow).GetField("student", flags)!.SetValue(window, view);
        typeof(MainWindow).GetField("screen", flags)!.SetValue(window, view);
        window.Preview(view); window.Show();
        try
        {
            await view.Load("database");
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            const string oldCode = "SELECT 13 AS prior_practice_draft;";
            editor.Text = oldCode;
            await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest(["database"], ["database"]));
            await WaitFor(() => window.GetVisualDescendants().OfType<StudentHomeControl>().Any());
            Assert.Equal(oldCode, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Draft);
            Assert.Empty((await f.Student.Get<StudentTask>("tasks/database")).Draft);
            var home = window.GetVisualDescendants().OfType<StudentHomeControl>().Single();
            await WaitFor(() => home.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Открыть"));
            home.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Открыть").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.GetVisualDescendants().OfType<StudentScreen>().Any() && view.CurrentWorkScope == "lesson:" + lesson.Id);
            Assert.NotEqual(oldCode, editor.Text);
            const string newCode = "SELECT 27 AS new_lesson_draft;";
            editor.Text = newCode; await view.SaveDraft();
            Assert.Equal(newCode, (await f.Student.Get<StudentTask>("tasks/database")).Draft);
            Assert.Equal(oldCode, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Draft);
        }
        finally { window.Close(); }
    }
}
