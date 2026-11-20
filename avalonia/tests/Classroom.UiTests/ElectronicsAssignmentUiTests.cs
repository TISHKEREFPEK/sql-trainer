using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsAssignmentUiTests
{
    static async Task WaitFor(Func<bool> ready)
    {
        for (var i = 0; i < 160 && !ready(); i++) await Task.Delay(50);
        Assert.True(ready());
    }
    static void Capture(MainWindow window, string name)
    {
        window.UpdateLayout(); var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, name));
    }
    [AvaloniaFact]
    public async Task TeacherWritesConditionAndIssuesItThroughNativeControls()
    {
        await using var f = await LiveClassroom.Start();
        var window = new MainWindow(false, false) { Width = 1320, Height = 900 };
        var form = new ElectronicsTaskAuthoring(window, f.Admin); window.Preview(form); window.Show();
        try
        {
            form.GetVisualDescendants().OfType<TextBox>().Single(t => t.MaxLength == 120).Text = "Работа со светодиодом";
            form.GetVisualDescendants().OfType<TextBox>().Single(t => t.MaxLength == 32000).Text = "Соберите цепь и напишите программу мигания.";
            form.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Сохранить задание").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => form.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("Сохранено ·", StringComparison.Ordinal) == true));
            var task = Assert.Single(await f.Admin.Get<ElectronicsTaskSummary[]>("teacher/electronics/tasks"));
            Assert.Null((await f.Admin.Get<ElectronicsTaskDto>("teacher/electronics/tasks/" + task.Id)).StarterJson);
            Capture(window, "electronics-authoring-1320.png");
            var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
            var picker = new LessonElectronicsPicker(window, f.Admin); window.Preview(picker); await picker.Refresh(lesson.Id); window.UpdateLayout();
            picker.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            picker.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Сохранить выдачу схем").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => !picker.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("не сохранено") == true));
            Assert.Equal(task.Id, (await f.Admin.Get<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks")).Tasks.Single().Id);
            Assert.Equal(task.Id, (await f.Student.Get<StudentHomeDto>("student/home")).Electronics!.Single().TaskId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TwoAssignedEditorsKeepOwnCodeAndSubmitAnImmutableVersion()
    {
        await using var f = await LiveClassroom.Start();
        async Task<string> Create(string title, string code)
        {
            var id = Guid.NewGuid().ToString("N"); var project = CircuitProject.Blink(); project.Code = code;
            await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + id, new ElectronicsTaskRequest(title, "Условие: " + title, project.Serialize())); return id;
        }
        var firstId = await Create("Первая схема", "// первый шаблон"); var secondId = await Create("Вторая схема", "// второй шаблон");
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks", new ElectronicsSelectionRequest([firstId, secondId]));
        var login = await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        var directory = Path.Combine(Path.GetTempPath(), "classroom-assigned-electronics-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(false, false, directory) { Width = 1320, Height = 900 };
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(MainWindow).GetField("api", flags)!.SetValue(window, f.Student); typeof(MainWindow).GetField("login", flags)!.SetValue(window, login);
        window.Show();
        void Open(string title)
        {
            var home = window.GetVisualDescendants().OfType<StudentHomeControl>().Single();
            ((Avalonia.Visual)home.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == title).Parent!).GetVisualDescendants().OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        static TextEditor Code(ElectronicsScreen view)
        {
            view.UpdateLayout();
            view.UpdateLayout();
            return view.GetVisualDescendants().OfType<TextEditor>().Single();
        }
        try
        {
            await window.ShowStudentHome(); Open("Первая схема"); await WaitFor(() => window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            var first = window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            Assert.Equal("// первый шаблон", Code(first).Text); Code(first).Text = "// собственная первая программа";
            await first.Synchronize(); await window.ShowStudentHome(); Open("Вторая схема");
            await WaitFor(() => window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            var second = window.GetVisualDescendants().OfType<ElectronicsScreen>().Single(); Assert.NotSame(first, second);
            Assert.Equal("// второй шаблон", Code(second).Text); Code(second).Text = "// собственная вторая программа";
            await second.Synchronize(); await window.ShowStudentHome(); Open("Первая схема");
            await WaitFor(() => window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            Assert.Same(first, window.GetVisualDescendants().OfType<ElectronicsScreen>().Single()); Assert.Equal("// собственная первая программа", Code(first).Text);
            Capture(window, "assigned-electronics-code-1320.png");
            first.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Отправить на проверку").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => first.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Ожидает проверки"));
            var status = await f.Student.Get<ElectronicsAssignmentStatus>($"electronics/assignments/{first.AssignmentId}/status");
            Assert.False(status.CanSubmit); Assert.Equal("awaiting-review", status.State);
            Assert.Equal("// собственная вторая программа", CircuitProject.Parse((await f.Student.Get<ElectronicsAssignmentDto>("electronics/assignments/" + second.AssignmentId)).ProjectJson).Code);
        }
        finally { window.Close(); }
    }
}
