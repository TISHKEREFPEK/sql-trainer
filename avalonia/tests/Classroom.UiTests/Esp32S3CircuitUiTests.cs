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
using System.Text.RegularExpressions;
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

    [Esp32S3GpioFact]
    public Task SerialMonitorExchangesUtf8OnEachS3UartAndKeepsTheJournal() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Esp32S3CircuitUiTests).Assembly)
            .Dispatch(CheckSerialUi, CancellationToken.None);

    [Esp32S3GpioFact]
    public Task PotentiometerSuppliesTheFirstSampleAndChangesRunningArduinoAdc() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Esp32S3CircuitUiTests).Assembly)
            .Dispatch(CheckAnalogUi, CancellationToken.None);

    [Esp32S3GpioFact]
    public Task WireDrawsOnTwoPoweredDisplaysThroughTheConfiguredPins() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Esp32S3CircuitUiTests).Assembly)
            .Dispatch(CheckDisplays, CancellationToken.None);

    [Esp32S3GpioFact]
    public Task LcdReadsAndWritesThroughTheTranslatorAndReachesTheTeacherJournal() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Esp32S3CircuitUiTests).Assembly)
            .Dispatch(CheckTranslatedLcd, CancellationToken.None);

    static async Task<bool> CheckTranslatedLcd()
    {
        await using var classroom = await LiveClassroom.Start();
        ThemeManager.Apply(new());
        var project = I2cLevelShifterTests.Project(pico: false);
        var folder = Directory.CreateTempSubdirectory("classroom-s3-lcd-");
        var path = Path.Combine(folder.FullName, "project.json");
        File.WriteAllText(path, project.Serialize());
        var window = new MainWindow(false, false) { Width = 1320, Height = 940 };
        using var editor = new ElectronicsScreen(window, path, classroom.Student);
        window.Preview(editor);
        window.Show();
        window.UpdateLayout();
        try
        {
            string Output() => string.Concat(editor.LocalHistory!.Changes
                .Where(c => c.Kind == "serial-output" && c.SerialIo?.Port == 0).Select(c => c.SerialIo!.Text));
            editor.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Запустить")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => Output().Contains("READY") ||
                editor.LocalHistory!.Changes.Any(c => c.Kind == "run-error"), TimeSpan.FromMinutes(3));
            Assert.Contains("EMPTY=2", Output());
            Assert.Contains("READ=4F,8F", Output());
            Assert.Contains("READY", Output());
            var display = Assert.Single(editor.GetVisualDescendants().OfType<LcdDisplayFace>()).Frame;
            Assert.NotNull(display);
            Assert.True(display.Display && display.Backlight);
            Assert.Equal((16, 2), (display.Columns, display.Rows));
            Assert.Equal("Hello Sql", new string(display.Cells.Take(9).Select(value => (char)value).ToArray()));
            Assert.Contains(editor.GetVisualDescendants().OfType<I2cLevelShifterFace>(), face => face.IsEffectivelyVisible);
            if (Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") is { } output)
            {
                Directory.CreateDirectory(output);
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, "esp32s3-lcd-level-shifter.png"));
            }
            editor.Suspend();
            var history = editor.LocalHistory!;
            Assert.Equal(project.Serialize(), history.Replay());
            Assert.True(await editor.Synchronize());
            var events = await classroom.Admin.Get<ElectronicsJournalEvents>(
                $"teacher/electronics-journal/{classroom.StudentId}/{history.StreamId}/changes");
            var transfers = events.Events.Where(e => e.Change.I2cIo is not null).SelectMany(e => e.Change.I2cIo!).ToArray();
            Assert.Contains(transfers, t => t is { Ack: false, Address: 38, Controller: 0, Sda: 8, Scl: 9 });
            Assert.Contains(transfers, t => t is { Ack: true, Address: 39, Read: true, Controller: 0, Sda: 8, Scl: 9 });
            return true;
        }
        finally { window.Close(); }
    }

    static async Task<bool> CheckDisplays()
    {
        await using var classroom = await LiveClassroom.Start();
        ThemeManager.Apply(new());
        var project = new CircuitProject
        {
            Code = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "esp32s3-oled.ino")),
            Parts =
            [
                new("board", PartKind.Esp32S3, 270, 70, ModelId: Esp32S3Board.ModelId),
                new("first", PartKind.OledI2c, 50, 125, 60, ModelId: "oled-ssd1306-i2c"),
                new("second", PartKind.OledI2c, 585, 75, 60, ModelId: "oled-ssd1306-i2c"),
                new("unpowered", PartKind.OledI2c, 585, 290, 61, ModelId: "oled-ssd1306-i2c"),
            ],
            Wires =
            [
                new("d0", new("first", "SDA"), new("board", Esp32S3Board.Gpio(8)!)),
                new("c0", new("first", "SCL"), new("board", Esp32S3Board.Gpio(9)!)),
                new("p0", new("first", "VDD"), new("board", Esp32S3Board.V3)),
                new("g0", new("first", "GND"), new("board", Esp32S3Board.Ground)),
                new("d1", new("second", "SDA"), new("board", Esp32S3Board.Gpio(15)!)),
                new("c1", new("second", "SCL"), new("board", Esp32S3Board.Gpio(16)!)),
                new("p1", new("second", "VDD"), new("board", Esp32S3Board.V3)),
                new("g1", new("second", "GND"), new("board", Esp32S3Board.Ground)),
                new("du", new("unpowered", "SDA"), new("board", Esp32S3Board.Gpio(17)!)),
                new("cu", new("unpowered", "SCL"), new("board", Esp32S3Board.Gpio(18)!)),
                new("gu", new("unpowered", "GND"), new("board", Esp32S3Board.Ground)),
            ]
        };
        var folder = Directory.CreateTempSubdirectory("classroom-s3-oled-");
        var path = Path.Combine(folder.FullName, "project.json");
        File.WriteAllText(path, project.Serialize());
        var window = new MainWindow(false, false) { Width = 1320, Height = 940 };
        using var editor = new ElectronicsScreen(window, path, classroom.Student);
        window.Preview(editor);
        window.Show();
        window.UpdateLayout();
        try
        {
            string Output() => string.Concat(editor.LocalHistory!.Changes
                .Where(c => c.Kind == "serial-output" && c.SerialIo?.Port == 0).Select(c => c.SerialIo!.Text));
            editor.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Запустить")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => Output().Contains("OLED_DRAW="), TimeSpan.FromMinutes(3));
            Assert.Contains("WRONG_PINS=2", Output());
            Assert.Contains("NO_POWER=2", Output());
            Assert.Contains("WRITE_ONLY=0", Output());
            Assert.Contains("OLED_DRAW=1,1", Output());
            var faces = editor.GetVisualDescendants().OfType<OledDisplayFace>().ToArray();
            Assert.Equal(3, faces.Length);
            foreach (var (id, pattern) in new[] { ("first", 11), ("second", 101) })
            {
                var face = Assert.Single(faces, p => p.Frame?.Id == id);
                Assert.True(face.Frame!.Display);
                var memory = Convert.FromBase64String(face.Frame.Memory);
                Assert.Equal(Enumerable.Range(pattern, 73).Select(i => (byte)i), memory.Take(73));
            }
            Assert.False(Assert.Single(faces, p => p.Frame?.Id == "unpowered").Frame!.Powered);
            var transfers = editor.LocalHistory!.Changes.Where(c => c.I2cIo is not null).SelectMany(c => c.I2cIo!).ToArray();
            Assert.Contains(transfers, t => t is { Ack: true, Controller: 0, Sda: 8, Scl: 9 } && t.Data.Length == 74);
            Assert.Contains(transfers, t => t is { Ack: true, Controller: 1, Sda: 15, Scl: 16 } && t.Data.Length == 74);
            Assert.Contains(transfers, t => t is { Ack: false, Controller: 0, Sda: 5, Scl: 6, Address: 60 });
            Assert.Contains(transfers, t => t is { Ack: false, Controller: 1, Address: 61 });
            if (Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") is { } output)
            {
                Directory.CreateDirectory(output);
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, "esp32s3-oled-two-buses.png"));
            }
            editor.Suspend();
            var history = editor.LocalHistory;
            Assert.Equal(project.Serialize(), history.Replay());
            Assert.True(await editor.Synchronize());
            var events = await classroom.Admin.Get<ElectronicsJournalEvents>(
                $"teacher/electronics-journal/{classroom.StudentId}/{history.StreamId}/changes");
            var delivered = events.Events.Where(e => e.Change.I2cIo is not null).SelectMany(e => e.Change.I2cIo!).ToArray();
            Assert.Contains(delivered, t => t is { Ack: true, Controller: 0, Sda: 8, Scl: 9 } && t.Data.Length == 74);
            Assert.Contains(delivered, t => t is { Ack: true, Controller: 1, Sda: 15, Scl: 16 } && t.Data.Length == 74);
            Assert.Contains(delivered, t => t is { Ack: false, Controller: 1, Address: 61 });
            return true;
        }
        finally { window.Close(); }
    }

    static async Task<bool> CheckAnalogUi()
    {
        await using var classroom = await LiveClassroom.Start();
        ThemeManager.Apply(new());
        var project = new CircuitProject
        {
            Code = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "esp32s3-adc.ino")),
            Parts =
            [
                new("board", PartKind.Esp32S3, 270, 90, ModelId: Esp32S3Board.ModelId),
                new("pot", PartKind.Pot3, 45, 125, 10_000, Setting: 0.25),
                new("reference", PartKind.AnalogSensor, 720, 125, 0.775)
            ],
            Wires =
            [
                new("low", new("pot", "1"), new("board", Esp32S3Board.Ground)),
                new("high", new("pot", "3"), new("board", Esp32S3Board.V3)),
                new("wiper", new("pot", "W"), new("board", Esp32S3Board.Gpio(1)!)),
                new("reference-out", new("reference", "+"), new("board", Esp32S3Board.Gpio(11)!)),
                new("reference-return", new("reference", "−"), new("board", Esp32S3Board.Ground))
            ]
        };
        var folder = Path.Combine(Path.GetTempPath(), "classroom-s3-adc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "project.json");
        File.WriteAllText(path, project.Serialize());
        var window = new MainWindow(false, false) { Width = 1320, Height = 940 };
        using var editor = new ElectronicsScreen(window, path, classroom.Student);
        window.Preview(editor);
        window.Show();
        window.UpdateLayout();
        try
        {
            string Output() => string.Concat(editor.LocalHistory!.Changes
                .Where(c => c.Kind == "serial-output" && c.SerialIo?.Port == 0).Select(c => c.SerialIo!.Text));
            editor.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Запустить")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var firstSample = new Regex(@"ADC_START=(\d+),(\d+)\r?\n");
            await WaitFor(() => firstSample.IsMatch(Output()), TimeSpan.FromMinutes(3));
            var startup = firstSample.Match(Output());
            Assert.InRange(int.Parse(startup.Groups[1].Value), 1087, 1092);
            Assert.Equal("1024", startup.Groups[2].Value);

            var track = editor.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CircuitInputSlider" &&
                AutomationProperties.GetName(b)?.StartsWith("Положение", StringComparison.Ordinal) == true);
            Point At(double fraction) => track.TranslatePoint(new(10 + (track.Width - 20) * fraction, 22), window)!.Value;
            var start = At(0.25);
            var end = At(0.75);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            var updated = new Regex(@"ADC_SAMPLE=(?:326[6-9]|327[0-3]),1024\r?\n");
            await WaitFor(() => updated.IsMatch(Output()), TimeSpan.FromSeconds(8));
            editor.Suspend();
            var saved = CircuitProject.Parse(File.ReadAllText(path));
            Assert.Equal(0.75, saved.Parts.Single(p => p.Id == "pot").Setting);
            Assert.Contains(editor.LocalHistory!.Changes, c => c.Kind == "control-change");
            Assert.Equal(saved.Serialize(), editor.LocalHistory.Replay());
            return true;
        }
        finally { window.Close(); }
    }

    static async Task<bool> CheckSerialUi()
    {
        await using var classroom = await LiveClassroom.Start();
        ThemeManager.Apply(new());
        var project = ButtonCircuit();
        project.Code = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "esp32s3-gpio.ino"));
        var folder = Path.Combine(Path.GetTempPath(), "classroom-s3-uart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "project.json");
        File.WriteAllText(path, project.Serialize());
        var window = new MainWindow(false, false) { Width = 1320, Height = 940 };
        using var editor = new ElectronicsScreen(window, path, classroom.Student);
        window.Preview(editor);
        window.Show();
        window.UpdateLayout();
        try
        {
            T Find<T>(Func<T, bool> test) where T : Control =>
                editor.GetVisualDescendants().OfType<T>().Single(test);
            string Output(int port) => string.Concat(editor.LocalHistory!.Changes
                .Where(c => c.Kind == "serial-output" && c.SerialIo?.Port == port).Select(c => c.SerialIo!.Text));

            Find<Button>(b => b.Content as string == "Запустить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => Output(0).Contains("GPIO_READY") && Output(1).Contains("UART1_READY") &&
                Output(2).Contains("UART2_READY"), TimeSpan.FromMinutes(3));
            var tabs = Find<TabControl>(t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "Последовательный монитор"));
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(i => i.Header as string == "Последовательный монитор");
            window.UpdateLayout();
            var channel = Find<ComboBox>(c => AutomationProperties.GetName(c) == "Порт последовательного монитора");
            Assert.Equal(new[] { "UART0 · Serial", "UART1 · Serial1", "UART2 · Serial2" }, channel.Items.Cast<string>());
            var input = Find<TextBox>(t => t.Watermark == "Данные для платы");
            var send = Find<Button>(b => b.Content as string == "Отправить");
            var sent = new List<ElectronicsSerialIo>();
            for (var port = 0; port < 3; port++)
            {
                // The multibyte character crosses a FIFO-sized exchange on each port.
                var payload = new string((char)('a' + port), 127) + $"🙂 Проверка UART{port}";
                channel.SelectedIndex = port;
                input.Text = payload;
                await WaitFor(() => send.IsEnabled, TimeSpan.FromSeconds(8));
                send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => Output(port).Contains(payload + "\n"), TimeSpan.FromSeconds(8));
                Assert.DoesNotContain(Enumerable.Range(0, 3).Where(i => i != port), i => Output(i).Contains(payload));
                var expected = new ElectronicsSerialIo(port, payload + "\n");
                sent.Add(expected);
                Assert.Contains(editor.LocalHistory!.Changes, c => c.Kind == "serial-input" && c.SerialIo == expected);
                Assert.Contains(editor.GetVisualDescendants().OfType<TextBox>(), t => t.IsReadOnly && t.Text?.Contains(payload) == true);
            }
            editor.Suspend();
            var history = editor.LocalHistory!;
            Assert.Equal(project.Serialize(), history.Replay());
            var route = $"teacher/electronics-journal/{classroom.StudentId}";
            ElectronicsJournalInfo? stream = null;
            for (var attempt = 0; attempt < 30 && stream is null; attempt++)
            {
                await editor.Synchronize();
                var streams = await classroom.Admin.Get<ElectronicsJournalInfo[]>(route);
                stream = streams.SingleOrDefault(s => s.Id == history.StreamId);
                if (stream is null) await Task.Delay(50);
            }
            Assert.NotNull(stream);
            var events = await classroom.Admin.Get<ElectronicsJournalEvents>($"{route}/{stream.Id}/changes");
            foreach (var request in sent)
            {
                Assert.Contains(events.Events, e => e.Change.Kind == "serial-input" && e.Change.SerialIo == request);
                var output = string.Concat(events.Events.Where(e => e.Change.Kind == "serial-output"
                    && e.Change.SerialIo?.Port == request.Port).Select(e => e.Change.SerialIo!.Text));
                Assert.Contains(request.Text, output);
            }
            return true;
        }
        finally { window.Close(); }
    }

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
