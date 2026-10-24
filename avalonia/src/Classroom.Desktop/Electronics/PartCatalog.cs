using System.Text.Json;
using System.Text.RegularExpressions;

namespace Classroom.Desktop.Electronics;

public record PartDefinition(string Id, string Name, string Category, PartKind Kind, double Value, string Art, bool Simulated, string[]? Pins = null, string? ReferenceName = null);

public static class PartCatalog
{
    public static readonly PartDefinition[] All = Build();
    static readonly Dictionary<string, PartDefinition> ById = All.ToDictionary(d => d.Id, StringComparer.Ordinal);
    public static PartDefinition? Find(string? id) => id is null ? null : ById.GetValueOrDefault(id);
    public static PartDefinition For(CircuitPart part) => Find(part.ModelId) ?? All.First(d => d.Kind == part.Kind);
    public static bool CanSimulate(CircuitPart part) => For(part).Simulated;
    public static string[] Categories => All.Select(d => d.Category).Distinct().OrderBy(c => c).Prepend("Все категории").ToArray();
    static PartDefinition[] Build()
    {
        var list = new List<PartDefinition>
        {
            new("uno", "Arduino Uno", "Платы", PartKind.Uno, 0, "uno", true, ReferenceName:"Arduino UNO"),
            new("breadboard", "Макетная плата · 400 точек", "Монтаж", PartKind.Breadboard, 0, "breadboard", true, ReferenceName:"Breadboard (Half Size)"),
            new("resistor-330", "Резистор · 330 Ом", "Пассивные", PartKind.Resistor, 330, "resistor", true, ReferenceName:"Resistor"),
            new("led-red", "Светодиод · красный", "Индикация", PartKind.Led, 2, "led-red", true, ReferenceName:"LED Two Pin (Red)"),
            new("pushbutton", "Кнопка", "Управление", PartKind.Switch, 0, "button", true, ReferenceName:"Pushbutton"),
            new("supply-5", "Источник · 5 В", "Питание", PartKind.Battery, 5, "battery", true, ReferenceName:"5V DC Power Supply"),
            new("ground", "Земля", "Питание", PartKind.Ground, 0, "ground", true),
            new("rheostat", "Переменный резистор · 10 кОм", "Пассивные", PartKind.Potentiometer, 10000, "pot", true),
            new("analog-source", "Аналоговый сигнал · 0–5 В", "Датчики", PartKind.AnalogSensor, 2.5, "pot", true),
            new("pot-3", "Потенциометр · 3 вывода", "Управление", PartKind.Pot3, 10000, "pot", true, ReferenceName:"Trimmer Potentiometer"),
            new("diode", "Выпрямительный диод", "Полупроводники", PartKind.Diode, 0.7, "diode", true, ReferenceName:"Rectifier Diode"),
            new("motor", "Двигатель постоянного тока", "Приводы", PartKind.Motor, 50, "motor", true, ReferenceName:"DC Gear Motor (3-6V)"),
            new("buzzer", "Пьезоизлучатель", "Индикация", PartKind.Buzzer, 200, "buzzer", true, ReferenceName:"Piezo Speaker"),
            new("servo", "Сервопривод SG90", "Приводы", PartKind.Servo, 0, "servo", true, ReferenceName:"Servo"),
            new("battery-9", "Батарея · 9 В", "Питание", PartKind.Battery, 9, "battery", true, ReferenceName:"9V Battery"),
            new("supply-12", "Источник · 12 В", "Питание", PartKind.Battery, 12, "battery", true, ReferenceName:"12V DC Power Supply"),
            new("led-green", "Светодиод · зелёный", "Индикация", PartKind.Led, 2.2, "led-green", true, ReferenceName:"LED Two Pin (Green)"),
            new("led-blue", "Светодиод · синий", "Индикация", PartKind.Led, 3, "led-blue", true, ReferenceName:"LED Two Pin (Blue)"),
            new("led-yellow", "Светодиод · жёлтый", "Индикация", PartKind.Led, 2.1, "led-yellow", true, ReferenceName:"LED Two Pin (Yellow)"),
            new("ldr", "Фоторезистор", "Датчики", PartKind.Potentiometer, 10000, "ldr", true),
            new("ntc", "Термистор NTC", "Датчики", PartKind.Potentiometer, 10000, "ntc", true),
            new("capacitor", "Электролитический конденсатор", "Пассивные", PartKind.Visual, 0, "electrolytic", false, ["+", "−"]),
            new("ceramic", "Керамический конденсатор", "Пассивные", PartKind.Visual, 0, "ceramic", false, ["1", "2"])
        };
        foreach (var value in new[] { 100, 220, 470, 680, 1000, 2200, 4700, 10000, 47000, 100000 })
            list.Add(new("resistor-" + value, "Резистор · " + (value >= 1000 ? value / 1000.0 + " кОм" : value + " Ом"), "Пассивные", PartKind.Resistor, value, "resistor", true));
        var reference = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "electronics", "catalog-reference.json"))) ?? [];
        foreach (var name in reference)
        {
            if (list.Any(d => d.ReferenceName == name)) continue;
            var art = Artwork(name);
            var id = "catalog-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();
            list.Add(new(id, name, Category(name), PartKind.Visual, 0, art, false, KnownPins(name), name));
        }
        return list.ToArray();
    }
    static string Category(string n)
    {
        if (Regex.IsMatch(n, "Arduino|ESP32|Pico")) return "Платы";
        if (n.Contains("Breadboard")) return "Монтаж";
        if (Regex.IsMatch(n, "Motor|Servo|Pump|Solenoid|Relay|Electromagnet|A4988|L298N", RegexOptions.IgnoreCase)) return "Приводы";
        if (Regex.IsMatch(n, "LED|LCD|OLED|Display|Segment|eInk|E-Ink|Light Module", RegexOptions.IgnoreCase)) return "Индикация";
        if (Regex.IsMatch(n, "Button|Switch|Keypad|Joystick|Encoder|Potentiometer", RegexOptions.IgnoreCase)) return "Управление";
        if (Regex.IsMatch(n, "Battery|Supply|Solar|Power|Regulator", RegexOptions.IgnoreCase)) return "Питание";
        if (Regex.IsMatch(n, "Sensor|Grove|DHT|BMP|MPU|MQ-|MAX301|DS18|VL53|HX711|Turbidity|Rain|TCS|pH ", RegexOptions.IgnoreCase)) return "Датчики";
        if (Regex.IsMatch(n, "Transistor|MOSFET|PNP|Diode|Gate|74|CD40|FlipFlop|LM393|Converter", RegexOptions.IgnoreCase)) return "Полупроводники";
        return "Модули";
    }
    static string Artwork(string n)
    {
        if (n.Contains("Mega")) return "mega";
        if (n.Contains("ESP32")) return "esp32";
        if (n.Contains("Pico")) return "pico";
        if (n.Contains("Breadboard")) return "breadboard";
        if (n.Contains("UNO")) return "uno";
        if (n.Contains("HC-SR04") || n.Contains("Ultrasonic")) return "ultrasonic";
        if (n.Contains("DHT22")) return "dht22";
        if (n.Contains("DHT11")) return "dht11";
        if (n.Contains("eInk") || n.Contains("E-Ink")) return "eink";
        if (n.Contains("LM35")) return "lm35";
        if (n.Contains("BMP180")) return "bmp";
        if (n.Contains("RC522")) return "rfid";
        if (n.Contains("MQ-2")) return "mq2";
        if (n.Contains("MQ-")) return "mq135";
        if (n.Contains("Soil")) return "soil";
        if (n.Contains("Water Level") || n.Contains("Rain")) return "water";
        if (n.Contains("PIR")) return "pir";
        if (n.Contains("Flame")) return "flame";
        if (n.Contains("Photoresistor") || n.Contains("Light Sensor")) return "ldr-module";
        if (n.Contains("MPU6050")) return "mpu";
        if (n.Contains("DS3231") || n.Contains("DS1307")) return "rtc";
        if (n.Contains("OLED")) return "oled";
        if (n.Contains("LCD") || n.Contains("ILI9341") || n.Contains("eInk") || n.Contains("E-Ink")) return "lcd";
        if (n.Contains("Matrix") || n.Contains("NeoMatrix")) return "matrix";
        if (n.Contains("4 Digit") || n.Contains("4-Digit")) return "four-digit";
        if (n.Contains("Segment") || n.Contains("Voltmeter")) return "seven-segment";
        if (n.Contains("Keypad")) return "keypad";
        if (n.Contains("Relay")) return "relay";
        if (n.Contains("Joystick")) return "joystick";
        if (n.Contains("A4988")) return "a4988";
        if (n.Contains("L298N")) return "l298n";
        if (n.Contains("Stepper")) return "stepper";
        if (n.Contains("Motor") || n.Contains("Pump") || n.Contains("Solenoid")) return "motor";
        if (n.Contains("RGB") || n.Contains("NeoPixel")) return "rgb-led";
        if (n.Contains("DIP Switch")) return "dip-switch";
        if (n.Contains("Toggle")) return "toggle";
        if (n.Contains("Button")) return "button";
        if (n.Contains("Switch")) return "slide";
        if (n.Contains("Potentiometer") || n.Contains("Encoder")) return "pot";
        if (n.Contains("MOSFET")) return "mosfet";
        if (n.Contains("Transistor") || n == "PNP" || n.Contains("DS18B20") || n.Contains("LM35")) return "transistor";
        if (n.Contains("Supply") || n.Contains("Battery") || n.Contains("Solar")) return "battery";
        if (n.Contains("Regulator")) return "buck";
        if (Category(n) == "Датчики") return "mpu";
        return "dip16-accurate";
    }
    static string[] KnownPins(string n) => n switch
    {
        "Raspberry Pi Pico" => new[] { "GP0", "GP1", "GND", "GP2", "GP3", "GP4", "GP5", "GND", "GP6", "GP7", "GP8", "GP9", "GND", "GP10", "GP11", "GP12", "GP13", "GND", "GP14", "GP15", "GP16", "GP17", "GND", "GP18", "GP19", "GP20", "GP21", "GND", "GP22", "RUN", "GP26/ADC0", "GP27/ADC1", "AGND", "GP28/ADC2", "ADC_VREF", "3V3", "3V3_EN", "GND", "VSYS", "VBUS" }.Select((pin,i) => (i+1) + ":" + pin).ToArray(),
        "HC-SR04 Ultrasonic Sensor" => ["VCC", "TRIG", "ECHO", "GND"],
        "DHT22" => ["VCC", "DATA", "NC", "GND"],
        "MOSFET" => ["G", "D", "S"],
        "BC547 Transistor" or "PNP" => ["C", "B", "E"],
        "4x4 Keypad" => ["R1", "R2", "R3", "R4", "C1", "C2", "C3", "C4"],
        "Toggle Switch SPST" => ["1", "2"],
        "Comment" => [],
        _ => Enumerable.Range(1, Artwork(n) is "dip16" or "dip16-accurate" ? 16 : Artwork(n) == "dip8" ? 8 : 4).Select(i => "P" + i).ToArray()
    };
}
