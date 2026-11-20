using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class OledTests
{
    public static CircuitProject Project(string board="uno",bool mixed=false,bool lowVoltage=false)
    {
        var mega=board=="mega2560";var project=mixed?LcdI2cTests.Project(board):new CircuitProject{Name="OLED · SSD1306"};
        if(!mixed)project.Parts.Add(new("mcu",mega?PartKind.Mega:PartKind.Uno,40,80,ModelId:mega?"mega2560":"uno"));
        project.Parts.Add(new("oled",PartKind.OledI2c,720,mixed?380:120,60,ModelId:"oled-ssd1306-i2c"));var index=project.Wires.Count;
        foreach(var pair in new[]{(lowVoltage?"3V3":"5V","VDD"),("GND","GND"),(mega?"D20":"A4","SDA"),(mega?"D21":"A5","SCL")})project.Wires.Add(new("w"+index++,new("mcu",pair.Item1),new("oled",pair.Item2)));
        project.Code=CircuitCodeGuide.Build(project).Sketch!;return project;
    }
    static bool[] Digital(string board)=>new bool[AvrBoards.Get(board).DigitalCount];
    static double[] Analog(string board)=>new double[AvrBoards.Get(board).AnalogCount];
    public static bool Pixel(OledFrame frame,int x,int y)=>(Convert.FromBase64String(frame.Pixels)[(y>>3)*128+x]&(1<<(y&7)))!=0;
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    public async Task ActualAdafruitGraphicsAndLcdCoexistOnOnePhysicalBus(string board)
    {
        var project=Project(board,true,board=="mega2560");var json=project.Serialize();ElectronicsProjectFormat.Inspect(json);var devices=CircuitI2c.Devices(project);Assert.Equal(2,devices.Length);Assert.All(devices,d=>{Assert.True(d.Powered);Assert.True(d.Connected);});
        var hex=await FirmwareRunner.Compile(project.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(hex,board);
        FirmwareFrame? frame=null;var packets=new List<ElectronicsI2cIo>();var output="";
        for(var i=0;i<12;i++){frame=await runner.Step(Digital(board),Analog(board),devices:devices);packets.AddRange(frame.I2c??[]);output+=frame.Serial;}
        var oled=Assert.Single(frame!.OledDisplays!);Assert.True(oled.Display,output);Assert.Equal(1024,Convert.FromBase64String(oled.Memory).Length);Assert.True(Pixel(oled,0,0));Assert.True(Pixel(oled,127,63));Assert.True(Pixel(oled,96,28));Assert.False(Pixel(oled,64,32));
        Assert.Contains(Convert.FromBase64String(oled.Pixels),b=>b!=0);Assert.Equal("LCD ready",new string(Assert.Single(frame.Displays!).Cells.Take(9).Select(v=>(char)v).ToArray()));
        Assert.Contains(packets,p=>p.Address==39&&p.Ack);Assert.Contains(packets,p=>p.Address==60&&p.Ack);Assert.All(packets,p=>Assert.True(p.Ack));
    }
    const string Commands="""
        #include <Wire.h>
        void cmd(byte a){Wire.beginTransmission(0x3D);Wire.write(0);Wire.write(a);Wire.endTransmission();}
        void pair(byte a,byte b){Wire.beginTransmission(0x3D);Wire.write(0);Wire.write(a);Wire.write(b);Wire.endTransmission();}
        void range(byte a,byte b,byte c){Wire.beginTransmission(0x3D);Wire.write(0x80);Wire.write(a);Wire.write(0x80);Wire.write(b);Wire.write(0);Wire.write(c);Wire.endTransmission();}
        void setup(){Serial.begin(115200);Wire.begin();Wire.setClock(400000);cmd(0xAE);pair(0x8D,0x14);cmd(0xA1);cmd(0xC8);pair(0xDA,0x12);pair(0x20,MODE);range(0x21,10,11);range(0x22,2,3);
          Wire.beginTransmission(0x3D);Wire.write(0x40);Wire.write(1);Wire.write(2);Wire.write(4);Wire.write(8);Wire.endTransmission();cmd(0xAF);Serial.println("READY");}
        void loop(){if(Serial.available())switch(Serial.read()){
          case 'i':cmd(0xA7);break;case 'n':cmd(0xA6);break;case 'o':cmd(0xAE);break;case 'f':cmd(0xAF);break;
          case 'a':cmd(0xA5);break;case 'r':cmd(0xA4);break;case 'm':cmd(0xA0);break;case 'v':cmd(0xC0);break;
          case 's':cmd(0x41);break;case 'd':pair(0xD3,1);break;case 'c':pair(0x81,16);break;
          case 'q':pair(0x8D,0x10);break;case 'p':pair(0x8D,0x14);break;
          case 'j':cmd(0xA1);cmd(0xC8);pair(0xDA,2);break;case 'k':pair(0xDA,0x32);break;case 'l':pair(0xDA,0x12);break;
          case 'u':pair(0xA8,31);cmd(0xA5);break;case 'w':pair(0xA8,63);cmd(0xA4);break;
          case 'h':Wire.beginTransmission(0x3D);Wire.write(0);Wire.write(0x26);Wire.write(0);Wire.write(2);Wire.write(7);Wire.write(3);Wire.write(0);Wire.write(255);Wire.write(0x2F);Wire.endTransmission();break;
          case 't':cmd(0x2E);break;
          case 'z':cmd(0x2E);Wire.beginTransmission(0x3D);Wire.write(0);Wire.write(0xA3);Wire.write(16);Wire.write(32);Wire.write(0x29);Wire.write(0);Wire.write(2);Wire.write(7);Wire.write(3);Wire.write(1);Wire.write(0x2F);Wire.endTransmission();break;
        }}
        """;
    [Theory]
    [InlineData("uno",0)]
    [InlineData("mega2560",1)]
    [InlineData("uno",2)]
    public async Task RawWireControlsAddressingOrientationDisplayAndScrolling(string board,int mode)
    {
        var project=Project(board);var device=Assert.Single(CircuitI2c.Devices(project)) with{Address=61};var code=Commands.Replace("MODE",mode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(hex,board);FirmwareFrame? frame=null;
        async Task Advance(string? command=null){for(var i=0;i<3;i++)frame=await runner.Step(Digital(board),Analog(board),i==0?command:null,devices:[device]);}
        await Advance();var original=frame!.OledDisplays![0];var bytes=Convert.FromBase64String(original.Memory);Assert.Equal(1,bytes[266]);
        if(mode==0){Assert.Equal(2,bytes[267]);Assert.Equal(4,bytes[394]);Assert.Equal(8,bytes[395]);}
        if(mode==1){Assert.Equal(2,bytes[394]);Assert.Equal(4,bytes[267]);Assert.Equal(8,bytes[395]);}
        if(mode==2){Assert.Equal(2,bytes[267]);Assert.Equal(4,bytes[268]);Assert.Equal(8,bytes[269]);}
        Assert.True(Pixel(original,10,16));await Advance("i");Assert.False(Pixel(frame!.OledDisplays![0],10,16));Assert.True(Pixel(frame.OledDisplays[0],0,0));
        await Advance("a");Assert.All(Convert.FromBase64String(frame!.OledDisplays![0].Pixels),b=>Assert.Equal(255,b));await Advance("r");await Advance("n");
        await Advance("m");Assert.True(Pixel(frame!.OledDisplays![0],117,16));await Advance("v");Assert.True(Pixel(frame!.OledDisplays![0],117,47));
        await Advance("o");Assert.All(Convert.FromBase64String(frame!.OledDisplays![0].Pixels),b=>Assert.Equal(0,b));Assert.Equal(original.Memory,frame.OledDisplays[0].Memory);await Advance("f");
        await Advance("s");Assert.True(Pixel(frame!.OledDisplays![0],117,48));await Advance("d");Assert.True(Pixel(frame!.OledDisplays![0],117,47));await Advance("c");Assert.Equal(16,frame!.OledDisplays![0].Contrast);
        await Advance("q");Assert.False(frame!.OledDisplays![0].Display);await Advance("p");Assert.True(frame!.OledDisplays![0].Display);
        await Advance("j");Assert.True(Pixel(frame!.OledDisplays![0],10,32));await Advance("k");Assert.True(Pixel(frame!.OledDisplays![0],10,17));await Advance("l");Assert.True(Pixel(frame!.OledDisplays![0],10,16));
        await Advance("u");for(var y=0;y<64;y++)Assert.Equal(y<32,Pixel(frame!.OledDisplays![0],64,y));await Advance("w");
        var before=frame.OledDisplays[0].Pixels;await Advance("h");Assert.NotEqual(before,frame!.OledDisplays![0].Pixels);await Advance("t");var stopped=frame!.OledDisplays![0].Pixels;await Advance();Assert.Equal(stopped,frame!.OledDisplays![0].Pixels);Assert.Equal(original.Memory,frame.OledDisplays[0].Memory);
        await Advance("z");var diagonal=frame!.OledDisplays![0];var lit=Enumerable.Range(0,64).SelectMany(y=>Enumerable.Range(0,128).Where(x=>Pixel(diagonal,x,y)).Select(x=>y)).ToArray();Assert.Equal(4,lit.Length);Assert.All(lit,y=>Assert.InRange(y,16,47));Assert.NotEqual(stopped,diagonal.Pixels);
    }
    [Fact]
    public void WiringAddressAndLegacyModelsRemainExplicit()
    {
        var project=Project("mega2560",lowVoltage:true);var original=project.Serialize();Assert.Equal(original,CircuitProject.Parse(original).Serialize());Assert.True(CircuitI2c.Devices(project)[0].Powered);
        foreach(var pin in new[]{"VDD","GND","SDA","SCL"}){var broken=CircuitProject.Parse(original);broken.Wires.RemoveAll(w=>w.To.Pin==pin);var device=Assert.Single(CircuitI2c.Devices(broken));Assert.False(device.Powered&&device.Connected);}
        var invalid=CircuitProject.Parse(original);invalid.Parts[^1]=invalid.Parts[^1] with{Value=62};Assert.Throws<InvalidDataException>(invalid.Validate);
        var legacy=PartCatalog.All.Single(p=>p.ReferenceName=="OLED 128x64 I2C Monochrome Display VDD-GND");Assert.Equal(PartKind.Visual,legacy.Kind);Assert.False(legacy.Simulated);
    }
    [Fact]
    public async Task AbsentWrongAndReadOnlyTransactionsDoNotProduceFictionalPixels()
    {
        const string code="#include <Wire.h>\nvoid setup(){Serial.begin(9600);Wire.begin();Wire.beginTransmission(0x3C);Wire.write(0);Wire.write(0xAF);Serial.print(\"ACK:\");Serial.println(Wire.endTransmission());Serial.print(\"READ:\");Serial.println(Wire.requestFrom((byte)0x3C,(byte)1));}void loop(){}";
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None);var original=CircuitI2c.Devices(Project())[0];
        foreach(var devices in new[]{Array.Empty<I2cDevice>(),new[]{original with{Address=61}},new[]{original with{Powered=false}},new[]{original with{Connected=false}},new[]{original}})
        {
            using var runner=new FirmwareRunner();await runner.Start(hex);var output="";
            for(var i=0;i<3;i++){var frame=await runner.Step(Digital("uno"),Analog("uno"),devices:devices);output+=frame.Serial;Assert.All(frame.OledDisplays??[],d=>Assert.All(Convert.FromBase64String(d.Pixels),b=>Assert.Equal(0,b)));}
            Assert.Contains("READ:0",output);Assert.Contains("ACK:"+(devices.Length==1&&devices[0]==original?0:2),output);
        }
        foreach(var change in new[]{("Wire.begin();","Wire.begin();Wire.setClock(800000);","400"),("Wire.write(0);","Wire.write(3);","управляющий"),("Wire.write(0xAF);","Wire.write(0xD5);Wire.write(0xF0);","D5=0x8n")})
        {
            var invalid=await FirmwareRunner.Compile(code.Replace(change.Item1,change.Item2),CancellationToken.None);using var runner=new FirmwareRunner();await runner.Start(invalid);
            Assert.Contains(change.Item3,(await Assert.ThrowsAsync<InvalidDataException>(async()=>{for(var i=0;i<3;i++)await runner.Step(Digital("uno"),Analog("uno"),devices:[original]);})).Message);
        }
    }
}
