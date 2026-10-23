using System.Text.Json;
using System.Text.Json.Serialization;

namespace Classroom.Desktop.Electronics;

public enum PartKind { Uno, Resistor, Led, Switch, Battery, Ground, Potentiometer, AnalogSensor, Breadboard, Diode, Motor, Buzzer, Servo, Pot3, Visual }
public record Terminal(string Part, string Pin);
public record WireLink(string Id, Terminal From, Terminal To, string Color = "#26966C");
public sealed record CircuitPart(string Id, PartKind Kind, double X, double Y, double Value = 0, bool Active = false, int Rotation = 0, string? ModelId = null, double Setting = 0.5, string[]? CustomPins = null, double Scale = 1, int QuarterTurns = 0, double Angle = 0, double StretchX = 1, double StretchY = 1);
public sealed record CircuitProject
{
    public int Version { get; init; } = 1;
    public string Name { get; set; } = "Новая схема";
    public List<CircuitPart> Parts { get; init; } = [];
    public List<WireLink> Wires { get; init; } = [];
    public List<string> Favorites { get; init; } = [];
    public string Code { get; set; } = "void setup() {\n\n}\n\nvoid loop() {\n\n}\n";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };
    public string Serialize() => JsonSerializer.Serialize(this, Json);
    public static CircuitProject Parse(string text)
    {
        if (text.Length > 2_000_000) throw new InvalidDataException("Схема слишком большая (не более 2 МБ).");
        var project = JsonSerializer.Deserialize<CircuitProject>(text, Json) ?? throw new InvalidDataException("Пустая схема.");
        project.Validate(); return project;
    }
    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("Версия схемы не поддерживается. Исходный файл сохранён без изменений.");
        if (Parts.Count > 80 || Wires.Count > 200 || Code.Length > 100_000 || Name.Length > 120 || Favorites.Count > 256 || Favorites.Any(f => f.Length > 100))
            throw new InvalidDataException("Превышен размер проекта: 80 компонентов, 200 проводов, 100 000 символов кода.");
        if (Parts.Any(p => string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || !Enum.IsDefined(p.Kind) || !double.IsFinite(p.Value) || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 4000 || p.Y < 0 || p.Y > 3000 || p.Rotation is < 0 or > 3) || Parts.Select(p => p.Id).Distinct().Count() != Parts.Count)
            throw new InvalidDataException("Некорректный компонент или координаты.");
        foreach (var p in Parts)
        {
            if (!double.IsFinite(p.Scale) || p.Scale is < 0.4 or > 2 || p.QuarterTurns is < 0 or > 3)
                throw new InvalidDataException("Размер детали: 40–200%, поворот: 0, 90, 180 или 270 градусов.");
            if (!double.IsFinite(p.Angle) || p.Angle is < 0 or >= 360 || !double.IsFinite(p.StretchX) || p.StretchX is < 0.1 or > 10 || !double.IsFinite(p.StretchY) || p.StretchY is < 0.1 or > 10)
                throw new InvalidDataException("Некорректный угол или растяжение детали.");
            if (p.ModelId is not null && (PartCatalog.Find(p.ModelId) is not { } definition || definition.Kind != p.Kind)) throw new InvalidDataException("Неизвестная или несовместимая модель компонента. Исходник не изменён.");
            if (p.Kind == PartKind.Visual && p.ModelId is null) throw new InvalidDataException("У детали отсутствует идентификатор модели.");
            if (!double.IsFinite(p.Setting) || p.Setting is < 0 or > 1) throw new InvalidDataException("Положение регулятора должно быть от 0 до 1.");
            if (p.CustomPins is not null && (p.Kind != PartKind.Visual || p.CustomPins.Length > 64 || p.CustomPins.Distinct().Count() != p.CustomPins.Length || p.CustomPins.Any(pin => string.IsNullOrWhiteSpace(pin) || pin.Length > 20))) throw new InvalidDataException("Некорректный набор пользовательских выводов.");
            if (p.Kind is PartKind.Resistor or PartKind.Potentiometer or PartKind.Pot3 or PartKind.Motor or PartKind.Buzzer && p.Value is < 1 or > 1_000_000 ||
                p.Kind is PartKind.Battery or PartKind.AnalogSensor && p.Value is < 0 or > 24 ||
                p.Kind == PartKind.Led && p.Value is < 1 or > 4 || p.Kind == PartKind.Diode && p.Value is < 0.1 or > 2)
                throw new InvalidDataException("Параметр компонента вне допустимого диапазона.");
        }
        if (Wires.Any(w => !Valid(w.From) || !Valid(w.To) || w.From == w.To || !System.Text.RegularExpressions.Regex.IsMatch(w.Color, "^#[0-9A-Fa-f]{6}$")) || Wires.Select(w => w.Id).Distinct().Count() != Wires.Count)
            throw new InvalidDataException("Провод ссылается на отсутствующий вывод.");
        if (Parts.Count(p => p.Kind == PartKind.Uno) > 1) throw new InvalidDataException("В этой версии поддерживается одна Arduino Uno на схему.");
        if (Parts.Count(p => p.Kind == PartKind.Breadboard) > 2) throw new InvalidDataException("На схеме допускается до двух макетных плат.");
        bool Valid(Terminal pin) => Parts.Any(p => p.Id == pin.Part && PartLibrary.Pins(p).Contains(pin.Pin));
    }
    public static CircuitProject Blink()
    {
        var p = new CircuitProject { Name = "Arduino Uno · мигание светодиода", Code = "void setup() {\n  pinMode(13, OUTPUT);\n}\n\nvoid loop() {\n  digitalWrite(13, HIGH);\n  delay(500);\n  digitalWrite(13, LOW);\n  delay(500);\n}\n" };
        p.Parts.AddRange([new("uno", PartKind.Uno, 80, 80), new("r", PartKind.Resistor, 350, 100, 330), new("led", PartKind.Led, 600, 100, 2)]);
        p.Wires.AddRange([new("w1", new("uno", "D13"), new("r", "1")), new("w2", new("r", "2"), new("led", "A")), new("w3", new("led", "K"), new("uno", "GND"))]);
        return p;
    }
}

public static class PartLibrary
{
    public static string Name(CircuitPart part) => PartCatalog.For(part).Name;
    public static string[] Pins(CircuitPart part) => part.CustomPins is { Length: > 0 } custom ? custom : (part.Kind == PartKind.Visual ? PartCatalog.For(part).Pins ?? [] : Pins(part.Kind));
    public static string Name(PartKind kind) => kind switch
    {
        PartKind.Uno => "Arduino Uno", PartKind.Resistor => "Резистор", PartKind.Led => "Светодиод",
        PartKind.Switch => "Кнопка", PartKind.Battery => "Источник питания", PartKind.Ground => "Земля",
        PartKind.Potentiometer => "Переменный резистор", PartKind.AnalogSensor => "Аналоговый датчик",
        PartKind.Breadboard => "Макетная плата", PartKind.Diode => "Диод", PartKind.Motor => "Двигатель", PartKind.Buzzer => "Пьезоизлучатель", PartKind.Servo => "Сервопривод", PartKind.Pot3 => "Потенциометр", _ => "Деталь для схемы"
    };
    public static string[] Pins(PartKind kind) => kind switch
    {
        PartKind.Uno => Enumerable.Range(0, 14).Select(i => "D" + i).Concat(Enumerable.Range(0, 6).Select(i => "A" + i)).Concat(["5V", "GND"]).ToArray(),
        PartKind.Led => ["A", "K"], PartKind.Ground => ["GND"],
        PartKind.Diode => ["A", "K"], PartKind.Motor or PartKind.Buzzer => ["+", "−"],
        PartKind.Servo => ["VCC", "GND", "SIG"], PartKind.Pot3 => ["1", "W", "3"],
        PartKind.Breadboard => Enumerable.Range(1,30).SelectMany(row => "abcdefghij".Select(col => col + row.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Concat(new[] {"L+","L−","R+","R−"}.SelectMany(rail => Enumerable.Range(1,25).Select(row => rail + row.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray(),
        PartKind.Visual => [],
        PartKind.Battery or PartKind.AnalogSensor => ["+", "−"], _ => ["1", "2"]
    };
    public static double Default(PartKind kind) => kind switch { PartKind.Resistor => 330, PartKind.Potentiometer or PartKind.Pot3 => 10000, PartKind.Led => 2, PartKind.Diode => 0.7, PartKind.Motor => 50, PartKind.Buzzer => 200, PartKind.Battery => 5, PartKind.AnalogSensor => 2.5, _ => 0 };
}

public record PinDrive(bool Output, double Voltage, bool PullUp = false, double? PulseUs = null, double? Frequency = null);
public record PartReading(double Voltage, double Current, bool Lit, double? Indicator = null);
public record CircuitResult(Dictionary<Terminal, double> Voltages, Dictionary<string, PartReading> Parts, string[] Warnings);

// Quasi-static nodal solver. Wires form equipotential nodes; an LED has a
// piecewise-linear forward characteristic. No fake firmware interpretation here.
public static class CircuitSolver
{
    public static CircuitResult Solve(CircuitProject project, PinDrive[]? digital = null)
    {
        project.Validate();
        var pins = project.Parts.SelectMany(p => PartLibrary.Pins(p).Select(pin => new Terminal(p.Id, pin))).ToArray();
        var parents = pins.ToDictionary(p => p, p => p);
        Terminal Root(Terminal p) { while (parents[p] != p) p = parents[p]; return p; }
        void Join(Terminal a, Terminal b) { parents[Root(a)] = Root(b); }
        foreach (var wire in project.Wires) Join(wire.From, wire.To);
        foreach (var breadboard in project.Parts.Where(p => p.Kind == PartKind.Breadboard))
        {
            for (var row = 1; row <= 30; row++)
            {
                foreach (var group in new[] { "abcde", "fghij" })
                    foreach (var c in group.Skip(1)) Join(new(breadboard.Id, group[0] + row.ToString(System.Globalization.CultureInfo.InvariantCulture)), new(breadboard.Id, c + row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            foreach (var rail in new[] {"L+","L−","R+","R−"})
                for (var row = 2; row <= 25; row++) Join(new(breadboard.Id, rail + "1"), new(breadboard.Id, rail + row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        var grounds = project.Parts.Where(p => p.Kind is PartKind.Ground or PartKind.Uno).Select(p => new Terminal(p.Id, "GND")).ToArray();
        if (grounds.Length == 0 && project.Parts.FirstOrDefault(p => p.Kind is PartKind.Battery or PartKind.AnalogSensor) is { } battery)
            grounds = [new(battery.Id, "−")];
        foreach (var ground in grounds.Skip(1)) Join(grounds[0], ground);
        var reference = grounds.Length > 0 ? Root(grounds[0]) : pins.FirstOrDefault();
        if (reference is not null) reference = Root(reference);
        var nodes = pins.Select(Root).Distinct().Where(p => p != reference).Select((p, i) => (p, i)).ToDictionary(v => v.p, v => v.i);
        int Index(Terminal p) => nodes.GetValueOrDefault(Root(p), -1);
        double V(Terminal p, double[] values) => Index(p) < 0 ? 0 : values[Index(p)];
        var branches = new List<(CircuitPart Part, Terminal A, Terminal B, double G, double Source, bool Diode)>();
        foreach (var part in project.Parts)
        {
            Terminal T(string pin) => new(part.Id, pin);
            switch (part.Kind)
            {
                case PartKind.Resistor: case PartKind.Potentiometer: branches.Add((part, T("1"), T("2"), 1 / part.Value, 0, false)); break;
                case PartKind.Pot3:
                    branches.Add((part, T("1"), T("W"), 1 / Math.Max(1, part.Value * part.Setting), 0, false));
                    branches.Add((part, T("W"), T("3"), 1 / Math.Max(1, part.Value * (1 - part.Setting)), 0, false)); break;
                case PartKind.Motor: case PartKind.Buzzer: branches.Add((part, T("+"), T("−"), 1 / part.Value, 0, false)); break;
                case PartKind.Servo: branches.Add((part, T("VCC"), T("GND"), 1.0 / 200, 0, false)); break;
                case PartKind.Switch: branches.Add((part, T("1"), T("2"), part.Active ? 100 : 1e-10, 0, false)); break;
                case PartKind.Led: case PartKind.Diode: branches.Add((part, T("A"), T("K"), 0, part.Value, true)); break;
                case PartKind.Battery: case PartKind.AnalogSensor: branches.Add((part, T("+"), T("−"), 10, part.Value, false)); break;
                case PartKind.Uno:
                    branches.Add((part, T("5V"), T("GND"), 10, 5, false));
                    for (var i = 0; i < 20; i++)
                        if (digital is not null && i < digital.Length && (digital[i].Output || digital[i].PullUp))
                            branches.Add((part, T(i < 14 ? "D" + i : "A" + (i - 14)), T("GND"), digital[i].Output ? 1.0 / 25 : 1.0 / 50000, digital[i].PullUp ? 5 : Math.Clamp(digital[i].Voltage, 0, 5), false));
                    break;
            }
        }
        var volts = new double[nodes.Count];
        var nonlinear = branches.Any(b => b.Diode);
        for (var iteration = 0; iteration < 80 && nodes.Count > 0; iteration++)
        {
            var matrix = new double[nodes.Count, nodes.Count + 1];
            for (var i = 0; i < nodes.Count; i++) matrix[i, i] = 1e-10;
            foreach (var b in branches)
            {
                var g = b.Diode ? (V(b.A, volts) - V(b.B, volts) > b.Source ? 0.1 : 1e-10) : b.G;
                var source = b.Diode && g < 0.01 ? 0 : b.Source;
                var a = Index(b.A); var z = Index(b.B);
                if (a >= 0) { matrix[a, a] += g; matrix[a, nodes.Count] += source * g; }
                if (z >= 0) { matrix[z, z] += g; matrix[z, nodes.Count] -= source * g; }
                if (a >= 0 && z >= 0) { matrix[a, z] -= g; matrix[z, a] -= g; }
            }
            var next = Gaussian(matrix, nodes.Count);
            if (!nonlinear) { volts = next; break; }
            var delta = 0.0;
            for (var i = 0; i < volts.Length; i++) { delta = Math.Max(delta, Math.Abs(next[i] - volts[i])); volts[i] = (volts[i] + next[i]) / 2; }
            if (delta < 1e-6) break;
        }
        var readings = new Dictionary<string, PartReading>();
        var warnings = new HashSet<string>();
        foreach (var b in branches)
        {
            var voltage = V(b.A, volts) - V(b.B, volts);
            var current = b.Diode ? Math.Max(0, (voltage - b.Source) / 10) : (voltage - b.Source) * b.G;
            if (b.Part.Kind != PartKind.Uno) readings[b.Part.Id] = new(voltage, current, b.Part.Kind == PartKind.Led && current > 0.0001,
                b.Part.Kind == PartKind.Motor ? Math.Clamp(Math.Abs(voltage) / 5, 0, 1) * 100 : null);
            if (b.Part.Kind == PartKind.Led && current > 0.03) warnings.Add("Ток светодиода выше 30 мА. Добавьте или увеличьте резистор.");
            if (Math.Abs(current) > 0.1 && b.Part.Kind is PartKind.Uno or PartKind.Battery) warnings.Add("Перегрузка источника или короткое замыкание. Проверьте проводку.");
        }
        foreach (var servo in project.Parts.Where(p => p.Kind == PartKind.Servo))
        {
            var signal = Root(new(servo.Id, "SIG"));
            double? pulse = null;
            foreach (var uno in project.Parts.Where(p => p.Kind == PartKind.Uno))
                for (var i = 0; digital is not null && i < Math.Min(20, digital.Length); i++)
                    if (Root(new(uno.Id, i < 14 ? "D" + i : "A" + (i - 14))) == signal) pulse = digital[i].PulseUs;
            if (readings.TryGetValue(servo.Id, out var reading) && reading.Voltage > 3.5 && pulse is >= 500 and <= 2500)
                readings[servo.Id] = reading with { Indicator = Math.Clamp((pulse.Value - 500) * 180 / 2000, 0, 180) };
        }
        if (grounds.Length == 0 && project.Parts.Count > 0) warnings.Add("Нет опорной земли. Добавьте источник питания, Arduino или землю.");
        return new(pins.ToDictionary(p => p, p => V(p, volts)), readings, warnings.ToArray());
    }

    static double[] Gaussian(double[,] m, int n)
    {
        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c])) pivot = r;
            if (Math.Abs(m[pivot, c]) < 1e-15) throw new InvalidDataException("Схема не имеет однозначного решения. Проверьте соединения и землю.");
            if (pivot != c) for (var k = c; k <= n; k++) (m[c, k], m[pivot, k]) = (m[pivot, k], m[c, k]);
            var divisor = m[c, c];
            for (var k = c; k <= n; k++) m[c, k] /= divisor;
            for (var r = 0; r < n; r++)
            {
                if (r == c) continue;
                var factor = m[r, c];
                if (factor == 0) continue;
                for (var k = c; k <= n; k++) m[r, k] -= factor * m[c, k];
            }
        }
        return Enumerable.Range(0, n).Select(i => m[i, n]).ToArray();
    }
}
