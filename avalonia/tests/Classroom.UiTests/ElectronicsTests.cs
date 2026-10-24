using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsTests
{
    [Fact]
    public void VoltageDividerAndLedRespectWiringPolarityAndResistance()
    {
        var p = new CircuitProject();
        p.Parts.AddRange([new("battery", PartKind.Battery, 0, 0, 5), new("a", PartKind.Resistor, 200, 0, 1000), new("b", PartKind.Resistor, 400, 0, 1000)]);
        p.Wires.AddRange([new("1", new("battery", "+"), new("a", "1")), new("2", new("a", "2"), new("b", "1")), new("3", new("b", "2"), new("battery", "−"))]);
        var result = CircuitSolver.Solve(p);
        Assert.InRange(result.Voltages[new("b", "1")], 2.49, 2.51);
        var blink = CircuitProject.Blink();
        var pins = Enumerable.Range(0, 14).Select(_ => new PinDrive(false, 0)).ToArray(); pins[13] = new(true, 5);
        result = CircuitSolver.Solve(blink, pins);
        Assert.True(result.Parts["led"].Lit); Assert.InRange(result.Parts["led"].Current, 0.007, 0.01);
        pins[13] = new(true, 0); Assert.False(CircuitSolver.Solve(blink, pins).Parts["led"].Lit);
        pins[13] = new(true, 5);
        blink.Wires.RemoveAt(2); blink.Wires.RemoveAt(1);
        blink.Wires.AddRange([new("reverse-a", new("r", "2"), new("led", "K")), new("reverse-b", new("led", "A"), new("uno", "GND"))]);
        Assert.False(CircuitSolver.Solve(blink, pins).Parts["led"].Lit);
        p.Wires.Add(new("short", new("battery", "+"), new("battery", "−")));
        Assert.Contains(CircuitSolver.Solve(p).Warnings, warning => warning.Contains("замыкание"));
    }

    [Fact]
    public void ProjectRoundTripAndInvalidImportsDoNotSilentlyChangeSchema()
    {
        var p = CircuitProject.Blink();
        Assert.Equal(p.Serialize(), CircuitProject.Parse(p.Serialize()).Serialize());
        Assert.Throws<InvalidDataException>(() => CircuitProject.Parse(p.Serialize().Replace("\"version\": 1", "\"version\": 99")));
        p.Wires.Add(new("bad", new("missing", "D13"), new("uno", "GND")));
        Assert.Throws<InvalidDataException>(p.Validate);
    }

    [Fact]
    public async Task FirmwareReallyCompilesRunsSerialAndReportsCompilerErrors()
    {
        Assert.True(FirmwareRunner.Available, "Run avalonia/scripts/prepare-electronics.ps1 before the full offline firmware test suite.");
        var code = "void setup(){Serial.begin(9600);pinMode(13,OUTPUT);}\nvoid loop(){digitalWrite(13,HIGH);Serial.println(\"offline\");delay(100);digitalWrite(13,LOW);delay(100);}";
        var hex = await FirmwareRunner.Compile(code, CancellationToken.None);
        Assert.StartsWith(":", hex);
        using var runner = new FirmwareRunner(); await runner.Start(hex);
        var highs = 0; var lows = 0; var serial = "";
        for (var i = 0; i < 10; i++)
        {
            var frame = await runner.Step(new bool[14], new double[6]);
            serial += frame.Serial;
            if (frame.Pins[13].Voltage > 4) highs++;
            if (frame.Pins[13].Voltage < 1) lows++;
        }
        Assert.True(highs > 0 && lows > 0); Assert.Contains("offline", serial);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => FirmwareRunner.Compile("void setup(){ this_is_invalid_code; } void loop(){}", CancellationToken.None));
        Assert.Contains("this_is_invalid_code", error.Message);
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public void NativeEditorRendersAndKeepsItsProjectSeparate(int width)
    {
        ThemeManager.Apply(new());
        var directory = Path.Combine(Path.GetTempPath(), "classroom-electronics-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "project.json");
        var window = new MainWindow(true, false) { Width = width, Height = 900 };
        using var view = new ElectronicsScreen(window, file);
        window.Preview(view); window.Show(); window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "Запустить");
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(output); frame!.Save(Path.Combine(output, $"electronics-{width}.png"));
        view.Suspend();
        Assert.Equal("Arduino Uno · мигание светодиода", CircuitProject.Parse(File.ReadAllText(file)).Name);
        window.Close();
    }
}
