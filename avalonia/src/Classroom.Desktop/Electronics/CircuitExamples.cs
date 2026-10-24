namespace Classroom.Desktop.Electronics;

public static class CircuitExamples
{
    public static readonly string[] Names = ["Мигание LED", "Потенциометр и PWM", "Сервопривод", "Макетная плата"];
    public static CircuitProject Create(int index) => index switch { 1 => Analog(), 2 => Servo(), 3 => Breadboard(), _ => CircuitProject.Blink() };
    public static CircuitProject Analog()
    {
        var p = CircuitProject.Blink(); p.Name = "Потенциометр · яркость LED";
        p.Parts.Add(new("pot", PartKind.Pot3, 390, 300, 10000, ModelId: "pot-3"));
        p.Wires[0] = p.Wires[0] with { From = new("uno", "D9") };
        p.Wires.AddRange([new("p1", new("pot", "1"), new("uno", "5V")), new("p2", new("pot", "3"), new("uno", "GND")), new("p3", new("pot", "W"), new("uno", "A0"))]);
        p.Code = "void setup() {\n  pinMode(9, OUTPUT);\n  Serial.begin(9600);\n}\n\nvoid loop() {\n  int value = analogRead(A0);\n  analogWrite(9, value / 4);\n  Serial.println(value);\n  delay(20);\n}\n";
        return p;
    }
    public static CircuitProject Servo()
    {
        var p = new CircuitProject { Name = "Arduino Uno · сервопривод" };
        p.Parts.AddRange([new("uno", PartKind.Uno, 80, 100), new("servo", PartKind.Servo, 490, 110, ModelId:"servo")]);
        p.Wires.AddRange([new("s1", new("uno", "5V"), new("servo", "VCC")), new("s2", new("uno", "GND"), new("servo", "GND")), new("s3", new("uno", "D9"), new("servo", "SIG"))]);
        p.Code = "#include <Servo.h>\nServo motor;\n\nvoid setup() {\n  motor.attach(9);\n}\n\nvoid loop() {\n  motor.write(0);\n  delay(1000);\n  motor.write(90);\n  delay(1000);\n  motor.write(180);\n  delay(1000);\n}\n";
        return p;
    }
    public static CircuitProject Breadboard()
    {
        var p = new CircuitProject { Name = "Arduino Uno · LED на макетной плате", Code = CircuitProject.Blink().Code };
        p.Parts.AddRange([new("uno", PartKind.Uno, 60, 90), new("board", PartKind.Breadboard, 400, 70, ModelId:"breadboard"), new("r", PartKind.Resistor, 740, 90, 330), new("led", PartKind.Led, 960, 100, 2)]);
        p.Wires.AddRange([new("b1", new("uno", "D13"), new("board", "a1")), new("b2", new("board", "e1"), new("r", "1")), new("b3", new("r", "2"), new("board", "a2")), new("b4", new("board", "e2"), new("led", "A")), new("b5", new("led", "K"), new("board", "a3")), new("b6", new("board", "e3"), new("uno", "GND"))]);
        return p;
    }
}
