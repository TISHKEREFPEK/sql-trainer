using Avalonia;
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
    [InlineData(704,396)]
    [InlineData(1008,567)]
    public async Task LongWorkAndPauseDuringOpeningKeepCommandsSafe(int width,int height)
    {
        await using var f=await LiveClassroom.Start();
        var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var id=Guid.NewGuid().ToString("N");
        const string title="Схема управления освещением лаборатории: проверка длинного названия задания и сохранения работы ученика";
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+id,new ElectronicsTaskRequest(title,"Условие синтетического задания"));
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([id]));
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        var window=new MainWindow(false,false){MinWidth=600,MinHeight=300,Width=width,Height=height};
        var home=new StudentHomeControl(window,f.Student,async(_,_)=>{calls++;await release.Task;});
        window.Preview(home);window.Show();
        try
        {
            await home.Refresh();window.UpdateLayout();
            var name=home.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Text==title);
            var row=(Grid)name.Parent!;var action=row.GetVisualDescendants().OfType<Button>().Single();
            Assert.True(name.Bounds.Width>120);Assert.True(name.Bounds.Right<=row.Bounds.Width);
            Assert.True(action.Bounds.Right<=row.Bounds.Width);Assert.True(action.Bounds.Width>0);
            var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(output);
            using(var frame=window.CaptureRenderedFrame())frame!.Save(Path.Combine(output,$"graphite-home-long-{width}x{height}.png"));
            action.BringIntoView();window.UpdateLayout();
            var viewport=((ScrollViewer)home.Content!).GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().First();
            var visible=action.TranslatePoint(new Point(0,0),viewport)!.Value;
            Assert.InRange(visible.Y,0,viewport.Bounds.Height-action.Bounds.Height+1);
            using(var frame=window.CaptureRenderedFrame())frame!.Save(Path.Combine(output,$"graphite-home-long-{width}x{height}-work.png"));
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1,calls);
            foreach(var button in home.GetVisualDescendants().OfType<Button>())button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1,calls);
            await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause");
            await home.Refresh();window.UpdateLayout();
            release.SetResult();
            for(var i=0;i<100&&action.Classes.Contains("busy");i++)await Task.Delay(10);
            Assert.DoesNotContain("busy",action.Classes);
            Assert.All(home.GetVisualDescendants().OfType<Button>().Where(b=>b.Classes.Contains("graphite-action")),b=>Assert.False(b.IsEnabled));
            foreach(var button in home.GetVisualDescendants().OfType<Button>())button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1,calls);
        }
        finally{release.TrySetResult();window.Close();}
    }

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
            home.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Посмотреть").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("database",opened);
            home.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == "Открыть").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(other,opened);
            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(output);
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, $"student-home-work-{width}.png"));
            await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/pause");
            await home.Refresh(); window.UpdateLayout();
            Assert.All(home.GetVisualDescendants().OfType<Button>().Where(b=>b.Classes.Contains("graphite-action")), b => Assert.False(b.IsEnabled));
            Assert.Contains(home.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Занятие на паузе") == true);
            opened=null;
            foreach(var button in home.GetVisualDescendants().OfType<Button>())button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(opened);
        }
        finally { window.Close(); }
    }
}
