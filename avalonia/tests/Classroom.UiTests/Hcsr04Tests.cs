using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class Hcsr04Tests
{
    public static CircuitProject Project(string board)
    {
        var pico=board=="pico";var p=new CircuitProject{Name="HC-SR04 · "+board};var ground=pico?PicoBoard.Ground:"GND";string Pin(int i)=>pico?PicoBoard.Gpio(i)!:"D"+i;
        p.Parts.AddRange([new("board",pico?PartKind.Pico:board=="mega2560"?PartKind.Mega:PartKind.Uno,60,60,ModelId:pico?PicoBoard.ModelId:board),new("sonar",PartKind.Hcsr04,700,110,100,true,ModelId:CircuitHcsr04.ModelId)]);
        p.Wires.AddRange([new("v",new("board",pico?PicoBoard.Vbus:"5V"),new("sonar","VCC")),new("g",new("board",ground),new("sonar","GND")),new("t",new("board",Pin(6)),new("sonar","TRIG"))]);
        if(!pico)p.Wires.Add(new("echo",new("sonar","ECHO"),new("board",Pin(7))));
        else{
            p.Parts.AddRange([new("top",PartKind.Resistor,450,160,1000,ModelId:"resistor-1000"),new("bottom",PartKind.Resistor,450,310,2200,ModelId:"resistor-2200")]);
            p.Wires.AddRange([new("echo-top",new("sonar","ECHO"),new("top","1")),new("top-gpio",new("top","2"),new("board",Pin(7))),new("gpio-bottom",new("board",Pin(7)),new("bottom","1")),new("bottom-gnd",new("bottom","2"),new("board",ground))]);
        }
        return p;
    }
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    [InlineData("pico")]
    public async Task RealPulseInMeasuresEchoAndTimesOutWhenTargetIsRemoved(string board)
    {
        var p=Project(board);var d=Assert.Single(CircuitHcsr04.Devices(p));Assert.True(d.Powered&&d.Connected);Assert.Equal(7,Assert.Single(d.Echoes).Pin);Assert.True(d.Echoes[0].High);Assert.False(d.Echoes[0].Low);Assert.NotNull(CircuitCodeGuide.Build(p).Sketch);
        var pico=board=="pico";var serial=pico?"Serial1":"Serial";
        p.Code=$$"""
            void setup(){ {{serial}}.begin(115200); pinMode(6,OUTPUT); digitalWrite(6,LOW); pinMode(7,INPUT); }
            void loop(){ digitalWrite(6,HIGH);delayMicroseconds(10);digitalWrite(6,LOW);noInterrupts();unsigned long us=pulseIn(7,HIGH,40000);interrupts();{{serial}}.println(us);delay(70); }
            """;
        var output="";var removed=false;
        void Observe(string text){output+=text;if(output.Contains('\n'))removed=true;}
        if(pico){var image=await PicoCompiler.Compile(p.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
            for(var i=0;i<100&&output.Count(c=>c=='\n')<2;i++){var f=await runner.Step(milliseconds:50,hcsr04Devices:[removed?d with{Target=false}:d]);Observe(f.SerialPorts[1]);Assert.Empty(f.Diagnostics);}
        }else{var b=AvrBoards.Get(board);var image=await FirmwareRunner.Compile(p.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(image,board);
            for(var i=0;i<20&&output.Count(c=>c=='\n')<2;i++){var f=await runner.Step(new bool[b.DigitalCount],new double[b.AnalogCount],hcsr04Devices:[removed?d with{Target=false}:d]);Observe(f.Serial);}
        }
        var values=output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();Assert.True(values.Length>=2,output);Assert.InRange(values[0],5780,5820);Assert.Equal(0,values[1]);
    }
    [Fact]
    public void DividerIsSolvedAndDirectFiveVoltEchoOnPicoIsRejected()
    {
        var p=Project("pico");var d=Assert.Single(CircuitHcsr04.Devices(p));var high=CircuitSolver.Solve(p,signals:new Dictionary<Terminal,double>{{new("sonar","ECHO"),5}});Assert.InRange(high.Voltages[new("board",PicoBoard.Gpio(7)!)],3.40,3.43);
        Assert.Null(CircuitPico.Inputs(p,CircuitSolver.Solve(p),[],hcsr04Devices:[d]).Digital[7]);
        var json=p.Serialize();Assert.Equal(json,CircuitProject.Parse(json).Serialize());ElectronicsProjectFormat.Inspect(json);
        p.Wires.Add(new("unsafe",new("sonar","ECHO"),new("board",PicoBoard.Gpio(7)!)));Assert.Contains("3,3",Assert.Throws<InvalidDataException>(()=>CircuitHcsr04.Devices(p)).Message);Assert.Null(CircuitCodeGuide.Build(p).Sketch);
        p=Project("pico");p.Parts[p.Parts.FindIndex(x=>x.Id=="bottom")]=p.Parts.Single(x=>x.Id=="bottom") with{Value=220};d=Assert.Single(CircuitHcsr04.Devices(p));Assert.False(Assert.Single(d.Echoes).High);Assert.Null(CircuitCodeGuide.Build(p).Sketch);
        p=Project("uno");p.Wires.RemoveAll(w=>w.Id=="g");Assert.False(Assert.Single(CircuitHcsr04.Devices(p)).Powered);
        p=Project("pico");p.Wires.RemoveAll(w=>w.Id=="t");d=Assert.Single(CircuitHcsr04.Devices(p));Assert.False(d.Connected);Assert.Empty(d.Echoes);
        var old=PartCatalog.All.First(d=>d.Kind==PartKind.Visual&&d.Art=="ultrasonic");var legacy=new CircuitProject();legacy.Parts.Add(new("legacy",PartKind.Visual,0,0,ModelId:old.Id,CustomPins:old.Pins));json=legacy.Serialize();Assert.Equal(json,CircuitProject.Parse(json).Serialize());Assert.False(PartCatalog.CanSimulate(legacy.Parts[0]));
    }
    [Fact]
    public async Task PicoRunsUltrasonicDhtAndOledTogetherThroughIndependentGpioInputs()
    {
        var p=Project("pico");var dht=Dht11Tests.Project("pico");p.Parts.AddRange(dht.Parts.Where(x=>x.Id!="board"));p.Wires.AddRange(dht.Wires.Select(w=>w.Id=="d"?w with{From=new("board",PicoBoard.Gpio(8)!)}:w with{Id="dht-"+w.Id}));
        p.Parts.Add(new("oled",PartKind.OledI2c,900,300,60,ModelId:"oled-ssd1306-i2c"));foreach(var (pin,bp) in new[]{("VDD",PicoBoard.V3),("GND",PicoBoard.Ground),("SDA",PicoBoard.Gpio(4)!),("SCL",PicoBoard.Gpio(5)!)})p.Wires.Add(new("oled-"+pin,new("board",bp),new("oled",pin)));
        var guide=CircuitCodeGuide.Build(p);Assert.NotNull(guide.Sketch);p.Code=guide.Sketch.Replace("Serial.","Serial1.");var image=await PicoCompiler.Compile(p.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);var text="";PicoFrame? frame=null;
        for(var i=0;i<220&&(!text.Contains("53.00 T=24.00")||!text.Contains("HC-SR04 cm="));i++){frame=await runner.Step(milliseconds:50,devices:CircuitPico.Devices(p),dht11Devices:CircuitDht11.Devices(p),hcsr04Devices:CircuitHcsr04.Devices(p));text+=frame.SerialPorts[1];Assert.Empty(frame.Diagnostics);}
        Assert.Contains("53.00 T=24.00",text);Assert.Contains("HC-SR04 cm=100.0",text);Assert.NotNull(frame);Assert.True(Assert.Single(frame.OledDisplays!).Display);
    }
    [Theory]
    [InlineData("uno")]
    [InlineData("pico")]
    public async Task RealGpioInterruptHandlerReceivesBothEchoEdges(string board)
    {
        var p=Project(board);var pico=board=="pico";var pin=pico?7:2;
        if(!pico)p.Wires[p.Wires.FindIndex(w=>w.Id=="echo")]=new("echo",new("sonar","ECHO"),new("board","D2"));
        var d=Assert.Single(CircuitHcsr04.Devices(p));var serial=pico?"Serial1":"Serial";
        p.Code=$$"""
            volatile unsigned int edges=0;
            void edge(){edges++;}
            void setup(){
              {{serial}}.begin(115200);pinMode(6,OUTPUT);digitalWrite(6,LOW);pinMode({{pin}},INPUT);
              attachInterrupt(digitalPinToInterrupt({{pin}}),edge,CHANGE);
              digitalWrite(6,HIGH);delayMicroseconds(10);digitalWrite(6,LOW);delay(15);
              {{serial}}.print(edges);{{serial}}.println(";DONE");
            }
            void loop(){}
            """;
        var output="";
        if(pico){var image=await PicoCompiler.Compile(p.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);for(var i=0;i<30&&!output.Contains("DONE");i++)output+=(await runner.Step(milliseconds:50,hcsr04Devices:[d])).SerialPorts[1];}
        else{var b=AvrBoards.Get(board);var image=await FirmwareRunner.Compile(p.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(image,board);for(var i=0;i<15&&!output.Contains("DONE");i++)output+=(await runner.Step(new bool[b.DigitalCount],new double[b.AnalogCount],hcsr04Devices:[d])).Serial;}
        Assert.Equal("2;DONE\r\n",output);
    }
}
