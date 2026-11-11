using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class Dht11Tests
{
    public static CircuitProject Project(string board)
    {
        var pico=board=="pico";var project=new CircuitProject{Name="DHT11 · "+board};
        project.Parts.AddRange([new("board",pico?PartKind.Pico:board=="mega2560"?PartKind.Mega:PartKind.Uno,60,60,ModelId:pico?PicoBoard.ModelId:board),new("sensor",PartKind.Dht11,650,90,24,ModelId:CircuitDht11.ModelId,Setting:33.0/70),new("pullup",PartKind.Resistor,450,100,4700,ModelId:"resistor-4700")]);
        var supply=pico?PicoBoard.V3:"5V";
        project.Wires.AddRange([new("v",new("board",supply),new("sensor","VDD")),new("g",new("board",pico?PicoBoard.Ground:"GND"),new("sensor","GND")),new("d",new("board",pico?PicoBoard.Gpio(6)!:"D6"),new("sensor","DATA")),new("r1",new("sensor","DATA"),new("pullup","1")),new("r2",new("board",supply),new("pullup","2"))]);
        return project;
    }
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    [InlineData("pico")]
    public async Task RealDhtLibraryReadsActualGpioAndPowerLossTimesOut(string board)
    {
        var project=Project(board);var generated=CircuitCodeGuide.Build(project);Assert.NotNull(generated.Sketch);Assert.Contains("#include <DHT.h>",generated.Sketch);
        var d=Assert.Single(CircuitDht11.Devices(project));Assert.True(d.Powered&&d.Connected&&d.Pullup);Assert.Equal((6,24,53),(d.Pin,d.Temperature,d.Humidity));
        var pico=board=="pico";var serial=pico?"Serial1":"Serial";
        project.Code=$$"""
            #include <DHT.h>
            DHT sensor(6,DHT11);
            void setup(){ {{serial}}.begin(115200); sensor.begin(); delay(1100); }
            void loop(){
              float h=sensor.readHumidity(true),t=sensor.readTemperature(false);
              if(isnan(h)||isnan(t)) {{serial}}.println("READ_ERROR");
              else { {{serial}}.print(h,0);{{serial}}.print(';');{{serial}}.println(t,0); }
              delay(2100);
            }
            """;
        var output="";var unpowered=false;
        if(pico){
            var image=await PicoCompiler.Compile(project.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
            for(var i=0;i<650&&!output.Contains("READ_ERROR");i++){
                var frame=await runner.Step(milliseconds:50,dht11Devices:[unpowered?d with{Powered=false}:d]);output+=frame.SerialPorts[1];Assert.Empty(frame.Diagnostics);if(output.Contains("53;24"))unpowered=true;
            }
        }else{
            var profile=AvrBoards.Get(board);var image=await FirmwareRunner.Compile(project.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(image,board);
            for(var i=0;i<100&&!output.Contains("READ_ERROR");i++){var frame=await runner.Step(new bool[profile.DigitalCount],new double[profile.AnalogCount],dht11Devices:[unpowered?d with{Powered=false}:d]);output+=frame.Serial;if(output.Contains("53;24"))unpowered=true;}
        }
        Assert.StartsWith("53;24\r\n",output);Assert.Contains("READ_ERROR",output);
    }
    [Fact]
    public void PhysicalWiringValidationAndOldVisualProjectStayDistinct()
    {
        var p=Project("pico");var json=p.Serialize();Assert.Equal(json,CircuitProject.Parse(json).Serialize());ElectronicsProjectFormat.Inspect(json);
        var dc=CircuitSolver.Solve(p);var devices=CircuitDht11.Devices(p,dc);Assert.Null(CircuitPico.Inputs(p,dc,[],dht11Devices:devices).Digital[6]);
        p.Wires.RemoveAll(w=>w.Id=="d");Assert.False(Assert.Single(CircuitDht11.Devices(p)).Connected);Assert.Null(CircuitCodeGuide.Build(p).Sketch);
        p=Project("pico");p.Wires[p.Wires.FindIndex(w=>w.Id=="v")]=new("v",new("board",PicoBoard.Vbus),new("sensor","VDD"));p.Wires[p.Wires.FindIndex(w=>w.Id=="r2")]=new("r2",new("board",PicoBoard.Vbus),new("pullup","2"));
        Assert.Throws<InvalidDataException>(()=>CircuitPico.Inputs(p,CircuitSolver.Solve(p),[],dht11Devices:CircuitDht11.Devices(p)));
        p=Project("uno");p.Parts.Add(new("second",PartKind.Dht11,900,50,25,ModelId:CircuitDht11.ModelId));p.Wires.Add(new("same",new("sensor","DATA"),new("second","DATA")));Assert.Throws<InvalidDataException>(()=>CircuitDht11.ValidateRun(p));
        var old=PartCatalog.All.First(d=>d.Kind==PartKind.Visual&&d.Art=="dht11");var legacy=new CircuitProject();legacy.Parts.Add(new("legacy",PartKind.Visual,0,0,ModelId:old.Id,CustomPins:old.Pins));json=legacy.Serialize();Assert.Equal(json,CircuitProject.Parse(json).Serialize());Assert.False(PartCatalog.CanSimulate(legacy.Parts[0]));
        p=Project("uno");p.Parts[1]=p.Parts[1] with{Value=51};Assert.Throws<InvalidDataException>(()=>p.Serialize());
    }
    [Fact]
    public async Task DhtSharesTheMegaWithTwoTftLcdAndOledWithoutReplacingTheirInputs()
    {
        var project=SharedTftTests.Project("mega2560");var sensor=Project("mega2560");project.Parts.AddRange(sensor.Parts.Where(p=>p.Id!="board"));project.Wires.AddRange(sensor.Wires);
        project.Parts.AddRange([new("lcd",PartKind.LcdI2c,600,500,39,ModelId:"lcd-i2c-16x2"),new("oled",PartKind.OledI2c,950,500,60,ModelId:"oled-ssd1306-i2c")]);
        foreach(var id in new[]{"lcd","oled"})foreach(var (pin,boardPin) in new[]{(id=="lcd"?"VCC":"VDD","5V"),("GND","GND"),("SDA","D20"),("SCL","D21")})project.Wires.Add(new(id+pin,new("board",boardPin),new(id,pin)));
        var guide=CircuitCodeGuide.Build(project);Assert.NotNull(guide.Sketch);project.Code=guide.Sketch;
        var image=await FirmwareRunner.Compile(project.Code,CancellationToken.None,"mega2560");using var runner=new FirmwareRunner();await runner.Start(image,"mega2560");var profile=AvrBoards.Get("mega2560");var text="";FirmwareFrame? frame=null;
        for(var i=0;i<170&&!text.Contains("53.00 T=24.00");i++){frame=await runner.Step(new bool[profile.DigitalCount],new double[profile.AnalogCount],devices:CircuitI2c.Devices(project),spiDevices:CircuitSpi.Devices(project),dht11Devices:CircuitDht11.Devices(project));text+=frame.Serial;}
        Assert.Contains("53.00 T=24.00",text);Assert.NotNull(frame);Assert.Equal(2,frame.TftDisplays!.Length);Assert.All(frame.TftDisplays,d=>{Assert.True(d.Display);var rgb=Convert.FromBase64String(d.Pixels);Assert.Equal(new byte[]{255,0,0},rgb.Skip((8*240+8)*3).Take(3));});Assert.True(Assert.Single(frame.OledDisplays!).Display);Assert.Equal("LCD ready",new string(Assert.Single(frame.Displays!).Cells.Take(9).Select(v=>(char)v).ToArray()));
    }
}
