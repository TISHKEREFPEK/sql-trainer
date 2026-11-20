using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class SoftwareSpiTests
{
    public static CircuitProject Project(string board,bool hardwarePins=false)
    {
        var project=TftNativeTests.Project(board);project.Name="TFT · "+board+" · GPIO";var pico=board=="pico";
        var mosi=hardwarePins?(pico?19:board=="mega2560"?51:11):6;var sck=hardwarePins?(pico?18:board=="mega2560"?52:13):7;
        foreach(var (name,number) in new[]{("MOSI",mosi),("SCK",sck),("MISO",5)}){
            var index=project.Wires.FindIndex(w=>w.To==new Terminal("tft",name));project.Wires[index]=project.Wires[index] with{From=new("board",pico?PicoBoard.Gpio(number)!:"D"+number)};
        }
        return project;
    }
    [Theory]
    [InlineData("uno",false)]
    [InlineData("mega2560",false)]
    [InlineData("pico",false)]
    [InlineData("uno",true)]
    [InlineData("pico",true)]
    public async Task RealAdafruitSoftwareSpiPaintsAndReadsMisoUsingArbitraryGpio(string board,bool hardwarePins)
    {
        var project=Project(board,hardwarePins);var guide=CircuitCodeGuide.Build(project);Assert.NotNull(guide.Sketch);Assert.Contains("программный SPI",guide.Notes);Assert.DoesNotContain("&SPI",guide.Sketch);
        var devices=CircuitSpi.Devices(project);var config=Assert.Single(devices);Assert.False(CircuitSpi.Hardware(project.Parts[0],config));Assert.Equal(5,config.Miso);if(!hardwarePins)Assert.Equal((6,7),(config.Mosi,config.Sck));
        var pico=board=="pico";project.Code=$$"""
            #include <Adafruit_GFX.h>
            #include <Adafruit_ILI9341.h>
            Adafruit_ILI9341 screen({{config.Cs!.Pin}},{{config.Dc!.Pin}},{{config.Mosi}},{{config.Sck}},{{config.Rst!.Pin}},5);
            void setup(){
              {{(pico?"Serial1":"Serial")}}.begin(115200);screen.begin();screen.fillRect(1,2,3,4,ILI9341_RED);
              {{(pico?"Serial1":"Serial")}}.print(screen.readcommand8(0xd3,2),HEX);{{(pico?"Serial1":"Serial")}}.print(';');{{(pico?"Serial1":"Serial")}}.print(screen.readcommand8(0xd3,3),HEX);{{(pico?"Serial1":"Serial")}}.println(";DONE");
            }
            void loop(){}
            """;
        var output="";TftDisplayFrame? display=null;var samples=new List<ElectronicsSpiIo>();
        if(pico){var image=await PicoCompiler.Compile(project.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
            for(var i=0;i<180&&!output.Contains("DONE");i++){var frame=await runner.Step(spiDevices:devices);output+=frame.SerialPorts[1];display=Assert.Single(frame.TftDisplays!);samples.AddRange(frame.Spi??[]);Assert.Empty(frame.Diagnostics);}
        }else{var profile=AvrBoards.Get(board);var image=await FirmwareRunner.Compile(project.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(image,board);
            for(var i=0;i<180&&!output.Contains("DONE");i++){var frame=await runner.Step(new bool[profile.DigitalCount],new double[profile.AnalogCount],spiDevices:devices);output+=frame.Serial;display=Assert.Single(frame.TftDisplays!);samples.AddRange(frame.Spi??[]);}
        }
        Assert.Equal("93;41;DONE\r\n",output);Assert.NotNull(display);Assert.True(display.Display);var rgb=Convert.FromBase64String(display.Pixels);Assert.Equal(new byte[]{255,0,0},rgb.Skip((2*240+1)*3).Take(3));
        Assert.NotEmpty(samples);Assert.All(samples,t=>Assert.True(t.Software));Assert.Contains(samples,t=>t.Commands>0&&t.CommandPrefix.Contains(0xd3));
        var json=project.Serialize();var journal=ElectronicsJournal.Start(json,1);foreach(var sample in samples)journal=ElectronicsJournal.Record(journal,json,json,"spi-transfer",journal.Changes.Length+2,force:true,spiIo:[sample]);Assert.Equal(json,journal.Replay());
    }
}
