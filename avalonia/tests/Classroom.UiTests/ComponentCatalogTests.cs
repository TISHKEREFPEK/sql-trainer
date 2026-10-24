using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ComponentCatalogTests
{
    [Fact]
    public void BreadboardRowsPotentiometerAndDiodeHaveElectricalBehavior()
    {
        var p = CircuitExamples.Breadboard(); var output = Enumerable.Range(0,20).Select(_ => new PinDrive(false,0)).ToArray(); output[13] = new(true,5);
        Assert.True(CircuitSolver.Solve(p,output).Parts["led"].Lit);
        p.Wires[1] = p.Wires[1] with { From = new("board","f1") };
        Assert.False(CircuitSolver.Solve(p,output).Parts["led"].Lit);
        var analog = CircuitExamples.Analog(); var index = analog.Parts.FindIndex(part => part.Id == "pot");
        analog.Parts[index] = analog.Parts[index] with { Setting = 0.25 };
        Assert.InRange(CircuitSolver.Solve(analog).Voltages[new("uno","A0")],3.74,3.76);
        var d = new CircuitProject();
        d.Parts.AddRange([new("b",PartKind.Battery,0,0,5),new("r",PartKind.Resistor,0,0,1000),new("d",PartKind.Diode,0,0,0.7)]);
        d.Wires.AddRange([new("1",new("b","+"),new("r","1")),new("2",new("r","2"),new("d","A")),new("3",new("d","K"),new("b","−"))]);
        Assert.InRange(CircuitSolver.Solve(d).Parts["d"].Current,0.004,0.005);
        d.Wires[1] = d.Wires[1] with { To = new("d","K") }; d.Wires[2] = d.Wires[2] with { From = new("d","A") };
        Assert.InRange(CircuitSolver.Solve(d).Parts["d"].Current,0,0.000001);
    }
    [Fact]
    public async Task RealServoFirmwareProducesPulsesAndChangesServoAngle()
    {
        using var runner = new FirmwareRunner();
        var hex = await FirmwareRunner.Compile("#include <Servo.h>\nServo s; void setup(){s.attach(9);s.write(90);} void loop(){}",CancellationToken.None);
        await runner.Start(hex); FirmwareFrame? frame = null;
        for (var i=0;i<4;i++) frame=await runner.Step(new bool[14],new double[6]);
        Assert.InRange(frame!.Pins[9].PulseUs!.Value,1400,1600);
        var result = CircuitSolver.Solve(CircuitExamples.Servo(),frame.Pins);
        Assert.InRange(result.Parts["servo"].Indicator!.Value,80,100);
    }
    [AvaloniaFact]
    public async Task CatalogImagesSearchFavoritesAndUnsupportedModelsAreExplicit()
    {
        Assert.True(PartCatalog.All.Length >= 140);
        Assert.Equal(PartCatalog.All.Length,PartCatalog.All.Select(d=>d.Id).Distinct().Count());
        Assert.All(PartCatalog.All.Where(d=>d.Art!="ground"),d=>Assert.NotNull(PartArtwork.Image(d.Art)));
        var svg = System.Xml.Linq.XDocument.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\">" + PartArtwork.SvgDefinitions() + PartArtwork.SvgPart(new("uno",PartKind.Uno,100,100,Rotation:1)) + "</svg>");
        Assert.Equal(4,svg.Descendants().Count(e=>e.Name.LocalName=="image" && e.Attribute("href")!.Value.StartsWith("data:image/png;base64,")));
        Assert.Contains("matrix(-1 0 0 1 260 0)",svg.ToString());
        var pico = PartCatalog.All.Single(d=>d.ReferenceName=="Raspberry Pi Pico"); Assert.False(pico.Simulated);
        var project = new CircuitProject(); project.Parts.Add(new("pico",PartKind.Visual,100,100,ModelId:pico.Id));
        Assert.Equal(project.Serialize(),CircuitProject.Parse(project.Serialize()).Serialize());
        var dir=Path.Combine(Path.GetTempPath(),"classroom-catalog-"+Guid.NewGuid().ToString("N")); var path=Path.Combine(dir,"project.json");
        var window=new MainWindow(true,false){Width=1320,Height=900}; using var view=new ElectronicsScreen(window,path);
        window.Preview(view); window.Show(); window.UpdateLayout();
        var search=view.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark=="Поиск деталей…"); search.Text="ESP32"; window.UpdateLayout();
        for (var i=0;i<50 && view.GetVisualDescendants().OfType<Button>().Count(b=>AutomationProperties.GetName(b)?.StartsWith("Добавить:")==true)!=2;i++) { await Task.Delay(10); window.UpdateLayout(); }
        var cards=view.GetVisualDescendants().OfType<Button>().Where(b=>AutomationProperties.GetName(b)?.StartsWith("Добавить:")==true).ToArray(); Assert.Equal(2,cards.Length);
        view.GetVisualDescendants().OfType<Button>().First(b=>AutomationProperties.GetName(b)?.StartsWith("Избранное:")==true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(CircuitProject.Parse(File.ReadAllText(path)).Favorites);
        view.GetVisualDescendants().OfType<CheckBox>().Single(c=>c.Content?.ToString()=="С симуляцией").IsChecked=true; window.UpdateLayout();
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),b=>AutomationProperties.GetName(b)?.StartsWith("Добавить:")==true);
        window.Close();
    }
}

