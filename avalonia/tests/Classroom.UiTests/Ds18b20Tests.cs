using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class Ds18b20Tests
{
    [Fact]
    public async Task PicoRunsOneWireDhtUltrasonicAndOledOnIndependentPins()
    {
        var p=Hcsr04Tests.Project("pico");var dht=Dht11Tests.Project("pico");
        p.Parts.AddRange(dht.Parts.Where(x=>x.Id!="board"));
        p.Wires.AddRange(dht.Wires.Select(w=>w.Id=="d"?w with{Id="dht-data",From=new("board",PicoBoard.Gpio(8)!)}:w with{Id="dht-"+w.Id}));
        p.Parts.AddRange([new("cold",PartKind.Ds18b20,1050,100,25,ModelId:CircuitDs18b20.ModelId),new("ds-pullup",PartKind.Resistor,800,120,4700,ModelId:"resistor-4700"),new("oled",PartKind.OledI2c,900,300,60,ModelId:"oled-ssd1306-i2c")]);
        foreach(var (pin,bp) in new[]{("VDD",PicoBoard.V3),("GND",PicoBoard.Ground),("DQ",PicoBoard.Gpio(9)!)})p.Wires.Add(new("ds-"+pin,new("board",bp),new("cold",pin)));
        p.Wires.AddRange([new("ds-r1",new("cold","DQ"),new("ds-pullup","1")),new("ds-r2",new("board",PicoBoard.V3),new("ds-pullup","2"))]);
        foreach(var (pin,bp) in new[]{("VDD",PicoBoard.V3),("GND",PicoBoard.Ground),("SDA",PicoBoard.Gpio(4)!),("SCL",PicoBoard.Gpio(5)!)})p.Wires.Add(new("oled-"+pin,new("board",bp),new("oled",pin)));
        var guide=CircuitCodeGuide.Build(p);Assert.NotNull(guide.Sketch);p.Code=guide.Sketch.Replace("Serial.","Serial1.");
        var image=await PicoCompiler.Compile(p.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);var text="";PicoFrame? frame=null;
        for(var i=0;i<260&&(!text.Contains("DS18B20[0] C=25.0000")||!text.Contains("53.00 T=24.00")||!text.Contains("HC-SR04 cm=100.0"));i++){
            frame=await runner.Step(milliseconds:50,devices:CircuitPico.Devices(p),dht11Devices:CircuitDht11.Devices(p),hcsr04Devices:CircuitHcsr04.Devices(p),ds18b20Devices:CircuitDs18b20.Devices(p));text+=frame.SerialPorts[1];Assert.Empty(frame.Diagnostics);
        }
        Assert.Contains("DS18B20[0] C=25.0000",text);Assert.Contains("53.00 T=24.00",text);Assert.Contains("HC-SR04 cm=100.0",text);Assert.NotNull(frame);Assert.True(Assert.Single(frame.OledDisplays!).Display);Assert.Equal(25,Assert.Single(frame.Ds18b20!).Reading);
    }
    public static CircuitProject Project(string board,bool parasite=false,bool two=true)
    {
        var pico=board=="pico";var p=new CircuitProject{Name="DS18B20 · "+board};var ground=pico?PicoBoard.Ground:"GND";var supply=pico?PicoBoard.V3:"5V";
        p.Parts.AddRange([new("board",pico?PartKind.Pico:board=="mega2560"?PartKind.Mega:PartKind.Uno,60,60,ModelId:pico?PicoBoard.ModelId:board),new("cold",PartKind.Ds18b20,650,100,-10.125,ModelId:CircuitDs18b20.ModelId),new("pullup",PartKind.Resistor,400,100,4700,ModelId:"resistor-4700")]);
        p.Wires.AddRange([new("g",new("board",ground),new("cold","GND")),new("v",new("board",parasite?ground:supply),new("cold","VDD")),new("d",new("board",pico?PicoBoard.Gpio(6)!:"D6"),new("cold","DQ")),new("r1",new("pullup","1"),new("cold","DQ")),new("r2",new("pullup","2"),new("board",supply))]);
        if(two){p.Parts.Add(new("warm",PartKind.Ds18b20,850,100,31.5,ModelId:CircuitDs18b20.ModelId));foreach(var pin in new[]{"GND","DQ","VDD"})p.Wires.Add(new("warm-"+pin,new("cold",pin),new("warm",pin)));}
        return p;
    }
    [Theory]
    [InlineData("uno",false)]
    [InlineData("mega2560",false)]
    [InlineData("pico",false)]
    [InlineData("uno",true)]
    [InlineData("pico",true)]
    public async Task RealOneWireAndDallasFindTwoDevicesMeasureAndWriteEeprom(string board,bool parasite)
    {
        var p=Project(board,parasite);var devices=CircuitDs18b20.Devices(p);Assert.Equal(2,devices.Length);Assert.All(devices,d=>Assert.True(d.Connected&&d.Powered&&d.Pullup));Assert.Equal(2,devices.Select(d=>d.Rom).Distinct().Count());Assert.All(devices,d=>Assert.Equal(parasite,d.Parasite));Assert.NotNull(CircuitCodeGuide.Build(p).Sketch);
        var serial=board=="pico"?"Serial1":"Serial";p.Code=$$"""
            #include <OneWire.h>
            #include <DallasTemperature.h>
            OneWire wire(6);DallasTemperature sensors(&wire);DeviceAddress address;
            void setup(){
              {{serial}}.begin(115200);sensors.begin();{{serial}}.print("N=");{{serial}}.println(sensors.getDeviceCount());
              sensors.requestTemperatures();for(uint8_t i=0;i<sensors.getDeviceCount();i++){{serial}}.println(sensors.getTempCByIndex(i),4);
              sensors.getAddress(address,0);sensors.setResolution(address,9);sensors.saveScratchPad(address);
              {{serial}}.print("R=");{{serial}}.println(sensors.getResolution(address));{{serial}}.println("DONE");
            }
            void loop(){}
            """;
        var text="";var writes=new List<OneWireMemoryWrite>();
        if(board=="pico"){
            var image=await PicoCompiler.Compile(p.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
            for(var i=0;i<280&&!text.Contains("DONE");i++){var f=await runner.Step(milliseconds:50,ds18b20Devices:devices);text+=f.SerialPorts[1];writes.AddRange(f.OneWireWrites??[]);Assert.Empty(f.Diagnostics);}
        }else{
            var b=AvrBoards.Get(board);var image=await FirmwareRunner.Compile(p.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(image,board);
            for(var i=0;i<120&&!text.Contains("DONE");i++){var f=await runner.Step(new bool[b.DigitalCount],new double[b.AnalogCount],ds18b20Devices:devices);text+=f.Serial;writes.AddRange(f.OneWireWrites??[]);}
        }
        Assert.Contains("N=2\r\n",text);Assert.Contains("-10.1250\r\n",text);Assert.Contains("31.5000\r\n",text);Assert.Contains("R=9\r\nDONE\r\n",text);Assert.Contains(writes,w=>w.Memory[2]==0x1f);Assert.All(writes,w=>Assert.True(CircuitDs18b20.ValidMemory(w.Memory)));
    }
    [Fact]
    public void OldJsonIsUnchangedAndDeviceMemoryIsBoundedAcrossDesktopAndServer()
    {
        var old=CircuitProject.Blink().Serialize();Assert.DoesNotContain("deviceMemory",old);Assert.Equal(old,CircuitProject.Parse(old).Serialize());
        var p=Project("pico");var json=p.Serialize();Assert.Equal(json,CircuitProject.Parse(json).Serialize());ElectronicsProjectFormat.Inspect(json);var devices=CircuitDs18b20.Devices(p);Assert.Null(CircuitPico.Inputs(p,CircuitSolver.Solve(p),[],ds18b20Devices:devices).Digital[6]);
        p.Parts[1]=p.Parts[1] with{DeviceMemory=[15,240,31]};json=p.Serialize();ElectronicsProjectFormat.Inspect(json);Assert.Equal([15,240,31],CircuitProject.Parse(json).Parts[1].DeviceMemory!);
        p.Parts[1]=p.Parts[1] with{DeviceMemory=[0,0,0]};Assert.Throws<InvalidDataException>(()=>p.Serialize());Assert.Throws<InvalidDataException>(()=>ElectronicsProjectFormat.Inspect(json.Replace("31","0")));
        p=Project("pico");p.Wires[p.Wires.FindIndex(w=>w.Id=="r2")]=new("r2",new("pullup","2"),new("board",PicoBoard.Vbus));Assert.Throws<InvalidDataException>(()=>CircuitPico.Inputs(p,CircuitSolver.Solve(p),[],ds18b20Devices:CircuitDs18b20.Devices(p)));
        var legacy=PartCatalog.All.First(d=>d.Kind==PartKind.Visual&&d.ReferenceName?.Contains("DS18B20")==true);var v=new CircuitProject();v.Parts.Add(new("old",PartKind.Visual,0,0,ModelId:legacy.Id,CustomPins:legacy.Pins));old=v.Serialize();Assert.Equal(old,CircuitProject.Parse(old).Serialize());Assert.False(PartCatalog.CanSimulate(v.Parts[0]));
    }
    [Fact]
    public async Task AvrDirectRomAndScratchpadAreReadAtTheWire()
    {
        var p=Project("uno",two:false);var code="""
            #include <OneWire.h>
            OneWire wire(6);byte address[8];
            void hex(byte b){if(b<16)Serial.print('0');Serial.print(b,HEX);}
            void setup(){Serial.begin(115200);wire.reset();wire.write(0x33);for(byte i=0;i<8;i++){address[i]=wire.read();hex(address[i]);}Serial.print(';');
              wire.reset();wire.select(address);wire.write(0xBE);for(byte i=0;i<9;i++)hex(wire.read());Serial.println(";DONE");}
            void loop(){}
            """;
        var image=await FirmwareRunner.Compile(code,CancellationToken.None);using var runner=new FirmwareRunner();await runner.Start(image);var text="";
        for(var i=0;i<20&&!text.Contains("DONE");i++)text+=(await runner.Step(new bool[14],new double[6],ds18b20Devices:CircuitDs18b20.Devices(p))).Serial;
        Assert.StartsWith(CircuitDs18b20.Rom(p.Parts[1]).ToUpperInvariant()+";50054B467FFF0C10",text);
    }
    [Fact]
    public async Task EightDevicesShareOnePicoBusAndBroadcastEepromCopyIsDelivered()
    {
        var p=Project("pico",two:false);p.Parts[1]=p.Parts[1] with{Value=10};
        for(var i=1;i<8;i++){var id="sensor"+i;p.Parts.Add(p.Parts[1] with{Id=id,Value=10+i,X=650+i*110});foreach(var pin in new[]{"GND","DQ","VDD"})p.Wires.Add(new(id+pin,new("cold",pin),new(id,pin)));}
        var devices=CircuitDs18b20.Devices(p);Assert.Equal(8,devices.Length);p.Code="""
            #include <OneWire.h>
            #include <DallasTemperature.h>
            OneWire w(6);DallasTemperature s(&w);
            void setup(){Serial1.begin(115200);s.begin();Serial1.print("N=");Serial1.println(s.getDeviceCount());s.requestTemperatures();for(byte i=0;i<s.getDeviceCount();i++)Serial1.println(s.getTempCByIndex(i),1);w.reset();w.skip();w.write(0x4E);w.write(70);w.write(20);w.write(0x1F);w.reset();w.skip();w.write(0x48);delay(15);Serial1.println("DONE");}
            void loop(){}
            """;
        var image=await PicoCompiler.Compile(p.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);var text="";var writes=new List<OneWireMemoryWrite>();
        for(var i=0;i<400&&!text.Contains("DONE");i++){var f=await runner.Step(milliseconds:50,ds18b20Devices:devices);text+=f.SerialPorts[1];writes.AddRange(f.OneWireWrites??[]);Assert.Equal(8,f.Ds18b20!.Length);Assert.InRange((f.OneWireWrites??[]).Length,0,8);Assert.Empty(f.Diagnostics);}
        Assert.Contains("N=8\r\n",text);for(var i=10;i<18;i++)Assert.Contains(i+".0\r\n",text);Assert.Contains("DONE",text);Assert.Equal(8,writes.Select(w=>w.Id).Distinct().Count());Assert.All(writes,w=>Assert.Equal([70,20,31],w.Memory));
    }
}
