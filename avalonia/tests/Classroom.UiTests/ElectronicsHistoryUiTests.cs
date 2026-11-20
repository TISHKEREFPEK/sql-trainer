using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsHistoryUiTests
{
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task NativeEditsUndoAndIntermediateResizeAreDurableBeforeAutosave(int width)
    {
        await using var f = await LiveClassroom.Start(); var dir = Path.Combine(Path.GetTempPath(), "classroom-electronics-journal-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "project.json"); File.WriteAllText(file, CircuitProject.Blink().Serialize());
        var window = new MainWindow(false, false) { Width = width, Height = 900 }; using var view = new ElectronicsScreen(window, file, f.Student);
        window.Preview(view); window.Show(); window.UpdateLayout(); string latest;
        try
        {
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single(); editor.Document.UndoStack.ClearAll();
            editor.Document.Insert(editor.Document.TextLength, "\n// один 😀"); editor.Document.Insert(editor.Document.TextLength, "\n// два");
            ApplicationCommands.Undo.Execute(null, editor.TextArea); ApplicationCommands.Redo.Execute(null, editor.TextArea);
            Assert.Equal(new[] { "code-edit", "code-edit", "code-undo", "code-redo" }, view.LocalHistory!.Changes.TakeLast(4).Select(c => c.Kind));
            Assert.Equal(editor.Text, CircuitProject.Parse(view.LocalHistory.Replay()).Code);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            if (width == 900) typeof(ElectronicsScreen).GetField("zoom", flags)!.SetValue(view, 0.45);
            typeof(ElectronicsScreen).GetField("selected", flags)!.SetValue(view, "r"); typeof(ElectronicsScreen).GetMethod("Inspect", flags)!.Invoke(view, null); typeof(ElectronicsScreen).GetMethod("Draw", flags)!.Invoke(view, null); window.UpdateLayout();
            Point Handle() => view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ResizeHandle2").TranslatePoint(new(8, 8), window)!.Value;
            var start = Handle(); var middle = start + new Vector(12, 12); var end = start + new Vector(25, 20);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(middle, RawInputModifiers.LeftMouseButton); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); window.UpdateLayout();
            var history = view.LocalHistory!; var steps = history.Changes.Where(c => c.Kind == "resize").ToArray(); Assert.Equal(2, steps.Length);
            Assert.Equal(steps[0].GestureId, steps[1].GestureId); Assert.NotEqual(history.Replay((int)steps[0].Sequence), history.Replay((int)steps[1].Sequence));
            view.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Отменить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("undo", view.LocalHistory!.Changes[^1].Kind); latest = view.LocalHistory.Replay();
            view.Dispose(); window.Close();
        }
        finally { view.Dispose(); window.Close(); }
        var context = new ElectronicsHistoryContext(f.StudentId, f.Student.Connection.Fingerprint, f.Student.LessonId!, null);
        using (var reopened = new ElectronicsHistoryStore(file + ".history", context, null)) Assert.Equal(latest, reopened.ProjectJson);
        // A damaged mirror is preserved and the verified newer journal opens next time.
        File.WriteAllText(file, "{damaged source");
        var recoveryWindow = new MainWindow(false, false) { Width = width, Height = 900 };
        using var recovered = new ElectronicsScreen(recoveryWindow, file, f.Student);
        Assert.Equal(latest, recovered.LocalHistory!.Replay()); Assert.Equal(latest, File.ReadAllText(file));
        Assert.Equal("{damaged source", File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "project.json.history-source-*"))));
        try
        {
            recoveryWindow.Preview(recovered); recoveryWindow.Show(); recoveryWindow.UpdateLayout(); await Task.Delay(100); recoveryWindow.UpdateLayout();
            using var frame = recoveryWindow.CaptureRenderedFrame(); var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory; Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"electronics-history-recovery-{width}.png"));
        }
        finally { recovered.Dispose(); recoveryWindow.Close(); }
    }
}
