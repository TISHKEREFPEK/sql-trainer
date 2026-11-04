using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsWorkspaceUiTests
{
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public void SchemeAndCodeStayVisibleAndPanelChangesKeepDraftAndCamera(int width)
    {
        ThemeManager.Apply(new());
        var directory = Path.Combine(Path.GetTempPath(), "classroom-split-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var file = Path.Combine(directory, "project.json");
        var original = CircuitExamples.Servo(); File.WriteAllText(file, original.Serialize());
        var window = new MainWindow(false, false) { Width = width, Height = 900 };
        using var view = new ElectronicsScreen(window, file); window.Preview(view); window.Show(); window.UpdateLayout();
        try
        {
            var viewport = view.GetVisualDescendants().OfType<CircuitViewport>().Single();
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            Assert.True(viewport.Bounds.Height >= 160); Assert.True(editor.Bounds.Height > 100);
            Assert.True(editor.TranslatePoint(new Point(), window)!.Value.Y > viewport.TranslatePoint(new Point(0, viewport.Bounds.Height), window)!.Value.Y);
            const string draft = "// сохранение при смене панелей\nvoid setup(){}\nvoid loop(){}";
            editor.Text = draft;
            var center = viewport.ToWorld(new(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2));
            void Click(string name)
            {
                view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            }
            Click("Панель деталей");
            var after = viewport.ToWorld(new(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2));
            Assert.Equal(center.X, after.X, 5); Assert.Equal(center.Y, after.Y, 5);
            Click("Панель свойств"); Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "Выберите деталь или провод на схеме.");
            Click("Панель свойств"); Click("Панель деталей");
            var divider = view.GetVisualDescendants().OfType<GridSplitter>().Single();
            var at = divider.TranslatePoint(new(divider.Bounds.Width / 2, 3), window)!.Value;
            var beforeHeight = viewport.Bounds.Height;
            window.MouseDown(at, MouseButton.Left); window.MouseMove(new(at.X, at.Y - 35), RawInputModifiers.LeftMouseButton); window.MouseUp(new(at.X, at.Y - 35), MouseButton.Left); window.UpdateLayout();
            Assert.True(viewport.Bounds.Height < beforeHeight - 15);
            var resized = viewport.Bounds.Height;
            Click("Панель кода"); Assert.True(viewport.Bounds.Height > resized + 100);
            Click("Панель кода"); Assert.Equal(resized, viewport.Bounds.Height, 2);
            Assert.Equal(draft, editor.Text);
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex = 1; window.UpdateLayout();
            Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), t => t.IsReadOnly);
            tabs.SelectedIndex = 0; window.UpdateLayout(); Assert.Same(editor, view.GetVisualDescendants().OfType<TextEditor>().Single()); Assert.Equal(draft, editor.Text);
            view.Suspend(); var saved = CircuitProject.Parse(File.ReadAllText(file)); Assert.Equal(draft, saved.Code);
            saved.Code = original.Code; Assert.Equal(original.Serialize(), saved.Serialize());
            using var frame = window.CaptureRenderedFrame(); var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"electronics-split-{width}.png"));
        }
        finally { window.Close(); }
    }
}
