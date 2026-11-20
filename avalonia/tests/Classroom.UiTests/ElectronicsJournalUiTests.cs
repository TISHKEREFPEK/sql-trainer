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

public sealed class ElectronicsJournalUiTests
{
    static async Task Until(Func<bool> ready) { for (var i = 0; i < 150 && !ready(); i++) await Task.Delay(25); Assert.True(ready()); }
    static void Click(Control root, string text) => root.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task TeacherSelectsExactPagedSchemeAndCodeAndReturnsFromReferencedReport(int width)
    {
        await using var f = await LiveClassroom.Start(); var project = CircuitProject.Blink(); var initial = project.Serialize(); var client = Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 1, initial, []));
        var directory = Path.Combine(Path.GetTempPath(), "classroom-electronics-journal-view-" + Guid.NewGuid().ToString("N"));
        using var history = new ElectronicsHistoryStore(directory, new(f.StudentId, f.Student.Connection.Fingerprint, f.Student.LessonId!, null), initial);
        for (var i = 1; i <= 110; i++) { project.Code = "// шаг " + i + " 😀"; project.Parts[1] = project.Parts[1] with { X = 350 + i }; history.Record(project.Serialize(), "move"); }
        var report = Guid.NewGuid().ToString("N"); history.Record(project.Serialize(), "report-submitted", true, reportId: report, reportHash: SqlSyncHash.Text(project.Serialize()));
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(client, 1, initial, [new(report, "submitted", project.Serialize(), "Отправленный снимок")]));
        for (var i = 0; i < 10 && history.Pending; i++) await history.Deliver(f.Student, client); Assert.False(history.Pending);
        var window = new MainWindow(true, false) { Width = width, Height = 900 }; using var view = new ElectronicsJournalView(window, f.Admin, f.StudentId, "Тестовый ученик");
        try
        {
            window.Preview(view); window.Show(); await view.Load(); window.UpdateLayout(); var list = view.GetVisualDescendants().OfType<ListBox>().Single(); Assert.Equal(101, list.ItemCount);
            list.SelectedIndex = 64; await Until(() => view.GetVisualDescendants().OfType<TextBlock>().Any(b => b.Text?.StartsWith("Шаг 64 ·", StringComparison.Ordinal) == true)); window.UpdateLayout();
            var snapshot = view.GetVisualDescendants().OfType<ElectronicsSnapshotView>().Single();
            var captured = (CircuitProject)typeof(ElectronicsSnapshotView).GetField("project", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(snapshot)!;
            Assert.Equal(414, captured.Parts[1].X); snapshot.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; window.UpdateLayout();
            var code = snapshot.GetVisualDescendants().OfType<TextEditor>().Single(); Assert.True(code.IsReadOnly); Assert.Equal("// шаг 64 😀", code.Text);
            Click(view, "Ещё шаги"); await Until(() => list.ItemCount == 112); list.SelectedIndex = 111;
            await Until(() => view.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Открыть связанный снимок" && b.IsVisible));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), b => b.Text?.Contains("занятие не сохранялось") == true);
            Click(view, "Открыть связанный снимок"); await Until(() => view.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "К истории электроники")); window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), b => b.Text?.Contains("Работа отправлена") == true);
            Click(view, "К истории электроники"); window.UpdateLayout(); Assert.Equal(111, list.SelectedIndex); await Task.Delay(100); window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame(); var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory; Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"electronics-journal-{width}.png"));
            Assert.Equal(initial, (await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/work/{client}")).ProjectJson);
            Assert.Equal(1, (await f.Admin.Get<ElectronicsOverview>("teacher/electronics")).Students.Single().Submitted);
        }
        finally { window.Close(); }
    }
}
