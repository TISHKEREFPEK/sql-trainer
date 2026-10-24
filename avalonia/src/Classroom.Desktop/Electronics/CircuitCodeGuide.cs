using System.Text;

namespace Classroom.Desktop.Electronics;

public sealed record CodeGuide(string Notes, string? Sketch);
public static class CircuitCodeGuide
{
    public static CodeGuide Build(CircuitProject project)
    {
        var notes = new List<string> { "setup() — начальная настройка; loop() — повторяющиеся действия. Код меняется только по вашей команде." };
        var unsupported = project.Parts.Where(p => !PartCatalog.CanSimulate(p)).ToArray();
        if (unsupported.Length > 0) notes.Add("Только для схемы: " + string.Join(", ", unsupported.Select(PartLibrary.Name).Distinct()) + ". Автонастройка и исполнение для этих моделей пока недоступны.");
        var uno = project.Parts.SingleOrDefault(p => p.Kind == PartKind.Uno);
        if (uno is null) return new(string.Join("\n", notes.Append("Для готового шаблона нужна Arduino Uno. Для Pico, Mega и ESP32 требуется отдельная поддержка платформы.")), null);
        var edges = project.Parts.SelectMany(p => PartLibrary.Pins(p).Select(pin => new Terminal(p.Id,pin))).ToDictionary(t => t, _ => new List<Terminal>());
        var parts = project.Parts.ToDictionary(p => p.Id);
        void Link(Terminal a, Terminal b) { edges[a].Add(b); edges[b].Add(a); }
        foreach (var wire in project.Wires) Link(wire.From,wire.To);
        foreach (var board in project.Parts.Where(p=>p.Kind==PartKind.Breadboard))
        {
            foreach (var group in new[] { "abcde", "fghij" })
                for (var row=1;row<=30;row++) foreach(var c in group.Skip(1)) Link(new(board.Id,group[0]+row.ToString(System.Globalization.CultureInfo.InvariantCulture)),new(board.Id,c+row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            foreach(var rail in new[]{"L+","L−","R+","R−"}) for(var row=2;row<=25;row++) Link(new(board.Id,rail+"1"),new(board.Id,rail+row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        HashSet<Terminal> Reach(Terminal start,bool throughResistors=false)
        {
            var reached=new HashSet<Terminal>(); var queue=new Queue<Terminal>(); queue.Enqueue(start);
            while(queue.TryDequeue(out var node))
            {
                if(!reached.Add(node)) continue;
                foreach(var next in edges[node]) queue.Enqueue(next);
                if(throughResistors && parts[node.Part].Kind==PartKind.Resistor) queue.Enqueue(new(node.Part,node.Pin=="1"?"2":"1"));
            }
            return reached;
        }
        string? Pin(Terminal terminal,bool analog=false,bool resistor=false)
        {
            var found=Reach(terminal,resistor).Where(t=>t.Part==uno.Id && (analog ? t.Pin.StartsWith('A') : t.Pin.StartsWith('D') || t.Pin.StartsWith('A'))).Select(t=>t.Pin.StartsWith('D')?t.Pin[1..]:t.Pin).Distinct().ToArray();
            return found.Length==1 ? found[0] : null;
        }
        bool Ground(Terminal terminal) => Reach(terminal).Any(t=>t.Pin=="GND" && (t.Part==uno.Id || parts[t.Part].Kind==PartKind.Ground));
        var declarations=new List<string>(); var setup=new List<string>(); var loop=new List<string>(); var used=new Dictionary<string,string>();
        var ready=unsupported.Length==0; var servo=false; var serial=false; var blink=false; var count=0;
        bool Claim(string? pin,CircuitPart part)
        {
            if(pin is null) { notes.Add(PartLibrary.Name(part)+": подключите сигнальный вывод к одному подходящему выводу Uno — номер нельзя определить однозначно."); ready=false; return false; }
            if(used.TryGetValue(pin,out var other)) { notes.Add($"Вывод {pin} уже используется: {other}. Совместное использование нужно настроить вручную."); ready=false; return false; }
            used[pin]=PartLibrary.Name(part); return true;
        }
        foreach(var part in project.Parts)
        {
            var n=++count;
            switch(part.Kind)
            {
                case PartKind.Led:
                {
                    notes.Add(PartLibrary.Name(part)+": pinMode(..., OUTPUT), digitalWrite(..., HIGH/LOW). Резистор остаётся частью схемы.");
                    var pin=Pin(new(part.Id,"A"),resistor:true); if(!Claim(pin,part)) break;
                    declarations.Add($"const int ledPin{n} = {pin};"); setup.Add($"pinMode(ledPin{n}, OUTPUT);"); loop.Add($"digitalWrite(ledPin{n}, ledOn ? HIGH : LOW);"); blink=true; notes.Add($"Светодиод: вывод Uno {pin}."); break;
                }
                case PartKind.Servo:
                {
                    notes.Add("Сервопривод: библиотека Servo.h, attach(вывод), write(угол 0…180). Библиотека уже установлена локально.");
                    var pin=Pin(new(part.Id,"SIG")); if(!Claim(pin,part)) break;
                    servo=true; declarations.Add($"Servo servo{n};"); setup.Add($"servo{n}.attach({pin});"); loop.Add($"servo{n}.write(90);"); notes.Add($"Servo SIG: вывод Uno {pin}."); break;
                }
                case PartKind.Pot3:
                case PartKind.AnalogSensor:
                {
                    notes.Add(PartLibrary.Name(part)+": analogRead(A0…A5), результат 0…1023.");
                    var pin=Pin(new(part.Id,part.Kind==PartKind.Pot3?"W":"+"),analog:true); if(!Claim(pin,part)) break;
                    serial=true; loop.Add($"Serial.println(analogRead({pin}));"); notes.Add($"Аналоговый сигнал: {pin}."); break;
                }
                case PartKind.Switch:
                {
                    notes.Add("Кнопка: pinMode(..., INPUT_PULLUP), digitalRead(...). В шаблоне второй вывод кнопки должен быть подключён к GND.");
                    var pin=Ground(new(part.Id,"1"))?Pin(new(part.Id,"2")):Ground(new(part.Id,"2"))?Pin(new(part.Id,"1")):null;
                    if(!Claim(pin,part)) break;
                    declarations.Add($"const int buttonPin{n} = {pin};"); setup.Add($"pinMode(buttonPin{n}, INPUT_PULLUP);"); loop.Add($"Serial.println(digitalRead(buttonPin{n}));"); serial=true; break;
                }
                case PartKind.Motor:
                case PartKind.Buzzer:
                    notes.Add(PartLibrary.Name(part)+": программа зависит от драйвера и способа управления. Настройте управление вручную."); ready=false; break;
            }
        }
        if(!ready) return new(string.Join("\n",notes),null);
        var code=new StringBuilder("// Начальный шаблон по текущим соединениям Arduino Uno.\n");
        if(servo) code.Append("#include <Servo.h>\n");
        foreach(var line in declarations) code.AppendLine(line);
        code.Append("\nvoid setup() {\n"); if(serial) code.Append("  Serial.begin(9600);\n"); foreach(var line in setup) code.Append("  ").AppendLine(line);
        code.Append("}\n\nvoid loop() {\n"); if(blink) code.Append("  static bool ledOn = false;\n  ledOn = !ledOn;\n"); foreach(var line in loop) code.Append("  ").AppendLine(line);
        code.Append("  delay(500);\n}\n");
        return new(string.Join("\n",notes),code.ToString());
    }
}
