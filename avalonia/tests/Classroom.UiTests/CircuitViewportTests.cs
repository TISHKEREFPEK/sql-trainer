using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;
public sealed class CircuitViewportTests
{
    [AvaloniaFact]
    public void NegativeWorldCoordinatesRemainEditableAndPanZoomDoNotChangeTheProject()
    {
        ThemeManager.Apply(new());var project=CircuitProject.Blink();for(var i=0;i<project.Parts.Count;i++)project.Parts[i]=project.Parts[i] with {X=project.Parts[i].X-5000,Y=project.Parts[i].Y-4500};
        ElectronicsProjectFormat.Inspect(project.Serialize());project.Validate();
        var directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"classroom-viewport-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var file=System.IO.Path.Combine(directory,"project.json");File.WriteAllText(file,project.Serialize());
        var window=new MainWindow(true,false){Width=1320,Height=900};using var view=new ElectronicsScreen(window,file);window.Preview(view);window.Show();window.UpdateLayout();
        var viewport=view.GetVisualDescendants().OfType<CircuitViewport>().Single();var canvas=(Canvas)viewport.Child!;var before=File.ReadAllText(file);var pan=viewport.Pan;
        Point At(Point local)=>viewport.TranslatePoint(local,window)!.Value;
        var start=At(new(150,viewport.Bounds.Height-120));var end=new Point(start.X+75,start.Y+45);window.MouseDown(start,MouseButton.Middle);window.MouseMove(end,RawInputModifiers.MiddleMouseButton);window.MouseUp(end,MouseButton.Middle);window.UpdateLayout();
        Assert.Equal(pan.X+75,viewport.Pan.X,5);Assert.Equal(pan.Y+45,viewport.Pan.Y,5);Assert.Equal(before,File.ReadAllText(file));
        var anchor=new Point(200,200);var world=viewport.ToWorld(anchor);var previousZoom=viewport.Zoom;window.MouseWheel(At(anchor),new Vector(0,1),RawInputModifiers.None);window.UpdateLayout();Assert.True(viewport.Zoom>previousZoom);var afterWorld=viewport.ToWorld(anchor);Assert.Equal(world.X,afterWorld.X,5);Assert.Equal(world.Y,afterWorld.Y,5);
        var resistor=project.Parts[1];var position=PartArtwork.LocalPoint(resistor,new(80,27.5));var face=canvas.TranslatePoint(new((resistor.X+position.X)*viewport.Zoom,(resistor.Y+position.Y)*viewport.Zoom),window)!.Value;
        window.MouseDown(face,MouseButton.Left);window.MouseMove(new(face.X+30,face.Y+20),RawInputModifiers.LeftMouseButton);window.MouseUp(new(face.X+30,face.Y+20),MouseButton.Left);view.Suspend();
        var moved=CircuitProject.Parse(File.ReadAllText(file)).Parts[1];Assert.Equal(resistor.X+30/viewport.Zoom,moved.X,4);Assert.Equal(resistor.Y+20/viewport.Zoom,moved.Y,4);Assert.True(moved.X<0);
        window.UpdateLayout();var corner=view.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="ResizeHandle2");var cornerAt=corner.TranslatePoint(new(8,8),window)!.Value;var resizedAt=new Point(cornerAt.X+16,cornerAt.Y+12);window.MouseDown(cornerAt,MouseButton.Left);window.MouseMove(resizedAt,RawInputModifiers.LeftMouseButton);window.MouseUp(resizedAt,MouseButton.Left);
        var resized=CircuitProject.Parse(File.ReadAllText(file)).Parts[1];Assert.True(resized.StretchX>moved.StretchX);Assert.True(resized.StretchY>moved.StretchY);Assert.Equal(moved.X,resized.X,5);Assert.Equal(moved.Y,resized.Y,5);
        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;var svg=(string)typeof(ElectronicsScreen).GetMethod("Svg",flags)!.Invoke(view,null)!;var xml=System.Xml.Linq.XDocument.Parse(svg);Assert.StartsWith("-",xml.Root!.Attribute("viewBox")!.Value);
        var snapshot=new ElectronicsSnapshotView(new(File.ReadAllText(file),"draft",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),""));window.Preview(snapshot);window.UpdateLayout();Assert.Single(snapshot.GetVisualDescendants().OfType<CircuitViewport>());window.Close();
    }
    [AvaloniaTheory]
    [InlineData(900)] [InlineData(1320)] [InlineData(1920)]
    public void CompactWorkspaceHasCenteredStarsAndNoConnectionInstruction(int width)
    {
        ThemeManager.Apply(new());var file=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"classroom-compact-"+Guid.NewGuid().ToString("N"),"project.json");var window=new MainWindow(true,false){Width=width,Height=1000};using var view=new ElectronicsScreen(window,file);window.Preview(view);window.Show();window.UpdateLayout();
        var viewport=view.GetVisualDescendants().OfType<CircuitViewport>().Single();Assert.Empty(viewport.GetVisualAncestors().OfType<ScrollViewer>());Assert.True(viewport.Bounds.Height>500);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.StartsWith("Соединение: нажмите")==true);
        var favorite=view.GetVisualDescendants().OfType<Button>().First(b=>Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Избранное:")==true);var icon=(PathIcon)favorite.Content!;var center=icon.TranslatePoint(new(icon.Bounds.Width/2,icon.Bounds.Height/2),favorite)!.Value;
        Assert.Equal(favorite.Bounds.Width/2,center.X,1);Assert.Equal(favorite.Bounds.Height/2,center.Y,1);
        using(var frame=window.CaptureRenderedFrame()){var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(output);frame!.Save(System.IO.Path.Combine(output,$"compact-circuit-{width}.png"));}window.Close();
    }
}
