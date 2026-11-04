using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsReviewUiTests
{
    static async Task WaitFor(Func<Task<bool>> ready)
    {
        for (var i = 0; i < 100; i++) { if (await ready()) return; await Task.Delay(75); }
        Assert.True(await ready());
    }
    static void Capture(MainWindow window, string name)
    {
        window.UpdateLayout(); var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, name));
    }
    [AvaloniaFact]
    public async Task TeacherGradesSnapshotAndGrantsResubmissionThroughNativeControls()
    {
        await using var f = await LiveClassroom.Start(); var task = Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + task, new ElectronicsTaskRequest("Светодиод и резистор", "Соберите цепь и напишите программу мигания.", CircuitProject.Blink().Serialize()));
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks", new ElectronicsSelectionRequest([task]));
        var work = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + task);
        var report = Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"), 1, work.ProjectJson, [new(report, "submitted", work.ProjectJson, "", 1)], work.Id));
        var window = new MainWindow(true, false) { Width = 1320, Height = 900 };
        using var queue = new ElectronicsReviewQueueView(window, f.Admin); window.Preview(queue); window.Show();
        try
        {
            await queue.Load(); window.UpdateLayout();
            var list = queue.GetVisualDescendants().OfType<ListBox>().Single(); list.SelectedIndex = 0;
            await WaitFor(() => Task.FromResult(queue.GetVisualDescendants().OfType<ElectronicsSnapshotView>().Any())); window.UpdateLayout();
            queue.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.Count == 6).SelectedIndex = 4;
            queue.GetVisualDescendants().OfType<TextBox>().Single(t => t.MaxLength == 8000).Text = "Схема верная; добавьте пояснение к коду.";
            Capture(window, "electronics-review-1320.png");
            queue.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Сохранить результат").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(async () => (await f.Student.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + work.Id + "/status")).LatestReview?.Grade == 4);
            await WaitFor(() => Task.FromResult(queue.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Разрешить пересдачу" && b.IsVisible)));
            window.Width = 900; await Task.Delay(150); window.UpdateLayout();
            Assert.Equal(900, window.Bounds.Width);
            Capture(window, "electronics-review-900.png");
            queue.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Разрешить пересдачу").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(async () => (await f.Student.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + work.Id + "/status")).CanSubmit);
            var detail = await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");
            Assert.Equal(work.ProjectJson, detail.ProjectJson); Assert.Equal(2, detail.History.Length);
            Assert.Equal("allow-resubmit", detail.History[1].Action);
            Assert.Empty((await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics")).Items);
        }
        finally { window.Close(); }
    }
}
