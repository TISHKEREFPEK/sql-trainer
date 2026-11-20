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

namespace Classroom.UiTests;

public sealed class Ds1307Tests
{
    static readonly long leapEve = (new DateTime(2024, 2, 28, 23, 59, 58) - new DateTime(2000, 1, 1)).Ticks / TimeSpan.TicksPerSecond;
    const string firmware = """
        #include <Wire.h>
        const uint8_t address = 0x68;
        bool readRtc(uint8_t reg, uint8_t* dst, uint8_t length) {
          Wire.beginTransmission(address); Wire.write(reg);
          if (Wire.endTransmission(false) || Wire.requestFrom(address, length) != length) return false;
          for (uint8_t i = 0; i < length; i++) dst[i] = Wire.read();
          return true;
        }
        void writeRtc(uint8_t reg, uint8_t value) {
          Wire.beginTransmission(address); Wire.write(reg); Wire.write(value); Wire.endTransmission();
        }
        void setup() {
          Serial.begin(115200); Wire.begin(); Wire.setClock(100000);
          uint8_t values[2] = {};
          writeRtc(0x3f, 0xa5);
          if (readRtc(0x3f, values, sizeof(values))) {
            Serial.print("WRAP="); Serial.print(values[0], HEX); Serial.print(","); Serial.println(values[1], HEX);
            writeRtc(0, values[1] & 0x7f);
          }
        }
        void loop() {
          uint8_t values[7] = {};
          if (!readRtc(0, values, sizeof(values))) { Serial.println("CLOCK=NACK"); delay(140); return; }
          Serial.print("DATE="); Serial.print(values[4], HEX); Serial.print(",");
          Serial.print(values[5], HEX); Serial.print(","); Serial.println(values[6], HEX);
          if (readRtc(0x3f, values, 1)) { Serial.print("RAM="); Serial.println(values[0], HEX); }
          delay(140);
        }
        """;

    static CircuitProject Project(PartKind board)
    {
        CircuitProject project;
        if (board is PartKind.Pico or PartKind.Esp32S3)
        {
            project = I2cLevelShifterTests.Project(board == PartKind.Pico) with { Name = "DS1307" };
            project.Parts[2] = new("lcd", PartKind.Ds1307, 625, 142, Active: true, ModelId: "ds1307-rtc", RtcStart: leapEve);
        }
        else
        {
            var mega = board == PartKind.Mega;
            project = new CircuitProject
            {
                Name = "DS1307",
                Parts = [new("board", board, 60, 80), new("lcd", PartKind.Ds1307, 625, 142, Active: true, RtcStart: leapEve)],
                Wires = [new("lp", new("lcd", "VCC"), new("board", "5V")), new("lg", new("lcd", "GND"), new("board", "GND")),
                    new("data", new("lcd", "SDA"), new("board", mega ? "D20" : "A4")),
                    new("clock", new("lcd", "SCL"), new("board", mega ? "D21" : "A5"))]
            };
        }
        project.Code = board switch
        {
            PartKind.Pico => firmware.Replace("Wire.begin();", "Wire.setSDA(4);Wire.setSCL(5);Wire.begin();").Replace("Serial.", "Serial1."),
            PartKind.Esp32S3 => firmware.Replace("Wire.begin();", "Wire.begin(8,9,100000);"),
            _ => firmware
        };
        return project;
    }

    [Fact]
    public void InitialDateAndBackupRoundTripWithAnExplicitVoltageBoundary()
    {
        var project = Project(PartKind.Esp32S3);
        var json = project.Serialize();
        Assert.Equal(("DS1307", 3, 9), ElectronicsProjectFormat.Inspect(json));
        Assert.Equal(json, CircuitProject.Parse(json).Serialize());
        Assert.Throws<InvalidDataException>(() => ElectronicsProjectFormat.Inspect(json.Replace(leapEve.ToString(), "3155760000")));
        var board = project.Parts[0];
        Assert.True(Assert.Single(Esp32S3Board.I2cDevices(project, board, CircuitSolver.Solve(project))) is
            { Address: 0x68, Powered: true, Connected: true, Backup: true, Sda: 8, Scl: 9 });
        project.Wires.RemoveAll(w => w.Id == "lp");
        Assert.False(Assert.Single(Esp32S3Board.I2cDevices(project, board, CircuitSolver.Solve(project))).Powered);
        project.Wires.Add(new("sqw", new("lcd", "SQW"), new("board", Esp32S3Board.Gpio(5)!)));
        Assert.Throws<InvalidDataException>(() => CircuitI2c.ValidateRun(project));
    }

    [Theory]
    [InlineData(PartKind.Uno, "uno")]
    [InlineData(PartKind.Mega, "mega2560")]
    public async Task AvrWireReadsTheLeapDayAndWrappedRam(PartKind kind, string profile)
    {
        var project = Project(kind);
        var image = await FirmwareRunner.Compile(project.Code, CancellationToken.None, profile);
        using var runner = new FirmwareRunner();
        await runner.Start(image, profile);
        var board = AvrBoards.Get(profile);
        var serial = "";
        for (var i = 0; i < 180 && !serial.Contains("DATE=29,2,24"); i++)
            serial += (await runner.Step(new bool[board.DigitalCount], new double[board.AnalogCount], devices: CircuitI2c.Devices(project))).Serial;
        VerifyLeapDay(serial);
    }

    [Fact]
    public async Task PicoWireReadsTheLeapDayThroughTheTranslator()
    {
        var project = Project(PartKind.Pico);
        var image = await PicoCompiler.Compile(project.Code, CancellationToken.None);
        using var runner = new PicoFirmwareRunner();
        await runner.Start(image);
        var serial = "";
        PicoFrame? frame = null;
        for (var i = 0; i < 110 && !serial.Contains("DATE=29,2,24"); i++)
        {
            var circuit = CircuitSolver.Solve(project, frame?.Pins);
            var devices = CircuitPico.Devices(project, circuit);
            var inputs = CircuitPico.Inputs(project, circuit, devices);
            frame = await runner.Step(analog: inputs.Analog, milliseconds: 40, devices: devices, gpioInputs: inputs.Digital);
            serial += frame.SerialPorts[1];
        }
        VerifyLeapDay(serial);
    }

    [Esp32S3GpioFact]
    public Task NativeBackupControlsAffectWireAndTheJournal() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Ds1307Tests).Assembly).Dispatch(VerifyNative, CancellationToken.None);

    static async Task<bool> VerifyNative()
    {
        await using var classroom = await LiveClassroom.Start();
        ThemeManager.Apply(new());
        var project = Project(PartKind.Esp32S3);
        project.Wires.RemoveAll(w => w.Id == "lp");
        project.Parts.Add(new("power", PartKind.Switch, 530, 350, Active: true));
        project.Wires.AddRange([new("lp", new("lcd", "VCC"), new("power", "1")),
            new("supply", new("board", Esp32S3Board.V5), new("power", "2"))]);
        var folder = Directory.CreateTempSubdirectory("classroom-rtc-");
        var path = Path.Combine(folder.FullName, "project.json");
        File.WriteAllText(path, project.Serialize());
        var window = new MainWindow(false, false) { Width = 1320, Height = 940 };
        using var editor = new ElectronicsScreen(window, path, classroom.Student);
        window.Preview(editor);
        window.Show();
        window.UpdateLayout();
        try
        {
            string Serial() => string.Concat(editor.LocalHistory!.Changes.Where(c => c.Kind == "serial-output" && c.SerialIo?.Port == 0).Select(c => c.SerialIo!.Text));
            async Task Wait(string value, int after = 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (!Serial().AsSpan(after).Contains(value, StringComparison.Ordinal))
                {
                    var error = editor.LocalHistory!.Changes.LastOrDefault(c => c.Kind is "report-run-error" or "report-compile-error");
                    Assert.Null(error);
                    await Task.Delay(50, timeout.Token);
                }
            }
            Point TogglePoint(string label)
            {
                var toggle = editor.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CircuitInputToggle" && AutomationProperties.GetName(b) == label);
                return toggle.TranslatePoint(new Point(toggle.Width / 2, toggle.Height / 2), window)!.Value;
            }
            void Toggle(string label, MouseButton button = MouseButton.Left)
            {
                var point = TogglePoint(label);
                window.MouseDown(point, button);
                window.MouseUp(point, button);
                window.UpdateLayout();
            }

            editor.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Запустить")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait("DATE=29,2,24");
            VerifyLeapDay(Serial());
            var offset = Serial().Length;
            Toggle("Замкнута");
            await Wait("CLOCK=NACK", offset);
            offset = Serial().Length;
            Toggle("Разомкнута");
            await Wait("RAM=A5", offset);
            offset = Serial().Length;
            Toggle("CR2032 на месте");
            Toggle("Замкнута");
            await Wait("CLOCK=NACK", offset);
            offset = Serial().Length;
            Toggle("Разомкнута");
            await Wait("RAM=0", offset);
            Toggle("Батареи нет", MouseButton.Right);
            Assert.Contains(editor.GetVisualDescendants().OfType<TextBox>(), input => input.Text == "2024-02-28 23:59:58");

            if (Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") is { } output)
            {
                Directory.CreateDirectory(output);
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, "esp32s3-ds1307.png"));
            }
            editor.Suspend();
            var history = editor.LocalHistory!;
            Assert.False(CircuitProject.Parse(history.Replay()).Parts.Single(p => p.Kind == PartKind.Ds1307).Active);
            Assert.True(await editor.Synchronize());
            var journal = await classroom.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{classroom.StudentId}/{history.StreamId}/changes");
            Assert.Contains(journal.Events, e => e.Change.Kind == "control-change");
            Assert.Contains(journal.Events.SelectMany(e => e.Change.I2cIo ?? []), packet => packet is { Address: 0x68, Read: true, Ack: true } && packet.Data.Length == 7);
            return true;
        }
        finally { window.Close(); }
    }

    static void VerifyLeapDay(string serial)
    {
        Assert.Contains("WRAP=A5,D8", serial);
        Assert.Contains("DATE=29,2,24", serial);
        Assert.Contains("RAM=A5", serial);
    }
}
