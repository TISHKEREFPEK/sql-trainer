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

public sealed class Mpu6050Tests
{
    const string firstSample = "MOTION=0,-4096,8192,-3920,0,0,-2620";
    const string changedSample = "MOTION=16384,-4096,8192,-3920,0,0,-2620";
    const string sketch = """
        #include <Wire.h>
        const uint8_t address=0x69;
        uint8_t probe(uint8_t addr) { Wire.beginTransmission(addr); return Wire.endTransmission(); }
        void setRegister(uint8_t reg, uint8_t value) {
          Wire.beginTransmission(address); Wire.write(reg); Wire.write(value); Wire.endTransmission();
        }
        bool readRegisters(uint8_t reg, uint8_t* data, uint8_t count) {
          Wire.beginTransmission(address); Wire.write(reg);
          if (Wire.endTransmission(false) || Wire.requestFrom(address,count)!=count) return false;
          for (uint8_t i=0;i<count;i++) data[i]=Wire.read();
          return true;
        }
        uint8_t readRegister(uint8_t reg) { uint8_t value=0; readRegisters(reg,&value,1); return value; }
        void setup() {
          Serial.begin(115200);
          Wire.begin();
          Serial.print("OTHER="); Serial.println(probe(0x68));
          Serial.print("ID="); Serial.println(readRegister(0x75),HEX);
          Serial.print("SLEEP="); Serial.println(readRegister(0x6b),HEX);
          setRegister(0x6b,1); setRegister(0x19,9); setRegister(0x1a,3);
          setRegister(0x1b,8); setRegister(0x1c,8);
        }
        void loop() {
          delay(100);
          uint8_t data[14];
          if (!readRegisters(0x3b,data,sizeof(data))) return;
          Serial.print("MOTION=");
          for (uint8_t i=0;i<sizeof(data);i+=2) {
            if (i) Serial.print(",");
            Serial.print((int16_t)(((uint16_t)data[i]<<8)|data[i+1]));
          }
          Serial.println();
        }
        """;

    static CircuitProject Project(PartKind kind)
    {
        var pico = kind == PartKind.Pico;
        var esp = kind == PartKind.Esp32S3;
        var mega = kind == PartKind.Mega;
        var project = new CircuitProject
        {
            Name = "MPU-6050",
            Code = esp ? sketch.Replace("Wire.begin();", "Wire.begin(8,9,100000);")
                : pico ? sketch.Replace("Wire.begin();", "Wire.setSDA(4);Wire.setSCL(5);Wire.begin();")
                    .Replace("Serial.", "Serial1.") : sketch,
            Parts =
            [
                new("board", kind, 90, 75,
                    ModelId: esp ? Esp32S3Board.ModelId : pico ? PicoBoard.ModelId : mega ? "mega2560" : "uno"),
                new("motion", PartKind.Mpu6050, 462, 136, ModelId: CircuitMpu6050.ModelId,
                    Motion: ElectronicsMotion.Rest with { Ay = -0.5, Gz = -40 })
            ]
        };
        var ground = CircuitBoards.Ground(project.Parts[0]);
        if (esp || pico)
        {
            project.Wires.AddRange([
                new("power", new("board", esp ? Esp32S3Board.V3 : PicoBoard.V3), new("motion", "VDD")),
                new("sda", new("board", esp ? Esp32S3Board.Gpio(8)! : PicoBoard.Gpio(4)!), new("motion", "SDA")),
                new("scl", new("board", esp ? Esp32S3Board.Gpio(9)! : PicoBoard.Gpio(5)!), new("motion", "SCL"))
            ]);
        }
        else
        {
            project.Parts.Add(new("adapter", PartKind.I2cLevelShifter, 322, 80, ModelId: I2cLevelShifter.ModelId));
            project.Parts.Add(new("supply", PartKind.Battery, 326, 360, 3.3));
            project.Wires.AddRange([
                new("power", new("supply", "+"), new("motion", "VDD")),
                new("supply-gnd", new("supply", "−"), new("board", ground)),
                new("adapter-gnd", new("board", ground), new("adapter", "GND")),
                new("adapter-low", new("supply", "+"), new("adapter", "LV")),
                new("adapter-high", new("board", "5V"), new("adapter", "HV")),
                new("sda", new("board", mega ? "D20" : "A4"), new("adapter", "HV1")),
                new("scl", new("board", mega ? "D21" : "A5"), new("adapter", "HV2")),
                new("data", new("adapter", "LV1"), new("motion", "SDA")),
                new("clock", new("adapter", "LV2"), new("motion", "SCL"))
            ]);
        }
        project.Wires.Add(new("return", new("board", ground), new("motion", "GND")));
        project.Wires.Add(new("address", new("motion", "VDD"), new("motion", "AD0")));
        return project;
    }

    [Fact]
    public void AddressPowerAndStoredMotionFollowTheSchematic()
    {
        var project = Project(PartKind.Esp32S3);
        Esp32S3I2cDevice Device() => Assert.Single(Esp32S3Board.I2cDevices(project, project.Parts[0], CircuitSolver.Solve(project)));
        var original = project.Serialize();
        Assert.Equal((project.Name, 2, 5), ElectronicsProjectFormat.Inspect(original));
        Assert.Equal(original, CircuitProject.Parse(original).Serialize());
        Assert.True(Device() is { Address: 0x69, Connected: true, Powered: true });
        project.Wires.RemoveAll(w => w.Id == "address");
        Assert.True(Device() is { Address: 0x68, Connected: true });
        project.Wires.Add(new("bad-address", new("board", Esp32S3Board.V5), new("motion", "AD0")));
        Assert.False(Device().Connected);
        project.Wires.RemoveAll(w => w.Id is "return" or "bad-address");
        Assert.False(Device().Powered);
        Assert.Throws<InvalidDataException>(() => ElectronicsProjectFormat.Inspect(original.Replace("\"ay\": -0.5", "\"ay\": 17")));
    }

    [Theory]
    [InlineData(PartKind.Uno, "uno")]
    [InlineData(PartKind.Mega, "mega2560")]
    public async Task AvrReadsSamplesThroughTheConverter(PartKind kind, string profile)
    {
        var project = Project(kind);
        var device = Assert.Single(CircuitI2c.Devices(project));
        Assert.True(device is { Connected: true, Powered: true, Address: 0x69 });
        var image = await FirmwareRunner.Compile(project.Code, CancellationToken.None, profile);
        using var runner = new FirmwareRunner();
        await runner.Start(image, profile);
        var board = AvrBoards.Get(profile);
        var serial = "";
        for (var i = 0; i < 40 && !serial.Contains(firstSample); i++)
        {
            var frame = await runner.Step(new bool[board.DigitalCount], new double[board.AnalogCount],
                devices: CircuitI2c.Devices(project));
            serial += frame.Serial;
        }
        CheckSetupAndFirstSample(serial);
    }

    [Fact]
    public async Task PicoReadsSamplesAndUpdatesAcrossFrames()
    {
        var project = Project(PartKind.Pico);
        var image = await PicoCompiler.Compile(project.Code, CancellationToken.None);
        using var runner = new PicoFirmwareRunner();
        await runner.Start(image);
        var serial = "";
        PicoFrame? frame = null;
        async Task Advance(string expected)
        {
            for (var i = 0; i < 100 && !serial.Contains(expected); i++)
            {
                var circuit = CircuitSolver.Solve(project, frame?.Pins);
                var devices = CircuitPico.Devices(project, circuit);
                var inputs = CircuitPico.Inputs(project, circuit, devices);
                frame = await runner.Step(analog: inputs.Analog, devices: devices, gpioInputs: inputs.Digital);
                serial += frame.SerialPorts[1];
            }
            Assert.Contains(expected, serial);
        }
        await Advance(firstSample);
        CheckSetupAndFirstSample(serial);
        project.Parts[1] = project.Parts[1] with { Motion = project.Parts[1].Motion! with { Ax = 2 } };
        await Advance(changedSample);
    }

    [Esp32S3GpioFact]
    public Task NativeSliderChangesSamplingAndReplaysInTheTeacherJournal() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Mpu6050Tests).Assembly).Dispatch(CheckNative, CancellationToken.None);

    static async Task<bool> CheckNative()
    {
        await using var classroom = await LiveClassroom.Start();
        ThemeManager.Apply(new());
        var project = Project(PartKind.Esp32S3);
        var folder = Directory.CreateTempSubdirectory("classroom-mpu-");
        var path = Path.Combine(folder.FullName, "project.json");
        File.WriteAllText(path, project.Serialize());
        var window = new MainWindow(false, false) { Width = 1320, Height = 940 };
        using var editor = new ElectronicsScreen(window, path, classroom.Student);
        window.Preview(editor);
        window.Show();
        window.UpdateLayout();
        try
        {
            string Serial() => string.Concat(editor.LocalHistory!.Changes
                .Where(c => c.Kind == "serial-output" && c.SerialIo?.Port == 0).Select(c => c.SerialIo!.Text));
            async Task WaitFor(string value)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (!Serial().Contains(value) && !editor.LocalHistory!.Changes.Any(c => c.Kind == "run-error"))
                    await Task.Delay(50, timeout.Token);
                Assert.Contains(value, Serial());
            }
            editor.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Запустить")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(firstSample);
            CheckSetupAndFirstSample(Serial());
            var track = editor.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CircuitInputSlider" &&
                AutomationProperties.GetName(b)?.StartsWith("Ax ·") == true);
            // Pick a point beyond the end of the rail but within the control,
            // so the headless mouse's pixel rounding cannot miss the endpoint.
            var edge = track.TranslatePoint(new(track.Width - 5, 22), window)!.Value;
            window.MouseDown(edge, MouseButton.Left);
            window.MouseUp(edge, MouseButton.Left);
            await WaitFor("MOTION=32767,-4096,8192,-3920,0,0,-2620");
            var updatedTrack = editor.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CircuitInputSlider" &&
                AutomationProperties.GetName(b)?.StartsWith("Ax ·") == true);
            var center = updatedTrack.TranslatePoint(new(updatedTrack.Width / 2, 15), window)!.Value;
            window.MouseDown(center, MouseButton.Right);
            window.MouseUp(center, MouseButton.Right);
            window.UpdateLayout();
            Assert.Equal(16, Assert.Single(editor.GetVisualDescendants().OfType<Slider>(),
                control => AutomationProperties.GetName(control) == "Ax").Value);
            Assert.Equal(25, Assert.Single(editor.GetVisualDescendants().OfType<Slider>(),
                control => AutomationProperties.GetName(control) == "Температура").Value);
            if (Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") is { } output)
            {
                Directory.CreateDirectory(output);
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, "esp32s3-mpu6050.png"));
            }
            editor.Suspend();
            var history = editor.LocalHistory!;
            Assert.Equal(16, CircuitProject.Parse(history.Replay()).Parts.Single(p => p.Id == "motion").Motion!.Ax);
            Assert.True(await editor.Synchronize());
            var journal = await classroom.Admin.Get<ElectronicsJournalEvents>(
                $"teacher/electronics-journal/{classroom.StudentId}/{history.StreamId}/changes");
            Assert.Contains(journal.Events, e => e.Change.Kind == "control-change" && e.Change.GestureId is not null);
            Assert.Contains(journal.Events.SelectMany(e => e.Change.I2cIo ?? []),
                packet => packet is { Address: 0x69, Read: true, Ack: true, Controller: 0 } && packet.Data.Length == 14);
            return true;
        }
        finally { window.Close(); }
    }

    static void CheckSetupAndFirstSample(string serial)
    {
        Assert.Contains("OTHER=2", serial);
        Assert.Contains("ID=68", serial);
        Assert.Contains("SLEEP=40", serial);
        Assert.Contains(firstSample, serial);
    }
}
