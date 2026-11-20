namespace Classroom.Desktop.Electronics;

internal static class I2cLevelShifter
{
    public const string ModelId = "i2c-level-shifter";
    public const string Name = "Преобразователь уровней I2C · 3,3 / 5 В";
    public const string Wiring = "LV — 3,3 В, HV — 5 В, GND — общая земля. GPIO к LV1/LV2, SDA/SCL дисплея к соответствующим HV1/HV2.";
    public static readonly string[] Pins = ["LV", "GND", "LV1", "LV2", "HV", "HV1", "HV2"];
    public static readonly (string Low, string High)[] Channels = [("LV1", "HV1"), ("LV2", "HV2")];

    public static HashSet<Terminal> SupplyNets(CircuitProject project, CircuitConnections nets) =>
        project.Parts.SelectMany(part => PowerPins(part.Kind).Select(pin => nets.Root(new(part.Id, pin))))
            .ToHashSet();

    static string[] PowerPins(PartKind kind) => kind switch
    {
        PartKind.Esp32S3 => [Esp32S3Board.V3, Esp32S3Board.V5],
        PartKind.Pico => [PicoBoard.V3, PicoBoard.Vbus, PicoBoard.Vsys],
        PartKind.Mega => ["3V3", "5V", "IOREF"],
        PartKind.Uno => ["5V"],
        PartKind.Battery or PartKind.AnalogSensor => ["+", "−"],
        _ => []
    };

    public static bool IsPowered(CircuitPart part, CircuitConnections nets, Terminal ground,
        IReadOnlySet<Terminal> supplies, Func<Terminal, double> voltage)
    {
        var gnd = new Terminal(part.Id, "GND");
        var low = nets.Root(new(part.Id, "LV"));
        var high = nets.Root(new(part.Id, "HV"));
        // Pull-ups from other devices can make an open supply pin appear
        // energized in the DC solve. Require both power wires explicitly.
        return nets.Same(gnd, ground) && supplies.Contains(low) && supplies.Contains(high) &&
            voltage(low) - voltage(gnd) is >= 3.0 and <= 3.6 &&
            voltage(high) - voltage(gnd) is >= 4.5 and <= 5.5;
    }

    public static (int Sda, int Scl) LcdPins(
        CircuitPart lcd, CircuitConnections nets, CircuitResult result, Terminal ground,
        IEnumerable<CircuitPart> shifters, IReadOnlySet<Terminal> supplies, IReadOnlyDictionary<Terminal, int> gpioNets)
    {
        var supply = new Terminal(lcd.Id, "VCC");
        var data = nets.Root(new(lcd.Id, "SDA"));
        var clock = nets.Root(new(lcd.Id, "SCL"));
        double Voltage(Terminal pin) => result.Voltages[pin];
        bool HighSignal(Terminal pin) => !nets.Same(pin, ground) && !nets.Same(pin, supply) &&
            !supplies.Contains(pin) && !gpioNets.ContainsKey(pin) &&
            Voltage(pin) >= Voltage(supply) * 0.7 && Voltage(pin) <= 5.5;

        if (data == clock || !HighSignal(data) || !HighSignal(clock)) return (-1, -1);

        (int Sda, int Scl) pins = (-1, -1);
        foreach (var shifter in shifters)
        {
            if (!IsPowered(shifter, nets, ground, supplies, Voltage)) continue;
            int Gpio(Terminal highNet)
            {
                var channel = Channels.Where(pair => nets.Same(highNet, new(shifter.Id, pair.High))).ToArray();
                if (channel.Length != 1) return -1;
                var low = nets.Root(new(shifter.Id, channel[0].Low));
                return !supplies.Contains(low) && Voltage(low) is >= 2.475 and <= 3.6
                    ? gpioNets.GetValueOrDefault(low, -1) : -1;
            }
            var sda = Gpio(data);
            var scl = Gpio(clock);
            if (sda < 0 || scl < 0 || sda == scl) continue;
            if (pins.Sda >= 0) return (-1, -1);
            pins = (sda, scl);
        }
        return pins;
    }

    public static Dictionary<Terminal, int> GpioNets(
        CircuitConnections nets, CircuitPart board, int count, Func<int, string?> pinName) =>
        Enumerable.Range(0, count).Where(i => pinName(i) is not null)
            .GroupBy(i => nets.Root(new(board.Id, pinName(i)!)))
            .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.First() : -1);
}
