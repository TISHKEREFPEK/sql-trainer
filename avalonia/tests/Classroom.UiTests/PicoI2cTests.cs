using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoI2cTests
{
    [Fact]
    public async Task SimultaneousGpioBusesDrainWholePacketsAcrossBoundedFramesWithoutLoss()
    {
        const string code="""
            #include <hardware/gpio.h>
            #include <hardware/structs/sio.h>
            // The real SDK temporarily borrows GPIO15 for the USB erratum.
            const uint32_t clocks=0x37fe0; // twelve SCL lines: GPIO5..17 except 15
            void s(bool high){digitalWrite(4,high?HIGH:LOW);delayMicroseconds(5);}
            void c(bool high){if(high)sio_hw->gpio_oe_clr=clocks;else sio_hw->gpio_oe_set=clocks;delayMicroseconds(5);}
            bool put(byte b){for(int i=0;i<8;i++){s(b&128);c(true);c(false);b<<=1;}s(true);c(true);bool ack=!digitalRead(4);c(false);return ack;}
            void setup(){Serial1.begin(115200);pinMode(4,INPUT_PULLUP);for(int i=5;i<=17;i++)if(i!=15)pinMode(i,INPUT_PULLUP);Serial1.println("READY");}
            void loop(){if(Serial1.available()){Serial1.read();s(true);c(true);s(false);c(false);bool ok=put(0x4e);for(int i=0;i<4000;i++)ok=put(8)&&ok;s(false);c(true);s(true);Serial1.println(ok?"DONE":"NACK");}}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var devices=Enumerable.Range(5,13).Where(pin=>pin!=15).Select(pin=>new PicoI2cDevice("lcd"+pin,39,16,2,true,true,Scl:pin)).ToArray();var output="";
        for(var i=0;i<15&&!output.Contains("READY");i++)output+=(await runner.Step(devices:devices)).SerialPorts[1];Assert.Contains("READY",output);
        output="";var packets=new List<PicoI2cIo>();var drainedWithoutCpu=false;
        for(var i=0;i<300;i++){
            var frame=await runner.Step(serialInput:i==0?"p":null,serialPort:1,devices:devices);output+=frame.SerialPorts[1];packets.AddRange(frame.I2c);
            Assert.InRange(frame.I2c.Length,0,128);Assert.InRange(frame.I2c.Sum(p=>p.Data.Length),0,32768);
            if(frame.Instructions==0&&frame.I2c.Length>0)drainedWithoutCpu=true;
            if(output.Contains("DONE")&&packets.Count==12)break;
        }
        Assert.Contains("DONE",output);Assert.True(drainedWithoutCpu);Assert.Equal(12,packets.Count);
        Assert.All(packets,p=>{Assert.True(p.Software&&p.Ack,$"ACK={p.Ack}, software={p.Software}, address={p.Address}, bytes={p.Data.Length}, repeated={p.RepeatedStart}");Assert.Equal(4000,p.Data.Length);Assert.All(p.Data,b=>Assert.Equal(8,b));});
    }

    [Fact]
    public async Task ActualAdafruitBusIoAndGraphicsRunOfflineAlongsideLcdOnPico()
    {
        var code="#include <Adafruit_I2CDevice.h>\n"+OledTests.Project(mixed:true).Code.Replace("Serial.","Serial1.")
            .Replace("Wire.begin();","Wire.setSDA(4);Wire.setSCL(5);Wire.begin();Adafruit_I2CDevice check(0x3c,&Wire);Serial1.println(check.begin()?\"DETECT\":\"MISSING\");")
            .Replace("  delay(500);","  Serial1.println(\"READY\");delay(500);");
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        PicoI2cDevice[] devices=[new("lcd",39,16,2,true,true),new("oled",60,128,64,true,true,"ssd1306")];
        PicoFrame? frame=null;var output="";var trace=new List<PicoI2cIo>();
        for(var i=0;i<100;i++){frame=await runner.Step(devices:devices);output+=frame.SerialPorts[1];trace.AddRange(frame.I2c);if(output.Contains("READY"))break;}
        Assert.Contains("DETECT",output);Assert.Contains("READY",output);Assert.DoesNotContain("MISSING",output);
        var oled=Assert.Single(frame!.OledDisplays);Assert.True(oled.Display);Assert.True(OledTests.Pixel(oled,0,0));Assert.True(OledTests.Pixel(oled,127,63));Assert.True(OledTests.Pixel(oled,96,28));Assert.False(OledTests.Pixel(oled,64,32));
        Assert.Equal("LCD ready",new string(Assert.Single(frame.Displays).Cells.Take(9).Select(v=>(char)v).ToArray()));
        Assert.Contains(trace,t=>t.Software&&t.Address==60&&t.Ack&&t.Data.Length==0);Assert.Contains(trace,t=>!t.Software&&t.Address==60&&t.Data.Length>1&&t.Data[0]==64&&t.Ack);Assert.All(trace,t=>Assert.True(t.Ack));Assert.Empty(frame.Diagnostics);
    }

    [Fact]
    public async Task RealWireProbeUsesSioEdgesAndReturnsToHardwareI2c()
    {
        const string code="""
            #include <Wire.h>
            void probe(TwoWire& w,int address){w.beginTransmission(address);Serial1.print(w.endTransmission());Serial1.print(';');}
            void setup(){Serial1.begin(115200);Wire.setSDA(4);Wire.setSCL(5);Wire.begin();Wire1.setSDA(6);Wire1.setSCL(7);Wire1.begin();Serial1.println("READY");}
            void loop(){if(Serial1.available()){Serial1.read();probe(Wire,0x27);probe(Wire,0x26);probe(Wire,0x3c);probe(Wire1,0x27);Wire.beginTransmission(0x27);Wire.write(8);Serial1.print(Wire.endTransmission());Serial1.print(';');Serial1.print(Wire.requestFrom(0x27,1));Serial1.print(';');Serial1.println(Wire.available()?Wire.read():-1);}}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        PicoI2cDevice[] devices=[new("lcd",39,16,2,true,true),new("oled",60,128,64,true,true,"ssd1306"),new("other",39,16,2,true,true,Controller:1,Sda:6,Scl:7)];
        var trace=new List<PicoI2cIo>();
        async Task<string> Advance(PicoI2cDevice[] config,string? input=null,bool[]? digital=null){var output="";for(var i=0;i<30;i++){var f=await runner.Step(serialInput:i==0?input:null,serialPort:1,devices:config,digital:digital);output+=f.SerialPorts[1];trace.AddRange(f.I2c);if(output.Contains('\n'))break;}return output;}
        Assert.Contains("READY",await Advance(devices));Assert.Contains("0;2;0;0;0;1;8",await Advance(devices,"s"));
        Assert.Contains(trace,t=>t.Software&&t.Controller==0&&t.Address==39&&t.Ack&&t.Data.Length==0);
        Assert.Contains(trace,t=>t.Software&&t.Controller==1&&t.Address==39&&t.Ack);
        Assert.Contains(trace,t=>t.Software&&t.Address==38&&!t.Ack);
        Assert.Contains(trace,t=>!t.Software&&t.Read&&t.Data.SequenceEqual(new[]{8}));
        foreach(var missing in new[]{devices[0] with{Powered=false},devices[0] with{Connected=false},devices[0] with{Address=37},devices[0] with{Sda=8,Scl=9}})
            Assert.Contains("2;2;0;0;4;0;-1",await Advance([missing,devices[1],devices[2]],"s"));
        Assert.Contains("2;2;2;2;4;0;-1",await Advance([],"s"));
        Assert.Contains("0;2;0;0;0;1;8",await Advance(devices,"s"));
    }

    [Fact]
    public async Task IndependentOpenDrainFirmwareWritesAndReadsBytesThroughGpio()
    {
        const string code="""
            void s(bool high){digitalWrite(4,high?HIGH:LOW);delayMicroseconds(5);}
            void c(bool high){digitalWrite(5,high?HIGH:LOW);delayMicroseconds(5);}
            void start(){s(true);c(true);s(false);c(false);}
            void stop(){s(false);c(true);s(true);}
            bool put(byte b){for(int i=0;i<8;i++){s(b&128);c(true);c(false);b<<=1;}s(true);c(true);bool ack=!digitalRead(4);c(false);return ack;}
            byte get(bool more){byte b=0;s(true);for(int i=0;i<8;i++){c(true);b=(b<<1)|digitalRead(4);c(false);}s(!more);c(true);c(false);s(true);return b;}
            void setup(){Serial1.begin(115200);pinMode(4,INPUT_PULLUP);pinMode(5,INPUT_PULLUP);Serial1.println("READY");}
            void loop(){if(Serial1.available()){
              Serial1.read();start();bool a=put(0x4e),b=put(8);start();bool r=put(0x4f);byte x=get(true),y=get(false);stop();
              Serial1.print("ACK:");Serial1.print(a);Serial1.print(b);Serial1.print(r);Serial1.print(";DATA:");Serial1.print(x);Serial1.print(',');Serial1.println(y);
              start();put(0x78);put(0);put(0x8d);put(0x14);put(0xa5);put(0xaf);stop();
            }}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        PicoI2cDevice[] devices=[new("lcd",39,16,2,true,true),new("oled",60,128,64,true,true,"ssd1306")];var output="";var trace=new List<PicoI2cIo>();PicoFrame? frame=null;
        for(var i=0;i<15&&!output.Contains("READY");i++)output+=(await runner.Step(devices:devices)).SerialPorts[1];Assert.Contains("READY",output);
        output="";for(var i=0;i<12;i++){frame=await runner.Step(serialInput:i==0?"p":null,serialPort:1,devices:devices);output+=frame.SerialPorts[1];trace.AddRange(frame.I2c);}
        Assert.Contains("ACK:111;DATA:8,8",output);
        Assert.Contains(trace,t=>t.Software&&!t.Read&&t.Ack&&t.Data.SequenceEqual(new[]{8}));
        Assert.Contains(trace,t=>t.Software&&t.Read&&t.RepeatedStart&&t.Ack&&t.Data.SequenceEqual(new[]{8,8}));
        var oled=Assert.Single(frame!.OledDisplays);Assert.True(oled.Display);Assert.True(OledTests.Pixel(oled,0,0));Assert.Empty(frame.Diagnostics);
    }

    [Fact]
    public async Task RealWireWritesLcdAndOledAndReadsLcdThroughRepeatedStart()
    {
        var code=LcdI2cTests.Program.Replace("Serial.","Serial1.").Replace("Wire.begin();","Wire.setSDA(4);Wire.setSCL(5);Wire.begin();");
        var oled="""
            Wire.beginTransmission(0x3c);Wire.write(0);
            byte commands[]={0xae,0x8d,0x14,0xa1,0xc8,0xda,0x12,0x20,0,0x21,0,127,0x22,0,7,0xaf};
            for(byte c:commands)Wire.write(c);Wire.endTransmission();
            Wire.beginTransmission(0x3c);Wire.write(0x40);Wire.write(1);Wire.write(2);Wire.write(4);Wire.write(8);Wire.endTransmission();
            """;
        code=code.Replace("Serial1.println(\"READY\");",oled+"Serial1.println(\"READY\");");
        PicoI2cDevice[] devices=[new("lcd",39,20,4,true,true),new("oled",60,128,64,true,true,"ssd1306")];
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        PicoFrame? frame=null;var output="";var trace=new List<PicoI2cIo>();
        for(var i=0;i<80;i++){frame=await runner.Step(milliseconds:10,devices:devices);output+=frame.SerialPorts[1];trace.AddRange(frame.I2c);if(output.Contains("READY"))break;}
        Assert.Contains("READY",output);var lcd=Assert.Single(frame!.Displays);Assert.True(lcd.Display);Assert.True(lcd.Backlight);
        Assert.Equal("Hello Sql",new string(lcd.Cells.Take(9).Select(v=>(char)v).ToArray()));Assert.Equal('3',(char)lcd.Cells[40]);Assert.Equal('4',(char)lcd.Cells[60]);
        Assert.Equal(new[]{0,10,31,31,14,4,0,0},lcd.Cgram.Take(8));
        var display=Assert.Single(frame.OledDisplays);Assert.True(display.Display);Assert.True(OledTests.Pixel(display,0,0));Assert.True(OledTests.Pixel(display,3,3));
        for(var i=0;i<12;i++){frame=await runner.Step(serialInput:i==0?"r":null,serialPort:1,milliseconds:10,devices:devices);output+=frame.SerialPorts[1];trace.AddRange(frame.I2c);}
        Assert.Contains("4F\r\n8F",output);Assert.Contains(trace,t=>t.Controller==0&&t.Read&&t.RepeatedStart&&t.Ack);Assert.All(trace,t=>Assert.InRange(t.Data.Length,0,4096));Assert.Empty(frame.Diagnostics);

        // A later invalid descriptor must not power-cycle/reset the first LCD.
        foreach(var invalid in new[]{new[]{devices[0] with{Powered=false},devices[1] with{Kind="unknown"}},new[]{devices[0] with{Powered=false},devices[1] with{Sda=30}},new[]{devices[0] with{Powered=false},devices[0]}})
            await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Step(devices:invalid));
        frame=await runner.Step(devices:devices);Assert.Equal("Hello Sql",new string(frame.Displays[0].Cells.Take(9).Select(v=>(char)v).ToArray()));
    }

    const string TwoControllers="""
        #include <Wire.h>
        #include <hardware/gpio.h>
        #include <hardware/i2c.h>
        void transfer(TwoWire& wire,byte value){wire.beginTransmission(0x27);wire.write(value);Serial1.print(wire.endTransmission());Serial1.print(',');Serial1.print(wire.requestFrom(0x27,1));Serial1.print(',');Serial1.print(wire.available()?wire.read():-1);Serial1.print(';');}
        void setup(){Serial1.begin(115200);pinMode(25,OUTPUT);digitalWrite(25,LOW);Wire.setSDA(4);Wire.setSCL(5);Wire.begin();Wire1.setSDA(6);Wire1.setSCL(7);Wire1.begin();Serial1.println("READY");}
        void loop(){if(Serial1.available())switch(Serial1.read()){
          case 'p':transfer(Wire,8);transfer(Wire1,9);Serial1.println();break;
          case 'm':gpio_set_function(4,GPIO_FUNC_SIO);transfer(Wire,8);gpio_set_function(4,GPIO_FUNC_I2C);Serial1.println();break;
          case 'f':Wire.setClock(400000);transfer(Wire,8);Serial1.println();break;
          case 'h':{
            // Mark only after hardware START is queued, before the address ACK.
            i2c0->hw->enable=0;i2c0->hw->tar=0x27;i2c0->hw->enable=1;i2c0->hw->data_cmd=0x208;gpio_put(25,true);
            while(!(i2c0->hw->raw_intr_stat&0x10)){}
            auto reason=i2c0->hw->tx_abrt_source;auto clear=i2c0->hw->clr_tx_abrt;
            while(!(i2c0->hw->raw_intr_stat&0x200)){}clear=i2c0->hw->clr_stop_det;
            gpio_put(25,false);Serial1.print("HOT:");Serial1.println(reason&0x1ffff);break;
          }
        }}
        """;

    [Fact]
    public async Task SeparateControllersNackDisconnectedOrWrongPinsAndKeepTheirOwnLatches()
    {
        var image=await PicoCompiler.Compile(TwoControllers,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        PicoI2cDevice[] both=[new("first",39,16,2,true,true),new("second",39,16,2,true,true,Controller:1,Sda:6,Scl:7)];
        var trace=new List<PicoI2cIo>();
        async Task<string> Advance(PicoI2cDevice[] devices,string? command=null){var output="";for(var i=0;i<12;i++){var f=await runner.Step(serialInput:i==0?command:null,serialPort:1,devices:devices);output+=f.SerialPorts[1];trace.AddRange(f.I2c);if(output.Contains('\n'))break;}return output;}
        Assert.Contains("READY",await Advance(both));
        Assert.Contains("0,1,8;0,1,9;",await Advance(both,"p"));
        foreach(var changed in new[]{both[0] with{Powered=false},both[0] with{Connected=false},both[0] with{Address=38},both[0] with{Sda=8,Scl=9},both[0] with{Sda=5,Scl=4},both[0] with{Controller=1}})
            Assert.Contains("4,0,-1;0,1,9;",await Advance([changed,both[1]],"p"));
        Assert.Contains("4,0,-1;0,1,9;",await Advance([both[1]],"p"));
        PicoFrame? marker=null;
        for(var i=0;i<500;i++){marker=await runner.Step(serialInput:i==0?"h":null,serialPort:1,milliseconds:.001,devices:both);if(marker.Pins[25].Voltage>1)break;}
        Assert.True(marker!.Pins[25].Voltage>1,"Firmware did not queue the hot-disconnect transaction");
        await runner.Step(milliseconds:.02,devices:both);
        Assert.Contains("HOT:1\r\n",await Advance([both[0] with{Address=38},both[1]]));
        Assert.Contains("4,0,-1;",await Advance(both,"m"));
        Assert.Contains("0,1,8;0,1,9;",await Advance(both,"p"));
        Assert.Contains(trace,t=>t.Controller==0&&t.Address==39&&t.Ack&&t.Read&&t.Data.SequenceEqual(new[]{8}));
        Assert.Contains(trace,t=>t.Controller==1&&t.Address==39&&t.Ack&&t.Read&&t.Data.SequenceEqual(new[]{9}));
        Assert.Contains(trace,t=>t.Controller==0&&!t.Ack);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Step(serialInput:"f",serialPort:1,devices:both));Assert.Contains("100",error.Message);
    }

    [Fact]
    public async Task OledFastModeAcceptsWritesAndNacksReadsAndRejectsOverclocking()
    {
        const string code="""
            #include <Wire.h>
            void setup(){Serial1.begin(115200);Wire.setSDA(4);Wire.setSCL(5);Wire.begin();Wire.setClock(400000);Serial1.println("READY");}
            void loop(){if(Serial1.available()){char c=Serial1.read();if(c=='f')Wire.setClock(1000000);Wire.beginTransmission(0x3c);Wire.write(0);Wire.write(0xae);Serial1.print(Wire.endTransmission());Serial1.print(',');Serial1.println(Wire.requestFrom(0x3c,1));}}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        PicoI2cDevice[] devices=[new("oled",60,128,64,true,true,"ssd1306")];var output="";var trace=new List<PicoI2cIo>();
        for(var i=0;i<15&&!output.Contains("READY");i++)output+=(await runner.Step(devices:devices)).SerialPorts[1];Assert.Contains("READY",output);
        output="";for(var i=0;i<8;i++){var f=await runner.Step(serialInput:i==0?"p":null,serialPort:1,devices:devices);output+=f.SerialPorts[1];trace.AddRange(f.I2c);}
        Assert.Contains("0,0",output);Assert.Contains(trace,t=>!t.Read&&t.Ack&&t.Data.SequenceEqual(new[]{0,0xae}));Assert.Contains(trace,t=>t.Read&&!t.Ack);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Step(serialInput:"f",serialPort:1,devices:devices));Assert.Contains("400",error.Message);
    }
}
