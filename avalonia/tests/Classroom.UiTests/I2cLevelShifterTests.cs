using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class I2cLevelShifterTests
{
    internal static CircuitProject Project(bool pico)
    {
        var project = new CircuitProject
        {
            Name = "LCD · 3,3 / 5 В",
            Code = LcdI2cTests.Program.Replace("Wire.begin();", pico
                ? "Wire.setSDA(4);Wire.setSCL(5);Wire.begin();Wire.setClock(100000);"
                : "Wire.begin(8,9,100000);")
                .Replace("Serial.println(\"READY\");", """
                    Wire.beginTransmission(0x26);
                    Serial.print("EMPTY=");Serial.println(Wire.endTransmission());
                    send(0x80);pins(0xFB);pins(0xFF,true);
                    Serial.print("READ=");Serial.print(readPins(),HEX);
                    pins(0xFB);pins(0xFF,true);Serial.print(",");Serial.println(readPins(),HEX);pins(0xFB);
                    Serial.println("READY");
                    """)
        };
        if (pico) project.Code = project.Code.Replace("Serial.", "Serial1.");
        project.Parts.Add(new("board", pico ? PartKind.Pico : PartKind.Esp32S3, 60, 80,
            ModelId: pico ? PicoBoard.ModelId : Esp32S3Board.ModelId));
        project.Parts.Add(new("adapter", PartKind.I2cLevelShifter, 365, 145, ModelId: I2cLevelShifter.ModelId));
        project.Parts.Add(new("lcd", PartKind.LcdI2c, 620, 140, 39, ModelId: pico ? "lcd-i2c-20x4" : "lcd-i2c-16x2"));
        var ground = pico ? PicoBoard.Ground : Esp32S3Board.Ground;
        var high = pico ? PicoBoard.Vbus : Esp32S3Board.V5;
        project.Wires.AddRange([
            new("lv", new("adapter", "LV"), new("board", pico ? PicoBoard.V3 : Esp32S3Board.V3)),
            new("hv", new("adapter", "HV"), new("board", high)),
            new("ag", new("adapter", "GND"), new("board", ground)),
            new("lg", new("lcd", "GND"), new("board", ground)),
            new("lp", new("lcd", "VCC"), new("board", high)),
            new("lsda", new("adapter", "LV1"), new("board", pico ? PicoBoard.Gpio(4)! : Esp32S3Board.Gpio(8)!)),
            new("lscl", new("adapter", "LV2"), new("board", pico ? PicoBoard.Gpio(5)! : Esp32S3Board.Gpio(9)!)),
            new("hsda", new("adapter", "HV1"), new("lcd", "SDA")),
            new("hscl", new("adapter", "HV2"), new("lcd", "SCL"))
        ]);
        return project;
    }

    [Fact]
    public void RailsStaySeparateAndEitherSideCanPullOnlyItsOwnChannelLow()
    {
        var project = Project(pico: false);
        var snapshot = project.Serialize();
        Assert.Equal((project.Name, 3, 9), ElectronicsProjectFormat.Inspect(snapshot));
        Assert.Equal(snapshot, CircuitProject.Parse(snapshot).Serialize());
        var board = project.Parts[0];
        var idle = CircuitSolver.Solve(project);
        var lcd = Assert.Single(Esp32S3Board.I2cDevices(project, board, idle));
        Assert.True(lcd.Powered && lcd.Connected);
        Assert.Equal((8, 9), (lcd.Sda, lcd.Scl));
        Assert.Equal(3.3, idle.Voltages[new("adapter", "LV1")], 4);
        Assert.InRange(idle.Voltages[new("adapter", "HV1")], 4.99, 5.0);
        Assert.False(new CircuitConnections(project).Same(new("adapter", "LV1"), new("adapter", "HV1")));

        var pulls = Enumerable.Repeat(new PinDrive(false, 0), Esp32S3Board.GpioCount).ToArray();
        pulls[8] = new(true, 0);
        var low = CircuitSolver.Solve(project, pulls);
        Assert.InRange(low.Voltages[new("lcd", "SDA")], 0, 0.2);
        Assert.InRange(low.Voltages[new("lcd", "SCL")], 4.99, 5.0);

        project.Wires.Add(new("sink", new("lcd", "SDA"), new("board", Esp32S3Board.Ground)));
        var high = CircuitSolver.Solve(project);
        Assert.InRange(high.Voltages[new("adapter", "LV1")], 0, 0.2);
        Assert.Equal(3.3, high.Voltages[new("adapter", "LV2")], 4);
        Assert.False(Assert.Single(Esp32S3Board.I2cDevices(project, board, high)).Connected);
    }

    [Theory]
    [InlineData("ag")]
    [InlineData("lv")]
    [InlineData("hv")]
    [InlineData("lscl")]
    [InlineData("hscl")]
    public void IncompleteTranslatorCannotReachAPoweredDisplay(string wire)
    {
        foreach (var pico in new[] { false, true })
        {
            var project = Project(pico);
            project.Wires.RemoveAll(w => w.Id == wire);
            var result = CircuitSolver.Solve(project);
            var state = pico
                ? CircuitPico.Devices(project, result).Select(d => (d.Powered, d.Connected))
                : Esp32S3Board.I2cDevices(project, project.Parts[0], result).Select(d => (d.Powered, d.Connected));
            Assert.Equal((true, false), Assert.Single(state));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectFiveVoltPullUpIsRejected(bool pico)
    {
        var project = Project(pico);
        var board = project.Parts[0];
        var data = pico ? PicoBoard.Gpio(4)! : Esp32S3Board.Gpio(8)!;
        project.Wires.RemoveAll(w => w.Id is "lsda" or "hsda");
        project.Wires.Add(new("direct", new("lcd", "SDA"), new("board", data)));
        var result = CircuitSolver.Solve(project);
        if (pico)
        {
            var devices = CircuitPico.Devices(project, result);
            Assert.False(Assert.Single(devices).Connected);
            Assert.Throws<InvalidDataException>(() => CircuitPico.Inputs(project, result, devices));
        }
        else
        {
            Assert.False(Assert.Single(Esp32S3Board.I2cDevices(project, board, result)).Connected);
            Assert.Throws<InvalidDataException>(() => Esp32S3Board.Inputs(board, result, [8], null));
        }
    }

    [Fact]
    public async Task PicoArduinoReadsAndWritesTheTwentyColumnDisplayThroughTheTranslator()
    {
        var project = Project(pico: true);
        var devices = CircuitPico.Devices(project);
        var device = Assert.Single(devices);
        Assert.True(device.Powered && device.Connected);
        Assert.Equal((4, 5), (device.Sda, device.Scl));
        var image = await PicoCompiler.Compile(project.Code, CancellationToken.None);
        using var runner = new PicoFirmwareRunner();
        await runner.Start(image);
        var serial = "";
        PicoFrame? frame = null;
        for (var i = 0; i < 80 && !serial.Contains("READY"); i++)
        {
            var circuit = CircuitSolver.Solve(project, frame?.Pins);
            devices = CircuitPico.Devices(project, circuit);
            var inputs = CircuitPico.Inputs(project, circuit, devices);
            frame = await runner.Step(analog: inputs.Analog, devices: devices, gpioInputs: inputs.Digital);
            serial += frame.SerialPorts[1];
        }
        Assert.Contains("EMPTY=2", serial);
        Assert.Contains("READ=4F,8F", serial);
        Assert.Contains("READY", serial);
        var display = Assert.Single(frame!.Displays);
        Assert.True(display.Display && display.Backlight);
        Assert.Equal((20, 4), (display.Columns, display.Rows));
        Assert.Equal("Hello Sql", new string(display.Cells.Take(9).Select(value => (char)value).ToArray()));
        Assert.Equal('3', (char)display.Cells[40]);
        Assert.Equal('4', (char)display.Cells[60]);
    }
}
