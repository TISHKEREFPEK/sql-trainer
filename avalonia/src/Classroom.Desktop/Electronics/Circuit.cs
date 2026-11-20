using System.Text.Json;
using System.Text.Json.Serialization;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public enum PartKind { Uno, Resistor, Led, Switch, Battery, Ground, Potentiometer, AnalogSensor, Breadboard, Diode, Motor, Buzzer, Servo, Pot3, Visual, Mega, LcdI2c, OledI2c, Pico, TftSpi, Dht11, Hcsr04, Ds18b20, Esp32S3, I2cLevelShifter, Mpu6050, Ds1307 }
public record Terminal(string Part, string Pin);
public record WireLink(string Id, Terminal From, Terminal To, string Color = "#26966C");
public sealed record CircuitPart(string Id, PartKind Kind, double X, double Y, double Value = 0, bool Active = false, int Rotation = 0, string? ModelId = null, double Setting = 0.5, string[]? CustomPins = null, double Scale = 1, int QuarterTurns = 0, double Angle = 0, double StretchX = 1, double StretchY = 1, [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int[]? DeviceMemory = null, [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ElectronicsMotion? Motion = null, [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? RtcStart = null);
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
    public string Serialize() { Validate(); return JsonSerializer.Serialize(this, Json); }
    public static CircuitProject Parse(string text)
    {
        if (text.Length > 2_000_000) throw new InvalidDataException("Схема слишком большая (не более 2 МБ).");
        var project = JsonSerializer.Deserialize<CircuitProject>(text, Json) ?? throw new InvalidDataException("Пустая схема.");
        project.Validate(); return project;
    }
    public void Validate()
    {
        try
        {
            var encoding = new System.Text.UTF8Encoding(false, true);
            foreach (var text in new[] { Code, Name }.Concat(Favorites).Concat(Parts.SelectMany(p => (p.CustomPins ?? []).Append(p.Id).Append(p.ModelId ?? "")))
                         .Concat(Wires.SelectMany(w => new[] { w.Id, w.From.Part, w.From.Pin, w.To.Part, w.To.Pin, w.Color }))) _ = encoding.GetByteCount(text);
        }
        catch (System.Text.EncoderFallbackException e) { throw new InvalidDataException("В тексте есть неполный символ. Завершите ввод или отмените изменение; новая схема не сохранена.", e); }
        if (Version != 1) throw new InvalidDataException("Версия схемы не поддерживается. Исходный файл сохранён без изменений.");
        if (Parts.Count > 80 || Wires.Count > 200 || Code.Length > 100_000 || Name.Length > 120 || Favorites.Count > 256 || Favorites.Any(f => f.Length > 100))
            throw new InvalidDataException("Превышен размер проекта: 80 компонентов, 200 проводов, 100 000 символов кода.");
        if (Parts.Any(p => string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || !Enum.IsDefined(p.Kind) || !double.IsFinite(p.Value) || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X)>Classroom.Contracts.ElectronicsProjectFormat.CoordinateLimit || Math.Abs(p.Y)>Classroom.Contracts.ElectronicsProjectFormat.CoordinateLimit || p.Rotation is < 0 or > 3) || Parts.Select(p => p.Id).Distinct().Count() != Parts.Count)
            throw new InvalidDataException("Некорректный компонент или координаты.");
        foreach (var p in Parts)
        {
            if (p.Motion is not null && (p.Kind != PartKind.Mpu6050 || !p.Motion.IsValid))
                throw new InvalidDataException("Некорректные параметры MPU-6050.");
            if (p.RtcStart is { } rtc && (p.Kind != PartKind.Ds1307 || rtc is < 0 or > ElectronicsProjectFormat.RtcStartMax))
                throw new InvalidDataException("Начальная дата DS1307 должна быть в диапазоне 2000–2099.");
            if(p.Kind==PartKind.LcdI2c&&(p.Value!=Math.Truncate(p.Value)||!(p.Value is >=32 and <=39 or >=56 and <=63)))throw new InvalidDataException("Адрес PCF8574: 0x20–0x27 или 0x38–0x3F.");
            if(p.DeviceMemory is not null&&(p.Kind!=PartKind.Ds18b20||!CircuitDs18b20.ValidMemory(p.DeviceMemory)))throw new InvalidDataException("DS18B20: повреждена память настроек.");
            if(p.Kind==PartKind.Ds18b20&&p.Value is < -55 or >125)throw new InvalidDataException("DS18B20: температура от −55 до 125 °C.");
            if(p.Kind==PartKind.Hcsr04&&p.Value is <2 or >400)throw new InvalidDataException("HC-SR04: расстояние от 2 до 400 см.");
            if(p.Kind==PartKind.Dht11&&(p.Value<0||p.Value>50||p.Value!=Math.Truncate(p.Value)))throw new InvalidDataException("DHT11: температура от 0 до 50 °C, целое число.");
            if(p.Kind==PartKind.OledI2c&&p.Value is not (60 or 61))throw new InvalidDataException("Адрес SSD1306: 0x3C или 0x3D.");
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
        if (Parts.Count(CircuitBoards.IsBoard) > 1) throw new InvalidDataException("Пока доступна одна исполняемая плата на схему; остальные платы можно сохранить в режиме схемы.");
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
        PartKind.Mega => "Arduino Mega 2560",
        PartKind.Pico => "Raspberry Pi Pico",
        PartKind.Esp32S3 => Esp32S3Board.Name,
        PartKind.LcdI2c => "LCD · I2C",
        PartKind.OledI2c => "OLED 128×64 · I2C",
        PartKind.I2cLevelShifter => I2cLevelShifter.Name,
        PartKind.Mpu6050 => CircuitMpu6050.Name,
        PartKind.Ds1307 => "DS1307 · часы реального времени",
        PartKind.Ds18b20 => "DS18B20 · температура · 1-Wire",
        PartKind.Hcsr04 => "HC-SR04 · расстояние",
        PartKind.Dht11 => "DHT11 · температура и влажность",
        PartKind.TftSpi => "TFT 240×320 · ILI9341 · SPI",
        PartKind.Switch => "Кнопка", PartKind.Battery => "Источник питания", PartKind.Ground => "Земля",
        PartKind.Potentiometer => "Переменный резистор", PartKind.AnalogSensor => "Аналоговый датчик",
        PartKind.Breadboard => "Макетная плата", PartKind.Diode => "Диод", PartKind.Motor => "Двигатель", PartKind.Buzzer => "Пьезоизлучатель", PartKind.Servo => "Сервопривод", PartKind.Pot3 => "Потенциометр", _ => "Деталь для схемы"
    };
    public static string[] Pins(PartKind kind) => kind switch
    {
        PartKind.Uno => Enumerable.Range(0, 14).Select(i => "D" + i).Concat(Enumerable.Range(0, 6).Select(i => "A" + i)).Concat(["5V", "GND"]).ToArray(),
        PartKind.Mega => AvrBoards.Get("mega2560").PinNames.Concat(["5V","3V3","GND","VIN","IOREF"]).ToArray(),
        PartKind.Pico => PicoBoard.Pins,
        PartKind.Esp32S3 => Esp32S3Board.Pins,
        PartKind.LcdI2c => ["GND","VCC","SDA","SCL"],
        PartKind.OledI2c => ["VDD","GND","SCL","SDA"],
        PartKind.I2cLevelShifter => I2cLevelShifter.Pins,
        PartKind.Mpu6050 => CircuitMpu6050.Pins,
        PartKind.Ds1307 => ["VCC", "GND", "SDA", "SCL", "SQW"],
        PartKind.Ds18b20 => ["GND","DQ","VDD"],
        PartKind.Hcsr04 => ["VCC","TRIG","ECHO","GND"],
        PartKind.Dht11 => ["VDD","DATA","NC","GND"],
        PartKind.TftSpi => ["VCC","GND","CS","RST","D/C","MOSI","SCK","LED","MISO"],
        PartKind.Led => ["A", "K"], PartKind.Ground => ["GND"],
        PartKind.Diode => ["A", "K"], PartKind.Motor or PartKind.Buzzer => ["+", "−"],
        PartKind.Servo => ["VCC", "GND", "SIG"], PartKind.Pot3 => ["1", "W", "3"],
        PartKind.Breadboard => Enumerable.Range(1,30).SelectMany(row => "abcdefghij".Select(col => col + row.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Concat(new[] {"L+","L−","R+","R−"}.SelectMany(rail => Enumerable.Range(1,25).Select(row => rail + row.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray(),
        PartKind.Visual => [],
        PartKind.Battery or PartKind.AnalogSensor => ["+", "−"], _ => ["1", "2"]
    };
    public static double Default(PartKind kind) => kind switch { PartKind.Resistor => 330, PartKind.Potentiometer or PartKind.Pot3 => 10000, PartKind.Led => 2, PartKind.Diode => 0.7, PartKind.Motor => 50, PartKind.Buzzer => 200, PartKind.Battery => 5, PartKind.AnalogSensor => 2.5, PartKind.Dht11 => 25, PartKind.Hcsr04 => 100, PartKind.Ds18b20 => 25, _ => 0 };
}

public record PinDrive(bool Output, double Voltage, bool PullUp = false, double? PulseUs = null, double? Frequency = null, bool PullDown = false);
public record PartReading(double Voltage, double Current, bool Lit, double? Indicator = null);
public record CircuitResult(Dictionary<Terminal, double> Voltages, Dictionary<string, PartReading> Parts, string[] Warnings)
{
    public IReadOnlySet<Terminal> ReferencedTerminals { get; init; } = new HashSet<Terminal>();
}

// Quasi-static nodal solver. Wires form equipotential nodes; an LED has a
// piecewise-linear forward characteristic. No fake firmware interpretation here.
public static class CircuitSolver
{
    public static CircuitResult Solve(CircuitProject project, PinDrive[]? digital = null,IReadOnlyDictionary<Terminal,double>? signals=null)
    {
        project.Validate();
        var pins = project.Parts.SelectMany(p => PartLibrary.Pins(p).Select(pin => new Terminal(p.Id, pin))).ToArray();
        var nets = new CircuitConnections(project);
        Terminal Root(Terminal pin) => nets.Root(pin);
        var grounds = project.Parts.Where(p => p.Kind==PartKind.Ground||CircuitBoards.IsBoard(p)).Select(p => new Terminal(p.Id, CircuitBoards.IsBoard(p)?CircuitBoards.Ground(p):"GND")).ToArray();
        if (grounds.Length == 0 && project.Parts.FirstOrDefault(p => p.Kind is PartKind.Battery or PartKind.AnalogSensor) is { } battery)
            grounds = [new(battery.Id, "−")];
        var reference = grounds.Length > 0 ? Root(grounds[0]) : pins.FirstOrDefault();
        if (reference is not null) reference = Root(reference);
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
                case PartKind.LcdI2c:
                    branches.Add((part,T("VCC"),T("GND"),1.0/1000,0,false));
                    branches.Add((part,T("SDA"),T("VCC"),1.0/4700,0,false));branches.Add((part,T("SCL"),T("VCC"),1.0/4700,0,false));break;
                case PartKind.Ds1307:
                    branches.Add((part, T("VCC"), T("GND"), 1.0 / 10_000, 0, false));
                    branches.Add((part, T("SDA"), T("VCC"), 1.0 / 4700, 0, false));
                    branches.Add((part, T("SCL"), T("VCC"), 1.0 / 4700, 0, false));
                    break;
                case PartKind.OledI2c:
                    branches.Add((part,T("VDD"),T("GND"),1.0/330,0,false));branches.Add((part,T("SDA"),T("VDD"),1.0/4700,0,false));branches.Add((part,T("SCL"),T("VDD"),1.0/4700,0,false));break;
                case PartKind.Mpu6050:
                    branches.Add((part, T("VDD"), T("GND"), 1.0 / 1000, 0, false));
                    branches.Add((part, T("SDA"), T("VDD"), 1.0 / 4700, 0, false));
                    branches.Add((part, T("SCL"), T("VDD"), 1.0 / 4700, 0, false));
                    branches.Add((part, T("AD0"), T("GND"), 1.0 / 100_000, 0, false));
                    break;
                case PartKind.I2cLevelShifter:
                    foreach (var (low, high) in I2cLevelShifter.Channels)
                    {
                        branches.Add((part, T(low), T("LV"), 1.0 / 10_000, 0, false));
                        branches.Add((part, T(high), T("HV"), 1.0 / 10_000, 0, false));
                    }
                    break;
                case PartKind.Hcsr04:
                    branches.Add((part,T("VCC"),T("GND"),1.0/333,0,false));
                    branches.Add((part,T("ECHO"),T("GND"),1.0/25,signals?.GetValueOrDefault(T("ECHO"))??0,false));break;
                case PartKind.Ds18b20:
                    branches.Add((part,T("VDD"),T("GND"),1.0/100000,0,false));break;
                case PartKind.Dht11:
                    branches.Add((part,T("VDD"),T("GND"),1.0/10000,0,false));break;
                case PartKind.TftSpi:
                    branches.Add((part,T("VCC"),T("GND"),1.0/1000,0,false));
                    branches.Add((part,T("LED"),T("GND"),1.0/330,0,false));break;
                case PartKind.Switch: branches.Add((part, T("1"), T("2"), part.Active ? 100 : 1e-10, 0, false)); break;
                case PartKind.Led: case PartKind.Diode: branches.Add((part, T("A"), T("K"), 0, part.Value, true)); break;
                case PartKind.Battery: case PartKind.AnalogSensor: branches.Add((part, T("+"), T("−"), 10, part.Value, false)); break;
                case PartKind.Uno: case PartKind.Mega: case PartKind.Pico: case PartKind.Esp32S3:
                    var pico=part.Kind==PartKind.Pico;
                    var s3 = part.Kind == PartKind.Esp32S3;
                    var ground=CircuitBoards.Ground(part);var rail=pico||s3?3.3:5;
                    branches.Add((part, T(pico ? PicoBoard.Vbus : s3 ? Esp32S3Board.V5 : "5V"), T(ground), 10, 5, false));
                    if (s3) branches.Add((part, T(Esp32S3Board.V3), T(ground), 10, Esp32S3Board.LogicVoltage, false));
                    if(pico){branches.Add((part,T(PicoBoard.V3),T(ground),10,3.3,false));branches.Add((part,T(PicoBoard.Vsys),T(ground),10,4.7,false));branches.Add((part,T("35:ADC_VREF"),T(PicoBoard.V3),1.0/200,0,false));branches.Add((part,T("30:RUN"),T(PicoBoard.V3),1.0/50000,0,false));branches.Add((part,T("37:3V3_EN"),T(PicoBoard.V3),1.0/100000,0,false));}
                    if(part.Kind==PartKind.Mega){branches.Add((part,T("3V3"),T("GND"),10,3.3,false));branches.Add((part,T("IOREF"),T("GND"),10,5,false));}
                    for (var i = 0; digital is not null && i < Math.Min(digital.Length, CircuitBoards.PinCount(part)); i++)
                    {
                        if (CircuitBoards.Pin(part, i) is not { } pin) continue;
                        var drive = digital[i];
                        if (drive.Output)
                            branches.Add((part, T(pin), T(ground), 1.0 / 25, Math.Clamp(drive.Voltage, 0, rail), false));
                        if (!drive.Output && drive.PullUp)
                            branches.Add((part, T(pin), T(ground), 1.0 / 50000, rail, false));
                        if (!drive.Output && drive.PullDown)
                            branches.Add((part, T(pin), T(ground), 1.0 / 50000, 0, false));
                    }
                    break;
            }
        }
        // Unconnected GPIO/visual terminals have zero potential in this model;
        // adding every floating terminal needlessly cubed the matrix cost.
        var nodes = branches.SelectMany(b=>new[]{Root(b.A),Root(b.B)}).Distinct().Where(p=>p!=reference).Select((p,i)=>(p,i)).ToDictionary(v=>v.p,v=>v.i);
        int Index(Terminal p) => nodes.GetValueOrDefault(Root(p), -1);
        double V(Terminal p, double[] values) => Index(p)<0?0:values[Index(p)];
        var nonlinear = branches.Any(b => b.Diode);
        double[] SolveNetwork()
        {
            var values = new double[nodes.Count];
            for (var iteration = 0; iteration < 80 && nodes.Count > 0; iteration++)
            {
                var matrix = new double[nodes.Count, nodes.Count + 1];
                for (var i = 0; i < nodes.Count; i++) matrix[i, i] = 1e-10;
                foreach (var b in branches)
                {
                    var g = b.Diode ? (V(b.A, values) - V(b.B, values) > b.Source ? 0.1 : 1e-10) : b.G;
                    var source = b.Diode && g < 0.01 ? 0 : b.Source;
                    var a = Index(b.A); var z = Index(b.B);
                    if (a >= 0) { matrix[a, a] += g; matrix[a, nodes.Count] += source * g; }
                    if (z >= 0) { matrix[z, z] += g; matrix[z, nodes.Count] -= source * g; }
                    if (a >= 0 && z >= 0) { matrix[a, z] -= g; matrix[z, a] -= g; }
                }
                var next = Gaussian(matrix, nodes.Count);
                if (!nonlinear) return next;
                var delta = 0.0;
                for (var i = 0; i < values.Length; i++) { delta = Math.Max(delta, Math.Abs(next[i] - values[i])); values[i] = (values[i] + next[i]) / 2; }
                if (delta < 1e-6) break;
            }
            return values;
        }
        var volts = SolveNetwork();
        var shifters = project.Parts.Where(p => p.Kind == PartKind.I2cLevelShifter).ToArray();
        if (reference is not null && shifters.Length > 0)
        {
            // Released channels have independent pull-ups. A pulled-low line
            // closes its own pass element from either side. Only open-drain use
            // is modelled here; protocol timing belongs to the I2C controller.
            var closed = new HashSet<Terminal>();
            var supplies = I2cLevelShifter.SupplyNets(project, nets);
            double Voltage(Terminal pin) => V(pin, volts);
            int previous;
            do
            {
                previous = closed.Count;
                foreach (var part in shifters)
                {
                    if (!I2cLevelShifter.IsPowered(part, nets, reference, supplies, Voltage)) continue;
                    var threshold = Voltage(new(part.Id, "LV")) * 0.3;
                    foreach (var (low, high) in I2cLevelShifter.Channels)
                    {
                        var a = new Terminal(part.Id, low);
                        var b = new Terminal(part.Id, high);
                        if (Math.Min(Voltage(a), Voltage(b)) <= threshold && closed.Add(a))
                            branches.Add((part, a, b, 1.0 / 15, 0, false));
                    }
                }
                if (closed.Count != previous) volts = SolveNetwork();
            } while (closed.Count != previous);
        }
        var readings = new Dictionary<string, PartReading>();
        var warnings = new HashSet<string>();
        foreach (var b in branches)
        {
            var voltage = V(b.A, volts) - V(b.B, volts);
            var current = b.Diode ? Math.Max(0, (voltage - b.Source) / 10) : (voltage - b.Source) * b.G;
            if (!CircuitBoards.IsBoard(b.Part)&&b.Part.Kind!=PartKind.I2cLevelShifter&&(!CircuitI2c.IsDevice(b.Part)||b.A.Pin==CircuitI2c.PowerPin(b.Part))&&(!CircuitSpi.IsDevice(b.Part)||b.A.Pin=="VCC")&&(b.Part.Kind!=PartKind.Hcsr04||b.A.Pin=="VCC")) readings[b.Part.Id] = new(voltage, current, b.Part.Kind == PartKind.Led && current > 0.0001,
                b.Part.Kind == PartKind.Motor ? Math.Clamp(Math.Abs(voltage) / 5, 0, 1) * 100 : null);
            if (b.Part.Kind == PartKind.Led && current > 0.03) warnings.Add("Ток светодиода выше 30 мА. Добавьте или увеличьте резистор.");
            if (Math.Abs(current) > 0.1 && (CircuitBoards.IsBoard(b.Part)||b.Part.Kind==PartKind.Battery)) warnings.Add("Перегрузка источника или короткое замыкание. Проверьте проводку.");
        }
        foreach (var servo in project.Parts.Where(p => p.Kind == PartKind.Servo))
        {
            var signal = Root(new(servo.Id, "SIG"));
            double? pulse = null;
            foreach (var mcu in project.Parts.Where(CircuitBoards.IsBoard))
            {
                for (var i = 0; digital is not null && i < Math.Min(CircuitBoards.PinCount(mcu), digital.Length); i++)
                    if (CircuitBoards.Pin(mcu,i) is { } name && Root(new(mcu.Id,name)) == signal) pulse = digital[i].PulseUs;
            }
            if (readings.TryGetValue(servo.Id, out var reading) && reading.Voltage > 3.5 && pulse is >= 500 and <= 2500)
                readings[servo.Id] = reading with { Indicator = Math.Clamp((pulse.Value - 500) * 180 / 2000, 0, 180) };
        }
        if (grounds.Length == 0 && project.Parts.Count > 0) warnings.Add("Нет опорной земли. Добавьте источник питания, Arduino или землю.");
        var solved = new CircuitResult(pins.ToDictionary(p => p, p => V(p, volts)), readings, warnings.ToArray());
        if (reference is null || !project.Parts.Any(p => p.Kind == PartKind.Esp32S3)) return solved;

        // Conducting components establish a reference. The numerical leakage
        // used to keep the matrix solvable must not turn a floating pin low.
        var conduction = new Dictionary<Terminal, List<Terminal>>();
        void Follow(Terminal a, Terminal b)
        {
            if (!conduction.TryGetValue(a, out var neighbors)) conduction.Add(a, neighbors = []);
            neighbors.Add(b);
        }
        foreach (var branch in branches)
        {
            if (branch.Diode ? V(branch.A, volts) - V(branch.B, volts) <= branch.Source : branch.G <= 1e-10) continue;
            var a = Root(branch.A);
            var b = Root(branch.B);
            Follow(a, b);
            Follow(b, a);
        }
        var known = new HashSet<Terminal> { reference };
        var pending = new Queue<Terminal>(known);
        while (pending.TryDequeue(out var node))
            if (conduction.TryGetValue(node, out var neighbors))
                foreach (var neighbor in neighbors)
                    if (known.Add(neighbor)) pending.Enqueue(neighbor);
        return solved with { ReferencedTerminals = pins.Where(pin => known.Contains(Root(pin))).ToHashSet() };
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
