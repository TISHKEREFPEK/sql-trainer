using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;
public sealed class PicoConnectionTests
{
    [AvaloniaFact]
    public void ExistingPicoGetsFortyVisibleConnectableTerminals()
    {
        var pico = PartCatalog.All.Single(d => d.ReferenceName == "Raspberry Pi Pico");
        var project = new CircuitProject { Name = "Raspberry Pi Pico · выводы" };
        project.Parts.Add(new("pico",PartKind.Visual,200,70,ModelId:pico.Id,CustomPins:[]));
        Assert.Equal(40,PartLibrary.Pins(project.Parts[0]).Length);
        Assert.Equal(40,PartLibrary.Pins(project.Parts[0]).Distinct().Count());
        var dir = Path.Combine(Path.GetTempPath(),"classroom-pico-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir,"project.json"); File.WriteAllText(file,project.Serialize());
        var window = new MainWindow(true,false) { Width=1320,Height=900 };
        using var view = new ElectronicsScreen(window,file); window.Preview(view); window.Show(); window.UpdateLayout();
        var pins=view.GetVisualDescendants().OfType<Button>().Where(b=>Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Raspberry Pi Pico: ")==true).ToArray();
        Assert.Equal(40,pins.Length); Assert.All(pins,b=>Assert.True(b.Bounds.Width>=12));
        pins[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // Selecting a terminal redraws the canvas, so query the second button again.
        view.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Raspberry Pi Pico: 2:GP1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(CircuitProject.Parse(File.ReadAllText(file)).Wires);
        using var frame=window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory,"screenshots"); Directory.CreateDirectory(output); frame!.Save(Path.Combine(output,"pico-terminals.png"));
        window.Close();
    }
}
