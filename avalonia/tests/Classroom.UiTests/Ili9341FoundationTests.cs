using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class Ili9341FoundationTests
{
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    [InlineData("pico")]
    public async Task ActualHardwareSpiPaintsTheControllerAndReadsItsId(string board)
    {
        var pico=board=="pico";var mega=board=="mega2560";
        var cs=pico?17:mega?53:10;var dc=pico?20:mega?49:9;var rst=pico?21:mega?48:8;
        var mosi=pico?19:mega?51:11;var sck=pico?18:mega?52:13;var miso=pico?16:mega?50:12;
        var code=$$"""
            #include <Adafruit_GFX.h>
            #include <Adafruit_ILI9341.h>
            Adafruit_ILI9341 screen({{cs}},{{dc}},{{rst}});
            void setup(){
              {{(pico?"SPI.setSCK(18);SPI.setTX(19);SPI.setRX(16);":"")}}
              Serial.begin(115200);screen.begin(4000000);screen.fillRect(1,2,3,4,ILI9341_RED);
              Serial.print(screen.readcommand8(0xd3,2),HEX);Serial.print(';');Serial.print(screen.readcommand8(0xd3,3),HEX);Serial.println(";DONE");
            }
            void loop(){}
            """;
        if(pico)code=code.Replace("Serial.","Serial1.");
        SpiDisplayDevice[] devices=[new("tft",0,mosi,sck,miso,new(cs),new(dc),new(rst),new(Level:true))];
        var output="";TftDisplayFrame? display=null;var samples=new List<Classroom.Contracts.ElectronicsSpiIo>();
        if(pico){
            var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
            for(var i=0;i<110&&!output.Contains("DONE");i++){var frame=await runner.Step(spiDevices:devices);output+=frame.SerialPorts[1];display=Assert.Single(frame.TftDisplays!);Assert.Empty(frame.Diagnostics);samples.AddRange(frame.Spi??[]);}
        }else{
            var image=await FirmwareRunner.Compile(code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(image,board);var profile=AvrBoards.Get(board);
            for(var i=0;i<110&&!output.Contains("DONE");i++){var frame=await runner.Step(new bool[profile.DigitalCount],new double[profile.AnalogCount],spiDevices:devices);output+=frame.Serial;display=Assert.Single(frame.TftDisplays!);samples.AddRange(frame.Spi??[]);}
        }
        Assert.Equal("93;41;DONE\r\n",output);Assert.NotNull(display);Assert.True(display.Display);var pixels=Convert.FromBase64String(display.Pixels);
        Assert.Equal(new byte[]{255,0,0},pixels.Skip((2*240+1)*3).Take(3));Assert.Equal(new byte[]{0,0,0},pixels.Take(3));
        Assert.Contains(samples,t=>t.Commands>0&&t.CommandPrefix.Contains(0xd3));Assert.Contains(samples,t=>t.DataBytes>0);var project=TftNativeTests.Project(board);project.Code=code;var json=project.Serialize();var journal=Classroom.Contracts.ElectronicsJournal.Start(json,1);
        foreach(var sample in samples)journal=Classroom.Contracts.ElectronicsJournal.Record(journal,json,json,"spi-transfer",journal.Changes.Length+2,force:true,spiIo:[sample]);Assert.Equal(json,journal.Replay());
    }
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    [InlineData("pico")]
    public async Task RealPinnedAdafruitDisplayLibraryCompilesInTheOfflineContainer(string board)
    {
        const string code="""
            #include <Adafruit_GFX.h>
            #include <Adafruit_ILI9341.h>
            Adafruit_ILI9341 screen(10,9,8);
            void setup(){screen.begin(8000000);screen.fillRect(1,2,3,4,ILI9341_RED);screen.setRotation(1);screen.setCursor(2,3);screen.print("TFT");}
            void loop(){}
            """;
        if(board=="pico")Assert.NotEmpty(await PicoCompiler.Compile(code,CancellationToken.None));
        else Assert.NotEmpty(await FirmwareRunner.Compile(code,CancellationToken.None,board));
    }
}
