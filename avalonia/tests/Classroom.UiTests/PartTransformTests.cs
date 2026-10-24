using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;
using Path = System.IO.Path;

namespace Classroom.UiTests;
public sealed class PartTransformTests
{
    [Theory]
    [InlineData(0,3,27.5)]
    [InlineData(1,27.5,3)]
    [InlineData(2,157,27.5)]
    [InlineData(3,27.5,157)]
    public void RotationAndScalePreserveTerminalIdentityAndElectricalResult(int turns,double x,double y)
    {
        var original = CircuitProject.Blink(); var before = original.Parts[1];
        var part = before with { Scale=0.6,QuarterTurns=turns };
        var pin = PartArtwork.Pin(part,"1");
        Assert.Equal(part.X+x*0.6,pin.X,8); Assert.Equal(part.Y+y*0.6,pin.Y,8);
        var centred=PartArtwork.WithLayout(before,0.6,turns);
        Assert.Equal(before.X+PartArtwork.Body(before).Width/2,centred.X+PartArtwork.Body(centred).Width/2,8);
        Assert.Equal(before.Y+PartArtwork.Body(before).Height/2,centred.Y+PartArtwork.Body(centred).Height/2,8);
        var matrix=PartArtwork.TransformMatrix(part); var point=new Point(3,27.5).Transform(matrix);
        Assert.Equal(pin.X-part.X,point.X,8); Assert.Equal(pin.Y-part.Y,point.Y,8);
        var drives=Enumerable.Range(0,20).Select(_=>new PinDrive(false,0)).ToArray(); drives[13]=new(true,5);
        var current=CircuitSolver.Solve(original,drives).Parts["led"].Current;
        original.Parts[1]=centred; original.Validate();
        Assert.Equal(current,CircuitSolver.Solve(original,drives).Parts["led"].Current,10);
        Assert.Equal(original.Serialize(),CircuitProject.Parse(original.Serialize()).Serialize());
    }
    [Fact]
    public void OldMirrorFilesKeepTheirAppearanceAndInvalidTransformsAreRejected()
    {
        var project=CircuitProject.Blink(); project.Parts[1]=project.Parts[1] with { Rotation=1 };
        var old=project.Serialize().Replace(",\n      \"scale\": 1","").Replace(",\n      \"quarterTurns\": 0","");
        var part=CircuitProject.Parse(old).Parts[1]; Assert.Equal(1,part.Scale); Assert.Equal(0,part.QuarterTurns);
        Assert.Equal(part.X+157,PartArtwork.Pin(part,"1").X,8);
        foreach(var invalid in new[] { part with { Scale=0.3 },part with { Scale=2.1 },part with { Scale=double.NaN },part with { QuarterTurns=4 } })
        { project.Parts[1]=invalid; Assert.Throws<InvalidDataException>(project.Validate); }
    }
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public void ControlsSaveTransformKeepWiresAttachedAndSupportUndo(int width)
    {
        var dir=Path.Combine(Path.GetTempPath(),"classroom-transform-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var file=Path.Combine(dir,"project.json"); File.WriteAllText(file,CircuitProject.Blink().Serialize());
        var window=new MainWindow(true,false){Width=width,Height=900}; using var view=new ElectronicsScreen(window,file); window.Preview(view); window.Show(); window.UpdateLayout();
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(ElectronicsScreen).GetField("selected",flags)!.SetValue(view,"r"); typeof(ElectronicsScreen).GetMethod("Inspect",flags)!.Invoke(view,null); window.UpdateLayout();
        void Click(string label) { view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); }
        Click("Меньше"); Click("↷ 90°"); var changed=CircuitProject.Parse(File.ReadAllText(file)); var resistor=changed.Parts.Single(p=>p.Id=="r");
        Assert.Equal(0.8,resistor.Scale); Assert.Equal(1,resistor.QuarterTurns); Assert.Equal(3,changed.Wires.Count);
        var zoom=(double)typeof(ElectronicsScreen).GetField("zoom",flags)!.GetValue(view)!;
        var end=PartArtwork.Pin(resistor,"1"); var start=PartArtwork.Pin(changed.Parts.Single(p=>p.Id=="uno"),"D13");
        Assert.Contains(view.GetVisualDescendants().OfType<Polyline>(), wire=>Math.Abs(wire.Points[^1].X-end.X*zoom)<0.000001 && Math.Abs(wire.Points[^1].Y-end.Y*zoom)<0.000001 && Math.Abs(wire.Points[0].X-start.X*zoom)<0.000001 && Math.Abs(wire.Points[0].Y-start.Y*zoom)<0.000001);
        var svg=(string)typeof(ElectronicsScreen).GetMethod("Svg",flags)!.Invoke(view,null)!; System.Xml.Linq.XDocument.Parse(svg); Assert.Contains("matrix(0 0.8 -0.8 0",svg);
        using(var frame=window.CaptureRenderedFrame()) { var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots"); Directory.CreateDirectory(output); frame!.Save(Path.Combine(output,$"part-transform-{width}.png")); }
        Click("Размер 100%"); Assert.Equal(1,CircuitProject.Parse(File.ReadAllText(file)).Parts.Single(p=>p.Id=="r").Scale);
        Click("Отменить"); Assert.Equal(0.8,CircuitProject.Parse(File.ReadAllText(file)).Parts.Single(p=>p.Id=="r").Scale);
        Click("Повторить"); Assert.Equal(1,CircuitProject.Parse(File.ReadAllText(file)).Parts.Single(p=>p.Id=="r").Scale);
        window.Close();
    }
}
