namespace Classroom.Desktop.Electronics;

public static class Esp32S3Board
{
    public const string ModelId = "esp32s3-devkitc-1-gpio";
    public const string Name = "ESP32-S3-DevKitC-1";
    public const string Ground = "J1.22:GND", V3 = "J1.1:3V3", V5 = "J1.21:5V", Reset = "J1.3:RST";
    public const int GpioCount = 49, HeaderLength = 22;
    public const double LogicVoltage = 3.3;

    // DevKitC-1 v1.1, headers J1 and J3 in their physical top-to-bottom order.
    public static readonly string[] Pins =
    [
        V3, "J1.2:3V3", Reset, "J1.4:GPIO4", "J1.5:GPIO5", "J1.6:GPIO6", "J1.7:GPIO7", "J1.8:GPIO15",
        "J1.9:GPIO16", "J1.10:GPIO17", "J1.11:GPIO18", "J1.12:GPIO8", "J1.13:GPIO3", "J1.14:GPIO46",
        "J1.15:GPIO9", "J1.16:GPIO10", "J1.17:GPIO11", "J1.18:GPIO12", "J1.19:GPIO13", "J1.20:GPIO14", V5, Ground,
        "J3.1:GND", "J3.2:GPIO43", "J3.3:GPIO44", "J3.4:GPIO1", "J3.5:GPIO2", "J3.6:GPIO42", "J3.7:GPIO41",
        "J3.8:GPIO40", "J3.9:GPIO39", "J3.10:GPIO38", "J3.11:GPIO37", "J3.12:GPIO36", "J3.13:GPIO35", "J3.14:GPIO0",
        "J3.15:GPIO45", "J3.16:GPIO48", "J3.17:GPIO47", "J3.18:GPIO21", "J3.19:GPIO20", "J3.20:GPIO19", "J3.21:GND", "J3.22:GND"
    ];

    public static readonly (string Root, string Contact)[] Aliases =
        [(V3, "J1.2:3V3"), (Ground, "J3.1:GND"), (Ground, "J3.21:GND"), (Ground, "J3.22:GND")];
    static readonly string?[] gpio = Enumerable.Range(0, GpioCount).Select(i =>
        Pins.FirstOrDefault(pin => pin.EndsWith(":GPIO" + i, StringComparison.Ordinal))).ToArray();

    public static string? Gpio(int number) => (uint)number < GpioCount ? gpio[number] : null;

    internal static string? ConfiguredRuntime
    {
        get
        {
            if (Environment.GetEnvironmentVariable("CLASSROOM_ESP32S3_GPIO_RUNTIME") is not { Length: > 0 } path)
                return null;
            return File.Exists(Esp32S3Compiler.Emulator(path)) ? path : null;
        }
    }

    internal static void ValidateRun(CircuitProject project, CircuitPart board)
    {
        var unsupported = project.Parts.FirstOrDefault(p => p.Kind is PartKind.LcdI2c or PartKind.OledI2c or
            PartKind.TftSpi or PartKind.Dht11 or PartKind.Hcsr04 or PartKind.Ds18b20 or PartKind.Servo);
        if (unsupported is not null)
            throw new InvalidDataException($"«{PartLibrary.Name(unsupported)}»: для этой детали требуется периферия, которая пока недоступна в цифровом профиле ESP32-S3.");
        if (project.Wires.Any(w => w.From == new Terminal(board.Id, Reset) || w.To == new Terminal(board.Id, Reset)))
            throw new InvalidDataException("Вход RST требует отдельной модели сброса. Для перезапуска остановите и заново запустите симуляцию.");
        var nets = new CircuitConnections(project);
        if (project.Parts.Where(p => p.Kind is PartKind.Battery or PartKind.AnalogSensor)
            .Any(p => new[] { V3, V5 }.Any(rail => nets.Same(new(p.Id, "+"), new(board.Id, rail)))))
            throw new InvalidDataException("В цифровом профиле ESP32-S3 работает от виртуального USB. Уберите внешний источник с вывода питания платы.");
    }

    internal static Dictionary<int, bool?> Inputs(CircuitPart board, CircuitResult result, int[] wired, PinDrive[]? drives)
    {
        var inputs = new Dictionary<int, bool?>(wired.Length);
        foreach (var index in wired)
        {
            var pin = new Terminal(board.Id, Gpio(index)!);
            var voltage = result.Voltages[pin];
            if (voltage is < -0.3 or > 3.6 || !double.IsFinite(voltage))
                throw new InvalidDataException($"{Name}, GPIO{index}: {voltage:F2} В. Проверьте согласование уровней 3,3 В.");
            inputs.Add(index, drives?[index].Output == true || !result.ReferencedTerminals.Contains(pin)
                ? null : voltage <= 0.25 * LogicVoltage ? false : voltage >= 0.75 * LogicVoltage ? true : null);
        }
        return inputs;
    }

    internal static PinDrive[] Drives(IReadOnlyDictionary<int, Esp32S3PadState> frame)
    {
        var drives = Enumerable.Repeat(new PinDrive(false, 0), GpioCount).ToArray();
        foreach (var (pin, state) in frame)
            drives[pin] = new(state.Driven, state.OutputHigh ? LogicVoltage : 0, state.PullUp, PullDown: state.PullDown);
        return drives;
    }
}
