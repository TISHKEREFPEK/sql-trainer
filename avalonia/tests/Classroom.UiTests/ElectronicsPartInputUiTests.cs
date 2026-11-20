using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsPartInputUiTests
{
    [AvaloniaFact]
    public async Task DragChangesTheRunningCircuitAcrossRepaintsAndRecordsOneUndo()
    {
        await using var classroom = await LiveClassroom.Start();
        ThemeManager.Apply(new());
        var original = new CircuitProject
        {
            Parts =
            [
                new("pot", PartKind.Pot3, 100, 105, 10000, Rotation: 1, QuarterTurns: 1),
                new("supply", PartKind.Battery, 314, 105, 5),
                new("resistor", PartKind.Resistor, 480, 130, 10000)
            ],
            Wires =
            [
                new("high", new("supply", "+"), new("pot", "1")),
                new("low", new("supply", "−"), new("pot", "3")),
                new("input", new("pot", "W"), new("resistor", "1")),
                new("return", new("resistor", "2"), new("supply", "−"))
            ]
        };
        var file = ProjectFile(original);
        var window = new MainWindow(false, false) { Width = 1320, Height = 900 };
        using var view = new ElectronicsScreen(window, file, classroom.Student);
        window.Preview(view);
        window.Show();
        window.UpdateLayout();
        try
        {
            Border Track() => Input(view, "CircuitInputSlider", "Положение");
            Point At(double fraction) => Track().TranslatePoint(new(10 + (Track().Width - 20) * fraction, 22), window)!.Value;
            string Reading() => view.GetVisualDescendants().OfType<Canvas>()
                .Single(part => part.Tag is CircuitPart { Id: "resistor" })
                .Children.OfType<TextBlock>().Single(t => t.Text?.Contains(" мА") == true).Text!;

            Click(view, "Запустить");
            await Task.Delay(300);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Симуляция постоянного тока запущена.");
            var start = At(0.5);
            var middle = At(0.67);
            var end = At(0.88);
            var reading = Reading();
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(middle, RawInputModifiers.LeftMouseButton);
            var first = Track();
            await Task.Delay(300);
            Assert.NotSame(first, Track());
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            window.UpdateLayout();
            Assert.NotEqual(reading, Reading());
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Симуляция остановлена.");
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Свойства");

            var saved = CircuitProject.Parse(File.ReadAllText(file));
            Assert.Equal(original.Parts[0] with { Setting = 0.88 }, saved.Parts[0]);
            var changes = view.LocalHistory!.Changes.Where(c => c.Kind == "control-change").ToArray();
            Assert.Equal(2, changes.Length);
            Assert.NotNull(changes[0].GestureId);
            Assert.Equal(changes[0].GestureId, changes[1].GestureId);
            Assert.Equal(0.67, CircuitProject.Parse(view.LocalHistory.Replay((int)changes[0].Sequence)).Parts[0].Setting);

            Click(view, "Отменить");
            Assert.Equal(original.Serialize(), File.ReadAllText(file));
        }
        finally { view.Dispose(); window.Close(); }
    }

    [AvaloniaFact]
    public void RightClickOpensSettingsWithoutActivatingAnInputAndRightDragPans()
    {
        ThemeManager.Apply(new());
        var original = new CircuitProject
        {
            Parts =
            [
                new("button", PartKind.Switch, 30, 50),
                new("sensor", PartKind.Dht11, 230, 50, 25),
                new("range", PartKind.Hcsr04, 405, 52, 100, true),
                new("temp", PartKind.Ds18b20, 725, 60, 24)
            ]
        };
        var file = ProjectFile(original);
        var window = new MainWindow(true, false) { Width = 1120, Height = 900 };
        using var view = new ElectronicsScreen(window, file);
        window.Preview(view);
        window.Show();
        window.UpdateLayout();
        try
        {
            var viewport = view.GetVisualDescendants().OfType<CircuitViewport>().Single();
            var pan = viewport.Pan;
            var button = Center(Input(view, "CircuitInputToggle", "Разомкнута"), window);
            var moved = new Point(button.X + 32, button.Y + 17);
            window.MouseDown(button, MouseButton.Right);
            window.MouseMove(moved, RawInputModifiers.RightMouseButton);
            window.MouseUp(moved, MouseButton.Right);
            window.UpdateLayout();
            Assert.Equal(pan + new Vector(32, 17), viewport.Pan);
            Assert.True(HasButton(view, "Свойства"));

            var pot = Input(view, "CircuitInputSlider", "Влажность");
            ClickAt(window, Center(pot, window), MouseButton.Right);
            Assert.True(HasButton(view, "Скрыть свойства"));
            var inspector = view.GetVisualDescendants().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "Влажность");
            Assert.Equal(55, inspector.Value);
            pot = Input(view, "CircuitInputSlider", "Влажность");
            ClickAt(window, pot.TranslatePoint(new(pot.Width - 10, 22), window)!.Value, MouseButton.Left);
            Assert.Equal(90, inspector.Value);
            pot = Input(view, "CircuitInputSlider", "Температура ·");
            ClickAt(window, pot.TranslatePoint(new(10 + 0.2 * (pot.Width - 20), 22), window)!.Value, MouseButton.Left);
            Assert.Equal(10, view.GetVisualDescendants().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "Температура").Value);
            Assert.Equal(90, inspector.Value);

            Click(view, "Скрыть свойства");
            ClickAt(window, Center(Input(view, "CircuitInputToggle", "Разомкнута"), window), MouseButton.Right);
            Assert.True(HasButton(view, "Замкнуть"));
            ClickAt(window, Center(Input(view, "CircuitInputToggle", "Разомкнута"), window), MouseButton.Left);
            Assert.True(HasButton(view, "Разомкнуть"));
            Assert.True(HasButton(view, "Скрыть свойства"));
            Click(view, "Скрыть свойства");
            var targetToggle = Input(view, "CircuitInputToggle", "Цель на месте");
            var rangeSlider = Input(view, "CircuitInputSlider", "Расстояние до цели");
            Assert.True(Center(rangeSlider, window).Y - Center(targetToggle, window).Y > 30);
            ClickAt(window, rangeSlider.TranslatePoint(new(rangeSlider.Width - 10, 22), window)!.Value, MouseButton.Left);
            ClickAt(window, Center(Input(view, "CircuitInputToggle", "Цель на месте"), window), MouseButton.Left);
            Assert.True(HasButton(view, "Свойства"));
            view.Suspend();
            var saved = CircuitProject.Parse(File.ReadAllText(file));
            Assert.Equal(original.Parts[0] with { Active = true }, saved.Parts[0]);
            Assert.Equal(original.Parts[1] with { Value = 10, Setting = 1 }, saved.Parts[1]);
            Assert.Equal(original.Parts[2] with { Value = 400, Active = false }, saved.Parts[2]);
            Assert.Equal(original.Parts[3], saved.Parts[3]);
            using var frame = window.CaptureRenderedFrame();
            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(output);
            frame!.Save(Path.Combine(output, "electronics-direct-inputs.png"));
        }
        finally { view.Dispose(); window.Close(); }
    }

    static string ProjectFile(CircuitProject project)
    {
        var folder = Path.Combine(Path.GetTempPath(), "classroom-circuit-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "project.json");
        File.WriteAllText(file, project.Serialize());
        return file;
    }

    static Border Input(ElectronicsScreen view, string name, string label) =>
        view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == name && AutomationProperties.GetName(b)?.StartsWith(label) == true);

    static Point Center(Control control, Window window) =>
        control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    static bool HasButton(ElectronicsScreen view, string text) =>
        view.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == text && b.IsEffectivelyVisible);

    static void Click(ElectronicsScreen view, string text)
    {
        view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == text && b.IsEffectivelyVisible)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        view.UpdateLayout();
    }

    static void ClickAt(Window window, Point point, MouseButton button)
    {
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        window.UpdateLayout();
    }
}
