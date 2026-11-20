using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Classroom.Desktop;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Reflection;
using Xunit;

namespace Classroom.UiTests;

public sealed class SharedTftTests
{
    readonly Xunit.Abstractions.ITestOutputHelper output;
    public SharedTftTests(Xunit.Abstractions.ITestOutputHelper output)=>this.output=output;
    public static CircuitProject Project(string board,bool sharedControl=true)
    {
        var project=TftNativeTests.Project(board);project.Name="Два TFT · "+board;
        var pico=board=="pico";var mega=board=="mega2560";string Pin(int n)=>pico?PicoBoard.Gpio(n)!:"D"+n;
        var second=project.Parts.Single(p=>p.Id=="tft") with{Id="tft2",X=mega?1120:820};project.Parts.Add(second);
        foreach(var wire in project.Wires.Where(w=>w.To.Part=="tft").ToArray()){
            var from=wire.From;from=wire.To.Pin switch{
                "CS"=>new("board",Pin(pico?22:mega?47:4)),
                "D/C" when !sharedControl=>new("board",Pin(pico?12:mega?46:3)),
                "RST" when !sharedControl=>new("board",Pin(pico?13:mega?45:2)),_=>from};
            project.Wires.Add(new(wire.Id+"b",from,new("tft2",wire.To.Pin),wire.Color));
        }
        return project;
    }
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    [InlineData("pico")]
    public void SharedClockDataDcAndResetProduceAnIndependentStarterWithoutChangingWires(string board)
    {
        var project=Project(board);var json=project.Serialize();var devices=CircuitSpi.Devices(project);Assert.Equal(2,devices.Length);Assert.All(devices,d=>Assert.True(d.Powered&&d.Connected));
        var guide=CircuitCodeGuide.Build(project);Assert.True(guide.Sketch is not null,guide.Notes);Assert.Equal(json,project.Serialize());
        Assert.Contains("tft0",guide.Sketch);Assert.Contains("tft1",guide.Sketch);
    }
    static void Code(CircuitProject project,bool pico)
    {
        var guide=CircuitCodeGuide.Build(project);Assert.True(guide.Sketch is not null,guide.Notes);
        var serial=pico?"Serial1":"Serial";
        project.Code=guide.Sketch.Replace("tft1.fillRect(8,8,16,12,ILI9341_RED)","tft1.fillRect(8,8,16,12,ILI9341_GREEN)").Replace("void setup()","void initialiseDisplays()").Replace("void loop()","void lessonLoop()")+$$"""

            void setup(){initialiseDisplays();{{serial}}.begin(115200);
              {{serial}}.print(tft0.readcommand8(0xd3,2),HEX);{{serial}}.print(';');{{serial}}.print(tft0.readcommand8(0xd3,3),HEX);{{serial}}.print(';');
              {{serial}}.print(tft1.readcommand8(0xd3,2),HEX);{{serial}}.print(';');{{serial}}.print(tft1.readcommand8(0xd3,3),HEX);{{serial}}.println(";DONE");}
            void loop(){while({{serial}}.available()){char c={{serial}}.read();tft0.drawPixel(0,0,c&1?ILI9341_RED:ILI9341_BLUE);tft1.drawPixel(0,0,c&1?ILI9341_GREEN:ILI9341_BLUE);{{serial}}.println("UPDATE");}lessonLoop();}
            """;
    }
    static void Pixels(TftDisplayFrame[] frames)
    {
        Assert.Equal(2,frames.Length);Assert.All(frames,f=>Assert.True(f.Display));
        byte[] Pixel(string id)=>Convert.FromBase64String(frames.Single(f=>f.Id==id).Pixels).Skip((8*240+8)*3).Take(3).ToArray();
        Assert.Equal(new byte[]{255,0,0},Pixel("tft"));Assert.Equal(new byte[]{0,255,0},Pixel("tft2"));
    }
    static CircuitProject Mixed()
    {
        var project=Project("mega2560");var i2c=OledTests.Project("mega2560",true);
        project.Parts.AddRange(i2c.Parts.Where(p=>p.Id!="mcu"));
        Terminal Port(Terminal t)=>t.Part=="mcu"?t with{Part="board"}:t;
        project.Wires.AddRange(i2c.Wires.Select(w=>w with{Id="i2c-"+w.Id,From=Port(w.From),To=Port(w.To)}));
        for(var i=0;i<project.Parts.Count;i++)project.Parts[i]=project.Parts[i].Kind switch{PartKind.LcdI2c=>project.Parts[i] with{X=160,Y=420},PartKind.OledI2c=>project.Parts[i] with{X=500,Y=450},_=>project.Parts[i]};
        Code(project,false);return project;
    }
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    [InlineData("pico")]
    public async Task RealSharedResetDoesNotEraseTheFirstDisplayAndResponsesStayBounded(string board)
    {
        var pico=board=="pico";var project=Project(board);Code(project,pico);var devices=CircuitSpi.Devices(project);var text="";TftDisplayFrame[] frames=[];long peak=0;
        var json=project.Serialize();var journal=ElectronicsJournal.Start(json,1);
        void Metadata(ElectronicsSpiIo[]? samples){if(samples is {Length:>0})journal=ElectronicsJournal.Record(journal,json,json,"spi-transfer",journal.Changes.Length+2,force:true,spiIo:samples);}
        if(pico){
            var image=await PicoCompiler.Compile(project.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
            for(var i=0;i<260&&!text.Contains("DONE");i++){var frame=await runner.Step(spiDevices:devices);text+=frame.SerialPorts[1];frames=frame.TftDisplays!;Metadata(frame.Spi);Assert.Empty(frame.Diagnostics);}
            Pixels(frames);var process=(System.Diagnostics.Process)typeof(PicoFirmwareRunner).GetField("process",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(runner)!;var updates="";
            for(var i=0;i<120;i++){var frame=await runner.Step(spiDevices:devices,serialPort:1,serialInput:i%4==0?"U":"");updates+=frame.SerialPorts[1];frames=frame.TftDisplays!;Assert.Equal(2,frames.Length);Assert.InRange(Wire.Write(frame).Length,1,1_000_000);process.Refresh();peak=Math.Max(peak,process.PeakWorkingSet64);}
            Assert.True(updates.Split("UPDATE").Length>10,updates);Assert.InRange(peak,1,512L*1024*1024);output.WriteLine("Two active TFT worker peak working set: "+peak+" bytes.");
        }else{
            var image=await FirmwareRunner.Compile(project.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(image,board);var profile=AvrBoards.Get(board);
            for(var i=0;i<160&&!text.Contains("DONE");i++){var frame=await runner.Step(new bool[profile.DigitalCount],new double[profile.AnalogCount],spiDevices:devices);text+=frame.Serial;frames=frame.TftDisplays!;Metadata(frame.Spi);}
        }
        Assert.EndsWith("93;41;93;41;DONE\r\n",text);Pixels(frames);Assert.Equal(json,journal.Replay());
    }
    [Fact]
    public async Task TwoTftAndLcdOledUseTheirActualSpiAndI2cBusesTogether()
    {
        var project=Mixed();
        var image=await FirmwareRunner.Compile(project.Code,CancellationToken.None,"mega2560");using var runner=new FirmwareRunner();await runner.Start(image,"mega2560");var profile=AvrBoards.Get("mega2560");var packets=new List<ElectronicsI2cIo>();var text="";FirmwareFrame? frame=null;
        for(var i=0;i<180&&!text.Contains("DONE");i++){frame=await runner.Step(new bool[profile.DigitalCount],new double[profile.AnalogCount],devices:CircuitI2c.Devices(project),spiDevices:CircuitSpi.Devices(project));text+=frame.Serial;packets.AddRange(frame.I2c??[]);}
        Assert.NotNull(frame);Assert.Contains("93;41;93;41;DONE",text);Pixels(frame.TftDisplays!);Assert.True(Assert.Single(frame.OledDisplays!).Display);Assert.Equal("LCD ready",new string(Assert.Single(frame.Displays!).Cells.Take(9).Select(v=>(char)v).ToArray()));Assert.Contains(packets,p=>p.Address==60&&p.Ack);Assert.Contains(packets,p=>p.Address==39&&p.Ack);
    }
    [Fact]
    public void CrossRoleControlOrChipSelectConflictsRequireExplicitCorrectionWhileThirdPanelStillSaves()
    {
        foreach(var pin in new[]{"CS","RST"}){
            var project=Project("mega2560");var wire=project.Wires.Single(w=>w.To==new Terminal("tft2",pin));var source=project.Wires.Single(w=>w.To==new Terminal("tft",pin=="CS"?"CS":"D/C")).From;
            project.Wires[project.Wires.IndexOf(wire)]=wire with{From=source};Assert.Null(CircuitCodeGuide.Build(project).Sketch);
        }
        var third=Project("mega2560");third.Parts.Add(third.Parts.Single(p=>p.Id=="tft2") with{Id="tft3"});Assert.Equal(4,CircuitProject.Parse(third.Serialize()).Parts.Count);Assert.Contains("два",Assert.Throws<System.IO.InvalidDataException>(()=>CircuitSpi.ValidateRun(third)).Message);
    }
    [AvaloniaFact]
    public async Task NativeMixedDisplaysRenderAndSaveTheirSharedWiresWithoutCrossingTheViewportTools()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var project=Mixed();var directory=Path.Combine(Path.GetTempPath(),"classroom-shared-tft-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var file=Path.Combine(directory,"work.json");File.WriteAllText(file,project.Serialize());
        var window=new MainWindow(true,false){Width=1320,Height=1000};using var view=new ElectronicsScreen(window,file,teacher:true);window.Preview(view);window.Show();window.UpdateLayout();
        await (Task)typeof(ElectronicsScreen).GetMethod("Compile",flags)!.Invoke(view,[true])!;((DispatcherTimer)typeof(ElectronicsScreen).GetField("timer",flags)!.GetValue(view)!).Stop();
        var monitor=(TextBox)typeof(ElectronicsScreen).GetField("monitor",flags)!.GetValue(view)!;for(var i=0;i<180&&monitor.Text?.Contains("DONE")!=true;i++)await (Task)typeof(ElectronicsScreen).GetMethod("Tick",flags)!.Invoke(view,null)!;
        Assert.Contains("93;41;93;41;DONE",monitor.Text);Pixels(((Dictionary<string,TftDisplayFrame>)typeof(ElectronicsScreen).GetField("tftDisplays",flags)!.GetValue(view)!).Values.ToArray());
        var canvas=(Canvas)typeof(ElectronicsScreen).GetField("canvas",flags)!.GetValue(view)!;Assert.Equal(2,canvas.GetVisualDescendants().OfType<TftDisplayFace>().Count());Assert.Single(canvas.GetVisualDescendants().OfType<OledDisplayFace>());Assert.Single(canvas.GetVisualDescendants().OfType<LcdDisplayFace>());
        view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Вписать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();var viewport=view.GetVisualDescendants().OfType<CircuitViewport>().Single();foreach(var part in project.Parts)foreach(var p in PartArtwork.Corners(part))Assert.InRange(p.Y*viewport.Zoom+viewport.Pan.Y,56,viewport.Bounds.Height-60);
        using(var frame=window.CaptureRenderedFrame()){Assert.NotNull(frame);var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??directory;Directory.CreateDirectory(output);frame!.Save(Path.Combine(output,"shared-tft-lcd-oled-1320.png"));}
        view.Suspend();Assert.Equal(project.Serialize(),File.ReadAllText(file));window.Close();
    }
}
