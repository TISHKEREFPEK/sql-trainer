using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Domain;
using Xunit;

namespace Classroom.UiTests;

public sealed class StudentHomeTests
{
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task HomeShowsAssignedWorkPreviousCompletionAndPauseWithoutTimer(int width)
    {
        await using var f = await LiveClassroom.Start();
        var task = await f.Student.Get<StudentTask>("tasks/database");
        var definition = Catalog.Data.Tasks.Single(t => t.Id == "database");
        var solution = task.VariantIndex == 1 ? definition : Variants.Create(definition)[task.VariantIndex - 2];
        Assert.True((await f.Student.Post<ExecutionDto>("tasks/database/execute", new ExecuteRequest(Guid.NewGuid().ToString(), solution.Solution, true))).Correct);
        var previous = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Post<LessonView>($"teacher/lessons/{previous.Id}/finish");
        var next = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, OpenToAllGroups: true)));
        var other = Catalog.Data.Tasks.First(t => t.Id != "database" && t.Project is null).Id;
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{next.Id}/sql-tasks", new LessonSqlSelectionRequest(["database", other]));
        await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start");
        await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        var dto = await f.Student.Get<StudentHomeDto>("student/home");
        Assert.Equal("completed-before", dto.Required.Single(w => w.Id == "database").State);
        Assert.Equal("not-started", dto.Required.Single(w => w.Id == other).State);
        string? opened = null;
        var window = new MainWindow(false, false) { Width = width, Height = 900 };
        var home = new StudentHomeControl(window, f.Student, (_, id) => { opened = id; return Task.CompletedTask; });
        window.Preview(home); window.Show();
        try
        {
            await home.Refresh(); window.UpdateLayout();
            Assert.Contains(home.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Решено раньше") == true);
            Assert.Contains(home.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Не начато") == true);
            Assert.DoesNotContain(home.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("секунд") == true);
            home.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == "Открыть").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(opened);
            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(output);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"student-home-work-{width}.png"));
            await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/pause");
            await home.Refresh(); window.UpdateLayout();
            Assert.All(home.GetVisualDescendants().OfType<Button>(), b => Assert.False(b.IsEnabled));
            Assert.Contains(home.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Занятие на паузе") == true);
        }
        finally { window.Close(); }
    }
}
