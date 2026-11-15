namespace Classroom.Desktop.Electronics;

public static class PicoCodeGuide
{
    public static CodeGuide Build(CircuitProject project)
    {
        var board=project.Parts.Single(p=>p.Kind==PartKind.Pico);var nets=new CircuitConnections(project);
        var notes=new List<string>{"Pico: GPIO и ADC работают на 3,3 В. Serial — USB, Serial1 — UART0, Serial2 — UART1. Шаблон меняет код только по вашей команде."};
        try{PicoBoard.ValidateRun(project);var dc=CircuitSolver.Solve(project);_=CircuitHcsr04.Devices(project,dc);_=CircuitPico.Inputs(project,dc,CircuitPico.Devices(project,dc));}catch(InvalidDataException e){return new(e.Message,null);}
        var setup=new List<string>{"Serial.begin(115200);","analogReadResolution(12);"};var loop=new List<string>();var globals=new List<string>();var claimed=new HashSet<int>();var ready=true;var oled=false;var dht=false;
        int? Pin(Terminal terminal,bool resistors=false){
            var reached=new HashSet<Terminal>{nets.Root(terminal)};bool more;
            do{more=false;if(resistors)foreach(var r in project.Parts.Where(p=>p.Kind==PartKind.Resistor)){var a=nets.Root(new(r.Id,"1"));var b=nets.Root(new(r.Id,"2"));if(reached.Contains(a))more|=reached.Add(b);if(reached.Contains(b))more|=reached.Add(a);}}while(more);
            var found=Enumerable.Range(0,30).Where(i=>PicoBoard.Gpio(i) is {}pin&&reached.Contains(nets.Root(new(board.Id,pin)))).ToArray();return found.Length==1?found[0]:null;
        }
        bool Claim(int? pin,CircuitPart part){if(pin is null||!claimed.Add(pin.Value)){notes.Add(PartLibrary.Name(part)+": подключите один свободный подходящий GPIO; совместные выводы настройте вручную.");ready=false;return false;}return true;}
        bool Ground(Terminal pin)=>nets.Same(pin,new(board.Id,PicoBoard.Ground));
        var buses=new Dictionary<int,(int Sda,int Scl)>();var devices=CircuitPico.Devices(project);
        foreach(var part in project.Parts){
            if(!PartCatalog.CanSimulate(part)){notes.Add(PartLibrary.Name(part)+": пока только схема.");ready=false;continue;}
            if(part.Kind==PartKind.Hcsr04){
                notes.Add(Hcsr04CodeGuide.Note);var d=CircuitHcsr04.Devices(project).Single(d=>d.Id==part.Id);
                if(!d.Powered||!d.Connected||!d.Echoes.Any(e=>e.High&&!e.Low)){notes.Add("HC-SR04: исправьте питание, Trig и уровни Echo.");ready=false;continue;}
                var valid=Claim(d.Trig,part);foreach(var e in d.Echoes)valid=Claim(e.Pin,part)&&valid;
                if(valid){setup.Add(Hcsr04CodeGuide.Setup(d.Trig.ToString(),d.Echoes.First(e=>e.High&&!e.Low).Pin.ToString()));loop.Add(Hcsr04CodeGuide.Loop(d.Trig.ToString(),d.Echoes.First(e=>e.High&&!e.Low).Pin.ToString()));}
            }
            else if(part.Kind==PartKind.Dht11){
                var device=CircuitDht11.Devices(project).Single(d=>d.Id==part.Id);var pin=Pin(new(part.Id,"DATA"));
                notes.Add("DHT11: питание 3,3 В, общая GND, отдельный DATA, NC не подключать; рекомендуется подтяжка 5,1 кОм. Библиотека DHT.h доступна автономно; после прогрева возвращается предыдущее измерение.");
                if(!device.Powered||!device.Connected){notes.Add("DHT11: исправьте питание и DATA.");ready=false;continue;}
                if(Claim(pin,part)){dht=true;var name="dht"+globals.Count;globals.Add($"DHT {name}({pin},DHT11);");setup.Add($"{name}.begin();");loop.Add($"{{ float h={name}.readHumidity(), t={name}.readTemperature(); if(isnan(h)||isnan(t)) Serial.println(\"DHT11: read error\"); else {{ Serial.print(\"DHT11 RH=\"); Serial.print(h); Serial.print(\" T=\"); Serial.println(t); }} }}");}
            }
            else if(part.Kind==PartKind.Led){var pin=Pin(new(part.Id,"A"),true);if(!Ground(new(part.Id,"K"))){notes.Add("Катод LED должен быть подключён к GND.");ready=false;continue;}if(Claim(pin,part)){setup.Add($"pinMode({pin}, OUTPUT);");loop.Add($"digitalWrite({pin}, HIGH); delay(500); digitalWrite({pin}, LOW); delay(500);");}}
            else if(part.Kind==PartKind.Switch){var pin=Ground(new(part.Id,"1"))?Pin(new(part.Id,"2")):Ground(new(part.Id,"2"))?Pin(new(part.Id,"1")):null;if(Claim(pin,part)){setup.Add($"pinMode({pin}, INPUT_PULLUP);");loop.Add($"Serial.println(digitalRead({pin}));");}}
            else if(part.Kind is PartKind.Pot3 or PartKind.AnalogSensor){var pin=Pin(new(part.Id,part.Kind==PartKind.Pot3?"W":"+"));if(pin is not (26 or 27 or 28)){notes.Add("Для ADC подключите сигнал к GP26, GP27 или GP28.");ready=false;}else if(Claim(pin,part))loop.Add($"Serial.println(analogRead({pin}));");}
            else if(part.Kind==PartKind.OledI2c){
                var d=devices.Single(d=>d.Id==part.Id);if(!d.Connected||!d.Powered||d.Sda%4!=d.Controller*2||d.Scl%4!=d.Controller*2+1){notes.Add("OLED: нужны питание 3,3 В, общая GND и аппаратные SDA/SCL одного банка. Программную шину настройте вручную.");ready=false;continue;}
                if(buses.TryGetValue(d.Controller,out var pair)&&pair!=(d.Sda,d.Scl)||claimed.Contains(d.Sda)&&!buses.ContainsKey(d.Controller)||claimed.Contains(d.Scl)&&!buses.ContainsKey(d.Controller)){notes.Add("Выводы I2C уже используются; настройте общий bus вручную.");ready=false;continue;}
                if(!buses.ContainsKey(d.Controller)){buses[d.Controller]=(d.Sda,d.Scl);claimed.Add(d.Sda);claimed.Add(d.Scl);var wire=d.Controller==0?"Wire":"Wire1";setup.Add($"{wire}.setSDA({d.Sda}); {wire}.setSCL({d.Scl}); {wire}.begin();");}
                var bus=d.Controller==0?"Wire":"Wire1";var name="oled"+globals.Count;oled=true;globals.Add($"Adafruit_SSD1306 {name}(128,64,&{bus},-1);");
                setup.Add($"if (!{name}.begin(SSD1306_SWITCHCAPVCC,0x{d.Address:X2})) {{ Serial.println(\"OLED: allocation failed\"); return; }}");
                setup.Add($"{name}.clearDisplay(); {name}.setTextColor(SSD1306_WHITE); {name}.setTextSize(1); {name}.setCursor(8,8); {name}.println(\"Pico ready\"); {name}.drawRect(0,0,128,64,SSD1306_WHITE); {name}.display();");
            }else if(part.Kind is PartKind.LcdI2c or PartKind.Servo or PartKind.Motor or PartKind.Buzzer){notes.Add(PartLibrary.Name(part)+": для этой комбинации подготовьте код вручную; автоматический шаблон ещё не подключён.");ready=false;}
        }
        if(!ready)return new(string.Join('\n',notes),null);
        var ds18=Ds18b20CodeGuide.Build(project,board);notes.AddRange(ds18.Notes);
        if(!ds18.Ready||ds18.Pins.Any(claimed.Contains))return new(string.Join('\n',notes.Append("DS18B20: вывод шины занят другой деталью.")),null);
        foreach(var pin in ds18.Pins)claimed.Add(pin);globals.AddRange(ds18.Declarations);setup.AddRange(ds18.Setup);loop.AddRange(ds18.Loop);
        var tft=TftCodeGuide.Build(project,board);notes.AddRange(tft.Notes);
        if(!tft.Ready||tft.Pins.Any(claimed.Contains))return new(string.Join('\n',notes.Append("Совместные выводы TFT и других деталей настройте вручную; шаблон не изменяет проводку.")),null);
        globals.AddRange(tft.Declarations);setup.AddRange(tft.Setup);
        var includes=oled?"#include <Wire.h>\n#include <Adafruit_GFX.h>\n#include <Adafruit_SSD1306.h>\n":"";
        if(tft.Declarations.Length>0)includes+=TftCodeGuide.Includes;
        if(ds18.Pins.Length>0)includes+=Ds18b20CodeGuide.Includes;
        if(dht){includes+="#include <DHT.h>\n";setup.Add("delay(1100);");loop.Add("delay(2000);");}
        return new(string.Join('\n',notes),includes+string.Join('\n',globals)+"\nvoid setup(){\n  "+string.Join("\n  ",setup)+"\n}\nvoid loop(){\n  "+string.Join("\n  ",loop)+"\n  delay(20);\n}\n");
    }
}
