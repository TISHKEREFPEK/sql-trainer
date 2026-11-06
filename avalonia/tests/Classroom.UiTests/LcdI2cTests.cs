using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class LcdI2cTests
{
    const string Program="""
        #include <Wire.h>
        void pins(byte b,bool repeated=false){Wire.beginTransmission(0x27);Wire.write(b);Wire.endTransmission(!repeated);}
        void nibble(byte b,bool data=false){byte p=(b<<4)|8|(data?1:0);pins(p);pins(p|4);pins(p);}
        void send(byte b,bool data=false){nibble(b>>4,data);nibble(b&15,data);delayMicroseconds(50);}
        byte readPins(){Wire.requestFrom(0x27,1);return Wire.available()?Wire.read():0;}
        void setup(){
          Serial.begin(115200);Wire.begin();delay(50);
          nibble(3);delay(5);nibble(3);delayMicroseconds(200);nibble(3);delayMicroseconds(200);nibble(2);
          send(0x28);send(0x0E);send(1);delay(2);send(6);
          const char* text="Hello Sql";while(*text)send(*text++,true);
          send(0x40);byte glyph[]={0,10,31,31,14,4,0,0};for(byte i=0;i<8;i++)send(glyph[i],true);
          send(0x94);send('3',true);send(0xD4);send('4',true);
          send(0xC0);send(0,true);Serial.println("READY");
        }
        void loop(){if(Serial.available())switch(Serial.read()){
          case 's':send(0x18);Serial.println("SHIFT");break;
          case 'c':send(1);delay(2);Serial.println("CLEAR");break;
          case 'o':send(8);Serial.println("OFF");break;
          case 'h':send(2);delay(2);Serial.println("HOME");break;
          case 'b':pins(0);Serial.println("DARK");break;
          case 'r':send(0x80);pins(0xFB);pins(0xFF,true);Serial.println(readPins(),HEX);pins(0xFB);pins(0xFF,true);Serial.println(readPins(),HEX);pins(0xFB);break;
          case 'q':send(1);pins(0xFA);pins(0xFE,true);Serial.print("BUSY:");Serial.println(readPins(),HEX);pins(0xFA);pins(0xFE,true);readPins();pins(0xFA);delay(2);break;
          case 'f':send(0x2C);break;
          case 't':send(0x20);break;
        }}
        """;
    public static CircuitProject Project(string board="uno",bool large=false)
    {
        var mega=board=="mega2560";var project=new CircuitProject{Name="LCD · I2C",Code=Program};
        project.Parts.Add(new("mcu",mega?PartKind.Mega:PartKind.Uno,40,60,ModelId:mega?"mega2560":"uno"));
        project.Parts.Add(new("lcd",PartKind.LcdI2c,680,120,39,ModelId:large?"lcd-i2c-20x4":"lcd-i2c-16x2"));
        var index=0;foreach(var pair in new[]{("5V","VCC"),("GND","GND"),(mega?"D20":"A4","SDA"),(mega?"D21":"A5","SCL")})project.Wires.Add(new("w"+index++,new("mcu",pair.Item1),new("lcd",pair.Item2)));
        return project;
    }
    static bool[] Digital(string board)=>new bool[AvrBoards.Get(board).DigitalCount];
    static double[] Analog(string board)=>new double[AvrBoards.Get(board).AnalogCount];
    [Theory]
    [InlineData("uno",false)]
    [InlineData("mega2560",true)]
    public async Task RealWireFirmwareDrivesLcdMemoryReadbackCgramAndControls(string board,bool large)
    {
        var project=Project(board,large);var devices=CircuitI2c.Devices(project);var device=Assert.Single(devices);Assert.True(device.Powered);Assert.True(device.Connected);
        var hex=await FirmwareRunner.Compile(project.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(hex,board);
        FirmwareFrame? frame=null;var serial="";var trace=new List<ElectronicsI2cIo>();
        async Task Advance(string? input=null){for(var i=0;i<4;i++){frame=await runner.Step(Digital(board),Analog(board),i==0?input:null,devices:devices);serial+=frame.Serial;trace.AddRange(frame.I2c??[]);}}
        await Advance();Assert.Contains("READY",serial);var lcd=Assert.Single(frame!.Displays!);Assert.True(lcd.Display);Assert.True(lcd.Backlight);
        Assert.Equal("Hello Sql",new string(lcd.Cells.Take(9).Select(v=>(char)v).ToArray()));Assert.Equal(0,lcd.Cells[device.Columns]);Assert.Equal(new[]{0,10,31,31,14,4,0,0},lcd.Cgram.Take(8));Assert.Equal(device.Columns+1,lcd.Cursor);
        if(large){Assert.Equal('3',(char)lcd.Cells[40]);Assert.Equal('4',(char)lcd.Cells[60]);}
        await Advance("r");Assert.Contains("4F\r\n8F",serial);Assert.Contains(trace,t=>t.Read&&t.RepeatedStart&&t.Ack);
        await Advance("s");Assert.Equal('e',(char)frame!.Displays![0].Cells[0]);
        await Advance("o");Assert.False(frame!.Displays![0].Display);Assert.Equal('e',(char)frame.Displays[0].Cells[0]);
        await Advance("h");Assert.Equal('H',(char)frame!.Displays![0].Cells[0]);Assert.False(frame.Displays[0].Display);
        await Advance("c");Assert.All(frame!.Displays![0].Cells,v=>Assert.Equal(32,v));
        await Advance("q");Assert.Contains("BUSY:8E",serial);
        await Advance("b");Assert.False(frame!.Displays![0].Backlight);
        Assert.All(trace,t=>Assert.InRange(t.Data.Length,0,4096));
        Assert.Contains("5x10",(await Assert.ThrowsAsync<InvalidDataException>(()=>Advance("f"))).Message);
        using var singleLine=new FirmwareRunner();await singleLine.Start(hex,board);for(var i=0;i<4;i++)await singleLine.Step(Digital(board),Analog(board),devices:devices);
        Assert.Contains("однострочный",(await Assert.ThrowsAsync<InvalidDataException>(async()=>{for(var i=0;i<3;i++)await singleLine.Step(Digital(board),Analog(board),i==0?"t":null,devices:devices);})).Message);
    }
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    public async Task MissingWrongAndUnpoweredDevicesNackAndBareSpiCompletes(string board)
    {
        const string code="#include <Wire.h>\n#include <SPI.h>\nvoid setup(){Serial.begin(9600);Wire.begin();SPI.begin();Serial.print(\"SPI:\");Serial.println(SPI.transfer(90));unsigned long t=micros();Wire.beginTransmission(0x27);for(byte i=0;i<4;i++)Wire.write(8);byte result=Wire.endTransmission();unsigned long elapsed=micros()-t;Serial.print(\"BUS:\");Serial.println(result);Serial.print(\"TIME:\");Serial.println(elapsed);}void loop(){}";
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None,board);var original=Assert.Single(CircuitI2c.Devices(Project(board)));
        foreach(var devices in new[]{Array.Empty<I2cDevice>(),new[]{original with{Powered=false}},new[]{original with{Connected=false}},new[]{original with{Address=38}},new[]{original}})
        {
            using var runner=new FirmwareRunner();await runner.Start(hex,board);var serial="";var trace=new List<ElectronicsI2cIo>();
            for(var i=0;i<3;i++){var frame=await runner.Step(Digital(board),Analog(board),devices:devices);serial+=frame.Serial;trace.AddRange(frame.I2c??[]);}
            Assert.Contains("SPI:255",serial);Assert.Contains("BUS:"+(devices.Length==1&&devices[0]==original?0:2),serial);Assert.Single(trace);
            if(devices.Length==1&&devices[0]==original){var time=int.Parse(System.Text.RegularExpressions.Regex.Match(serial,@"TIME:(\d+)").Groups[1].Value);Assert.InRange(time,470,650);Assert.Equal(new[]{8,8,8,8},trace[0].Data);}
        }
        var fast=await FirmwareRunner.Compile(code.Replace("Wire.begin();","Wire.begin();Wire.setClock(400000);"),CancellationToken.None,board);using var fastRunner=new FirmwareRunner();await fastRunner.Start(fast,board);
        Assert.Contains("100",(await Assert.ThrowsAsync<InvalidDataException>(async()=>{for(var i=0;i<3;i++)await fastRunner.Step(Digital(board),Analog(board),devices:[original]);})).Message);
    }
    [Fact]
    public void WiringValidationAndLegacySnapshotsAreExplicit()
    {
        var project=Project();var original=project.Serialize();ElectronicsProjectFormat.Inspect(original);Assert.Equal(original,CircuitProject.Parse(original).Serialize());
        foreach(var pin in new[]{"VCC","GND","SDA","SCL"})
        {var broken=CircuitProject.Parse(original);broken.Wires.RemoveAll(w=>w.To.Pin==pin);var config=Assert.Single(CircuitI2c.Devices(broken));Assert.False(config.Powered&&config.Connected);}
        var shorted=CircuitProject.Parse(original);shorted.Wires.Add(new("short",new("lcd","SDA"),new("lcd","SCL")));Assert.False(CircuitI2c.Devices(shorted)[0].Connected);
        var grounded=CircuitProject.Parse(original);grounded.Wires.Add(new("short",new("lcd","SDA"),new("lcd","GND")));Assert.False(CircuitI2c.Devices(grounded)[0].Connected);
        var low=CircuitProject.Parse(original);low.Parts.Add(new("supply",PartKind.Battery,100,400,3.3));low.Wires[0]=low.Wires[0] with{From=new("supply","+")};low.Wires.Add(new("g",new("supply","−"),new("mcu","GND")));Assert.False(CircuitI2c.Devices(low)[0].Powered);
        Assert.Contains("lcdBegin(0x27)",CircuitCodeGuide.Build(project).Sketch);project.Wires.RemoveAt(0);Assert.Null(CircuitCodeGuide.Build(project).Sketch);
        var legacy=PartCatalog.All.Single(p=>p.ReferenceName=="16x2 I2C LCD");Assert.Equal(PartKind.Visual,legacy.Kind);Assert.False(legacy.Simulated);
        var journal=ElectronicsJournal.Start(original,1);journal=ElectronicsJournal.Record(journal,original,original,"i2c-transfer",2,force:true,i2cIo:[new(39,false,[8,12,8],true,false)]);Assert.Equal(original,Wire.Read<ElectronicsJournal>(Wire.Write(journal)).Replay());
        Assert.Throws<InvalidDataException>(()=>(journal with{Changes=[journal.Changes[0] with{I2cIo=[new(39,false,[256],true,false)]}]}).Replay());
    }
}
