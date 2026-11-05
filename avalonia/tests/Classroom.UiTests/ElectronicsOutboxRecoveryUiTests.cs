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

public sealed class ElectronicsOutboxRecoveryUiTests
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Click(MainWindow window, string text) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task Wait(Func<bool> ready) { for (var i = 0; i < 150 && !ready(); i++) await Task.Delay(50); Assert.True(ready()); }
    static async Task<MainWindow> Window(LiveClassroom f, RecoveryWork w, int width)
    {
        var login = await f.Student.Login(new("student", "ui-test", "synthetic-student-password")); var window = new MainWindow(false, false, w.Root) { Width = width, Height = 850 };
        typeof(MainWindow).GetField("api", Flags)!.SetValue(window, f.Student); typeof(MainWindow).GetField("login", Flags)!.SetValue(window, login);
        var sql = new StudentScreen(window, f.Student); typeof(MainWindow).GetField("student", Flags)!.SetValue(window, sql); typeof(MainWindow).GetField("screen", Flags)!.SetValue(window, sql);
        window.Preview(sql); window.Show(); await sql.Load(); return window;
    }
    static Task Quit(MainWindow window) => (Task)typeof(MainWindow).GetMethod("Quit", Flags)!.Invoke(window, null)!;
    static Task Permission(MainWindow window) => (Task)typeof(MainWindow).GetMethod("RefreshLessonPermission", Flags)!.Invoke(window, null)!;
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task RecoveryIsExplicitAndReturnsToLatestCircuitWithTheOriginalSubmission(int width)
    {
        await using var f = await LiveClassroom.Start(); var w = await ElectronicsOutboxRecoveryTests.Create(f); File.WriteAllText(w.Queue, "broken");
        var header = File.ReadAllBytes(w.Header); var backup = File.ReadAllBytes(w.Queue + ".backup"); var window = await Window(f, w, width);
        try
        {
            await window.Safe(() => window.EnterLesson(new("electronics"))); window.UpdateLayout();
            Assert.Single(window.GetVisualDescendants().OfType<ElectronicsOutboxRecoveryView>()); Assert.Equal("broken", File.ReadAllText(w.Queue));
            Assert.Equal(header, File.ReadAllBytes(w.Header)); Assert.Equal(backup, File.ReadAllBytes(w.Queue + ".backup"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(w.Queue)!, "*.damaged-*"));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsReadOnly && t.Text == CircuitProject.Parse(w.Initial).Code);
            await Task.Delay(2200); Assert.Empty((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Reports);
            window.UpdateLayout(); using (var frame = window.CaptureRenderedFrame())
            {
                var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "screenshots"); Directory.CreateDirectory(output);
                frame!.Save(Path.Combine(output, $"electronics-outbox-recovery-{width}.png"));
            }
            Click(window, "Восстановить предыдущую очередь");
            await Wait(() => typeof(MainWindow).GetField("electronics", Flags)!.GetValue(window) is ElectronicsScreen); window.UpdateLayout();
            var circuit = window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            Assert.Equal(CircuitProject.Parse(w.Latest).Code, circuit.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            Assert.Equal("broken", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(w.Queue)!, "*.damaged-*"))));
            Assert.True(await circuit.Synchronize());
            var teacher = await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}"); Assert.Single(teacher.Works); Assert.Equal(w.Report, Assert.Single(teacher.Reports).Id);
            Assert.Equal(w.Client, ElectronicsSync.ReadOutbox(w.Queue, null).ClientId);
        }
        finally { await Quit(window); }
    }
    [AvaloniaFact]
    public async Task ChangedSessionCannotApplyPreviewAndBackPreservesSqlWork()
    {
        await using var f = await LiveClassroom.Start(); var w = await ElectronicsOutboxRecoveryTests.Create(f); File.WriteAllText(w.Queue, "broken"); var window = await Window(f, w, 900);
        try
        {
            var sql = window.GetVisualDescendants().OfType<TextEditor>().Single(); sql.Text = "SELECT 29 AS retained;";
            await window.Safe(() => window.EnterLesson(new("electronics"))); window.UpdateLayout();
            await f.Student.Login(new("student", "ui-test", "synthetic-student-password")); Click(window, "Восстановить предыдущую очередь"); await Task.Delay(100);
            Assert.Equal("broken", File.ReadAllText(w.Queue)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(w.Queue)!, "*.damaged-*"));
            Click(window, "Назад"); window.UpdateLayout();
            await Permission(window);
            Assert.Equal("SELECT 29 AS retained;", window.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            using var lease = new ElectronicsHistoryStore(w.Project + ".history", w.Context, null); Assert.Equal(w.Latest, lease.ProjectJson);
        }
        finally { await Quit(window); }
    }
    [AvaloniaFact]
    public async Task LocalRecoveryRemainsAvailableDuringPauseAndDoesNotUnlockTheCircuit()
    {
        await using var f = await LiveClassroom.Start(); var w = await ElectronicsOutboxRecoveryTests.Create(f); File.WriteAllText(w.Queue,"broken"); var window = await Window(f,w,900);
        try
        {
            await f.Admin.Post<LessonView>($"teacher/lessons/{w.Context.LessonId}/pause");
            await window.Safe(()=>window.EnterLesson(new("electronics"))); await Permission(window); window.UpdateLayout();
            var button=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Восстановить предыдущую очередь"); Assert.True(button.IsEffectivelyEnabled);
            Click(window,"Восстановить предыдущую очередь"); await Wait(()=>typeof(MainWindow).GetField("electronics",Flags)!.GetValue(window) is ElectronicsScreen);
            await Permission(window); window.UpdateLayout(); Assert.False(window.GetVisualDescendants().OfType<ElectronicsScreen>().Single().IsEffectivelyEnabled);
            await f.Admin.Post<LessonView>($"teacher/lessons/{w.Context.LessonId}/resume"); await Permission(window);
            Assert.True(window.GetVisualDescendants().OfType<ElectronicsScreen>().Single().IsEffectivelyEnabled);
        }
        finally { await Quit(window); }
    }
    [AvaloniaFact]
    public async Task MissingLegacyProjectUsesTheExistingQueueInsteadOfTheDefaultSketch()
    {
        await using var f=await LiveClassroom.Start(); var root=Path.Combine(Path.GetTempPath(),"classroom-queue-project-"+Guid.NewGuid().ToString("N"));
        var file=Path.Combine(root,"electronics",SqlSyncHash.Text(f.Student.Connection.Fingerprint+":"+f.StudentId)+".json");
        var report=Assert.Single(ElectronicsLegacyDeliveryTests.SaveLegacy(file,f.Student)); var old=ElectronicsSync.ReadOutbox(file+".sync.json",null); File.Delete(file);
        var w=new RecoveryWork(root,file,new(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null),old.ClientId,report,old.ProjectJson,old.ProjectJson);
        var window=await Window(f,w,900);
        try
        {
            await window.EnterLesson(new("electronics")); window.UpdateLayout(); var circuit=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            Assert.Equal(CircuitProject.Parse(old.ProjectJson).Code,circuit.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            Assert.Equal(old.ProjectJson,File.ReadAllText(file)); Assert.True(await circuit.Synchronize());
            var teacher=await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}"); Assert.Equal(old.ClientId,Assert.Single(teacher.Works).ClientId); Assert.Equal(report,Assert.Single(teacher.Reports).Id);
        }
        finally { await Quit(window); }
    }
}
