using System.Reflection;
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

public sealed class ReviewAccessUiTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Click(Control view, string label) => view.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task WaitFor(Func<Task<bool>> ready)
    { for (var i = 0; i < 240; i++) { if (await ready()) return; await Task.Delay(75); } Assert.True(await ready()); }
    static void Capture(MainWindow window, string name)
    {
        window.UpdateLayout(); var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, name));
    }
    [AvaloniaFact]
    public async Task SubmittedCopyRunsAndResetsWithoutChangingReportsOrTeacherFiles()
    {
        await using var f = await LiveClassroom.Start();
        var project = CircuitProject.Blink(); project.Code = "void setup(){Serial.begin(9600);Serial.println(\"REVIEW_ONLY\");} void loop(){}";
        var report = Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, project.Serialize(), [new(report, "submitted", project.Serialize(), "")]));
        var data = await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");
        var ownFile = Path.Combine(Path.GetTempPath(), "teacher-sentinel-" + Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(ownFile, CircuitExamples.Servo().Serialize()); var before = File.ReadAllText(ownFile);
        var window = new MainWindow(true, false) { Width = 1320, Height = 900 };
        using var ownEditor = new ElectronicsScreen(window, ownFile, teacher: true);
        window.Preview(ownEditor); window.Show();
        using var view = new ElectronicsReviewWorkspace(window, data); window.ShowPage("Проверочная копия", view, view.Dispose); window.UpdateLayout();
        try
        {
            Click(view, "Запустить");
            var editor = view.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            editor.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; window.UpdateLayout();
            await WaitFor(() => Task.FromResult(editor.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text?.Contains("REVIEW_ONLY", StringComparison.Ordinal) == true)));
            editor.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 0; window.UpdateLayout();
            Assert.Equal(project.Code, editor.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            editor.GetVisualDescendants().OfType<TextEditor>().Single().Text = "void setup(){broken_review_symbol;}void loop(){}";
            Click(view, "Восстановить сданную версию"); window.UpdateLayout();
            var reset = view.GetVisualDescendants().OfType<ElectronicsScreen>().Single(); window.UpdateLayout();
            Assert.Equal(project.Code, reset.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            Capture(window, "review-sandbox-code-1320.png");
            Assert.Single((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Reports);
            Assert.Equal(project.Serialize(), (await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}")).ProjectJson);
            Assert.Equal(before, File.ReadAllText(ownFile));
            window.ClosePage(); window.UpdateLayout();
            Assert.Same(ownEditor, window.GetVisualDescendants().OfType<ElectronicsScreen>().Single());
            Assert.Equal(before, File.ReadAllText(ownFile));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task TeacherReviewsDuringPauseAndReturnsToSameStudentDraft()
    {
        await using var f = await LiveClassroom.Start();
        var directory = Path.Combine(Path.GetTempPath(), "classroom-review-student-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(false, false, directory) { Width = 1320, Height = 900 };
        typeof(MainWindow).GetField("api", Hidden)!.SetValue(window, f.Student);
        await (Task)typeof(MainWindow).GetMethod("ShowLogin", Hidden)!.Invoke(window, null)!;
        window.Show(); window.UpdateLayout();
        try
        {
            window.GetVisualDescendants().OfType<TextBox>().Single(t => t.PasswordChar == '●' && t.IsEffectivelyVisible).Text = "synthetic-student-password";
            window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.OfType<StudentChoice>().Any()).SelectedIndex = 0;
            Click(window, "Войти");
            await WaitFor(() => Task.FromResult(window.GetVisualDescendants().OfType<StudentHomeControl>().Any()));
            await window.EnterLesson(new LessonDestination("electronics")); window.UpdateLayout();
            var original = window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            window.UpdateLayout();
            var draft = "// несданный черновик ученика\nvoid setup(){} void loop(){}";
            original.GetVisualDescendants().OfType<TextEditor>().Single().Text = draft;
            var submitted = CircuitProject.Blink().Serialize(); var report = Guid.NewGuid().ToString("N");
            await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, submitted, [new(report, "submitted", submitted, "")]));
            var code = await f.Admin.Post<ReviewAccessCode>("teacher/review-access", new CreateReviewAccess(f.StudentId));
            Click(window, "Проверка преподавателя");
            await WaitFor(() => Task.FromResult(window.GetVisualDescendants().OfType<StudentReviewPage>().Any())); window.UpdateLayout();
            var page = window.GetVisualDescendants().OfType<StudentReviewPage>().Single();
            page.GetVisualDescendants().OfType<TextBox>().Single(t => t.MaxLength == 32).Text = code.Code;
            Click(page, "Открыть проверку");
            await WaitFor(() => Task.FromResult(page.GetVisualDescendants().OfType<ElectronicsReviewQueueView>().Any())); window.UpdateLayout();
            var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
            await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause"); await Task.Delay(1200);
            Assert.True(((ContentControl)typeof(MainWindow).GetField("body", Hidden)!.GetValue(window)!).IsEnabled);
            page.GetVisualDescendants().OfType<ListBox>().Single().SelectedIndex = 0;
            await WaitFor(() => Task.FromResult(page.GetVisualDescendants().OfType<ElectronicsSnapshotView>().Any())); window.UpdateLayout();
            page.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.Count == 6).SelectedIndex = 3;
            page.GetVisualDescendants().OfType<TextBox>().Single(t => t.MaxLength == 8000).Text = "Проверено на компьютере ученика.";
            Click(page, "Сохранить результат");
            await WaitFor(async () => (await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}")).Item.Grade == 3);
            Click(page, "Завершить проверку");
            await WaitFor(() => Task.FromResult(!window.GetVisualDescendants().OfType<StudentReviewPage>().Any())); window.UpdateLayout();
            Assert.Same(original, window.GetVisualDescendants().OfType<ElectronicsScreen>().Single());
            Assert.Equal(draft, original.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            await WaitFor(() => Task.FromResult(!((ContentControl)typeof(MainWindow).GetField("body", Hidden)!.GetValue(window)!).IsEnabled));
            await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/resume");
            await WaitFor(() => Task.FromResult(((ContentControl)typeof(MainWindow).GetField("body", Hidden)!.GetValue(window)!).IsEnabled));
            Assert.Equal(submitted, (await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}")).ProjectJson);
        }
        finally { await (Task)typeof(MainWindow).GetMethod("Quit", Hidden)!.Invoke(window, null)!; }
    }
}
