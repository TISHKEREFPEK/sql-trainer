using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Classroom.UiTests;

public sealed class Esp32S3CircuitUiTests
{
    [Fact]
    public void BoardRoundTripsWithCorrectHeadersAndFloatingInputs()
    {
        var project = ButtonCircuit();
        var json = project.Serialize();
        ElectronicsProjectFormat.Inspect(json);
        Assert.Equal(json, CircuitProject.Parse(json).Serialize());
        Assert.Equal(44, Esp32S3Board.Pins.Length);
        Assert.Equal("J1.18:GPIO12", Esp32S3Board.Gpio(12));
        Assert.Equal("J3.9:GPIO39", Esp32S3Board.Gpio(39));
        Assert.Null(Esp32S3Board.Gpio(22));
        var board = project.Parts[0];
        var floating = CircuitSolver.Solve(project);
        Assert.Null(Esp32S3Board.Inputs(board, floating, [12], null)[12]);
        Assert.Equal(3.3, floating.Voltages[new("board", "J1.2:3V3")], 6);
        Assert.Equal(0, floating.Voltages[new("board", "J3.1:GND")]);

        var drives = Esp32S3Board.Drives(new Dictionary<int, Esp32S3PadState>
        {
            [12] = new(true, false, false, true, false),
            [39] = new(false, true, true, false, false)
        });
        var pulled = CircuitSolver.Solve(project, drives);
        Assert.True(Esp32S3Board.Inputs(board, pulled, [12], drives)[12]);
        Assert.True(pulled.Parts["led"].Lit);
        project.Parts[1] = project.Parts[1] with { Active = true };
        Assert.False(Esp32S3Board.Inputs(board, CircuitSolver.Solve(project, drives), [12], drives)[12]);
    }

    [Esp32S3GpioFact]
    public Task ClickingTheSchematicButtonChangesTheRunningS3AndLed() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Esp32S3CircuitUiTests).Assembly)
            .Dispatch(CheckCircuitUi, CancellationToken.None);

    static async Task<bool> CheckCircuitUi()
    {
        ThemeManager.Apply(new());
        var project = ButtonCircuit();
        project.Code = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "esp32s3-gpio.ino"));
        var folder = Path.Combine(Path.GetTempPath(), "classroom-s3-circuit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "project.json");
        File.WriteAllText(path, project.Serialize());
        var window = new MainWindow(false, false) { Width = 1320, Height = 940 };
        using var editor = new ElectronicsScreen(window, path);
        window.Preview(editor);
        window.Show();
        window.UpdateLayout();
        try
        {
            Button Command(Func<Button, bool> predicate) => editor.GetVisualDescendants().OfType<Button>().Single(predicate);
            bool LedIsLit() => editor.GetVisualDescendants().OfType<Canvas>()
                .Single(c => c.Tag is CircuitPart { Id: "led" }).Children.OfType<Ellipse>().Any();

            Assert.Contains(editor.GetVisualDescendants().OfType<Button>(),
                b => AutomationProperties.GetName(b) == "Добавить: ESP32-S3-DevKitC-1 · GPIO");
            Command(b => b.Content as string == "Запустить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(LedIsLit, TimeSpan.FromMinutes(3));
            ClickSchematicSwitch(window, editor);
            await WaitFor(() => !LedIsLit(), TimeSpan.FromSeconds(8));
            ClickSchematicSwitch(window, editor);
            await WaitFor(LedIsLit, TimeSpan.FromSeconds(8));
            using (var frame = window.CaptureRenderedFrame())
            {
                var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(output);
                frame!.Save(Path.Combine(output, "esp32s3-circuit-run.png"));
            }

            Command(b => AutomationProperties.GetName(b) == "Остановить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => !LedIsLit(), TimeSpan.FromSeconds(5));
            editor.Suspend();
            var saved = CircuitProject.Parse(File.ReadAllText(path));
            Assert.Equal(project.Code, saved.Code);
            Assert.Equal(project.Wires, saved.Wires);
            Assert.False(saved.Parts[1].Active);
            return true;
        }
        finally { window.Close(); }
    }

    static CircuitProject ButtonCircuit() => new()
    {
        Parts =
        [
            new("board", PartKind.Esp32S3, 245, 70, ModelId: Esp32S3Board.ModelId),
            new("switch", PartKind.Switch, 30, 240),
            new("resistor", PartKind.Resistor, 536, 155, 330),
            new("led", PartKind.Led, 740, 130, 2)
        ],
        Wires =
        [
            new("input", new("board", Esp32S3Board.Gpio(12)!), new("switch", "1")),
            new("switch-return", new("switch", "2"), new("board", "J3.1:GND")),
            new("drive", new("board", Esp32S3Board.Gpio(39)!), new("resistor", "1")),
            new("led-input", new("resistor", "2"), new("led", "A")),
            new("led-return", new("led", "K"), new("board", Esp32S3Board.Ground))
        ]
    };

    static void ClickSchematicSwitch(Window window, ElectronicsScreen editor)
    {
        window.UpdateLayout();
        var hit = editor.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CircuitInputToggle");
        var center = hit.TranslatePoint(new(hit.Bounds.Width / 2, hit.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    static async Task WaitFor(Func<bool> predicate, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!predicate()) await Task.Delay(20, deadline.Token);
    }
}
