using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed record I2cDevice(string Id,int Address,int Columns,int Rows,bool Connected,bool Powered,string Kind="lcd",ElectronicsMotion? Motion=null,
    long? RtcStart=null,bool Backup=false);
public sealed record OledFrame(string Id,int Width,int Height,bool Powered,bool Display,int Contrast,string Pixels,string Memory);
public sealed record LcdFrame(string Id,int Columns,int Rows,int[] Cells,int[] Cgram,bool Powered,bool Display,bool Backlight,int Cursor,bool Blink,bool Underline);
public static class CircuitI2c
{
    public static bool IsDisplay(CircuitPart part) => part.Kind is PartKind.LcdI2c or PartKind.OledI2c;
    public static bool IsDevice(CircuitPart part) => IsDisplay(part) || part.Kind is PartKind.Mpu6050 or PartKind.Ds1307;
    internal static bool HasFiveVoltPullups(CircuitPart part) => part.Kind is PartKind.LcdI2c or PartKind.Ds1307;
    public static string PowerPin(CircuitPart part) => HasFiveVoltPullups(part) ? "VCC" : "VDD";
    public static (int Columns, int Rows) Size(CircuitPart part) => part.Kind switch
    {
        PartKind.OledI2c => (128, 64),
        PartKind.Mpu6050 or PartKind.Ds1307 => (0, 0),
        _ => part.ModelId == "lcd-i2c-20x4" ? (20, 4) : (16, 2)
    };

    internal static void ValidateRun(CircuitProject project)
    {
        var clocks = project.Parts.Where(p => p.Kind == PartKind.Ds1307).Select(p => p.Id).ToHashSet();
        if (project.Wires.Any(w => IsSquareWave(w.From) || IsSquareWave(w.To)))
            throw new InvalidDataException("Выход SQW DS1307 пока доступен только на схеме. Для симуляции часов отсоедините SQW.");
        bool IsSquareWave(Terminal terminal) => terminal.Pin == "SQW" && clocks.Contains(terminal.Part);
    }

    internal static I2cDevice Describe(CircuitPart part, CircuitConnections nets, CircuitResult result, Terminal ground, bool connected)
    {
        var returnPin = new Terminal(part.Id, "GND");
        var voltage = result.Voltages[new(part.Id, PowerPin(part))] - result.Voltages[returnPin];
        var (columns, rows) = Size(part);
        var commonGround = nets.Same(returnPin, ground);
        if (part.Kind == PartKind.Mpu6050)
        {
            var address = CircuitMpu6050.Address(part, result);
            return new(part.Id, address < 0 ? 0x68 : address, columns, rows, connected && address >= 0,
                commonGround && voltage is >= 3.0 and <= 3.45, "mpu6050", CircuitMpu6050.Motion(part));
        }
        if (part.Kind == PartKind.Ds1307)
            return new(part.Id, 0x68, columns, rows, connected, commonGround && voltage is >= 4.5 and <= 5.5,
                "ds1307", RtcStart: part.RtcStart ?? 0, Backup: part.Active);
        var oled = part.Kind == PartKind.OledI2c;
        return new(part.Id, (int)part.Value, columns, rows, connected,
            commonGround && voltage >= (oled ? 3.0 : 4.5) && voltage <= 5.5, oled ? "ssd1306" : "lcd");
    }

    public static I2cDevice[] Devices(CircuitProject project, CircuitResult? result = null)
    {
        if (!project.Parts.Any(IsDevice)) return [];
        var controller = project.Parts.FirstOrDefault(p => AvrBoards.For(p) is not null);
        if (controller is null) return [];
        var nets = new CircuitConnections(project);
        result ??= CircuitSolver.Solve(project);
        var ground = new Terminal(controller.Id, "GND");
        var sda = nets.Root(new(controller.Id, controller.Kind == PartKind.Mega ? "D20" : "A4"));
        var scl = nets.Root(new(controller.Id, controller.Kind == PartKind.Mega ? "D21" : "A5"));
        var shifters = project.Parts.Where(p => p.Kind == PartKind.I2cLevelShifter).ToArray();
        var supplies = I2cLevelShifter.SupplyNets(project, nets);
        return project.Parts.Where(IsDevice).Select(part =>
        {
            var data = new Terminal(part.Id, "SDA");
            var clock = new Terminal(part.Id, "SCL");
            var voltage = result.Voltages[new(part.Id, PowerPin(part))] - result.Voltages[new(part.Id, "GND")];
            var connected = sda != scl && nets.Root(data) == sda && nets.Root(clock) == scl &&
                result.Voltages[data] > voltage * 0.7 && result.Voltages[clock] > voltage * 0.7;
            if (part.Kind == PartKind.Mpu6050 && sda != scl)
            {
                var controllerPins = new Dictionary<Terminal, int> { [sda] = 0, [scl] = 1 };
                connected = I2cLevelShifter.DevicePins(part, nets, result, ground, shifters, supplies, controllerPins,
                    deviceOnHighSide: false) == (0, 1);
            }
            return Describe(part, nets, result, ground, connected);
        }).ToArray();
    }
}
