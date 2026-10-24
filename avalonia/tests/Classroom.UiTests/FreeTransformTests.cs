using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;
using Path=System.IO.Path;

namespace Classroom.UiTests;
public sealed class FreeTransformTests
{
    [Theory]
    [InlineData(0)] [InlineData(37)] [InlineData(125)] [InlineData(271)]
    public void CornersStayAnchoredAndPinsUseTheSameAffineTransform(double angle)
    {
        var project=CircuitProject.Blink();var original=project.Parts[1] with {Angle=angle,StretchX=1.2,StretchY=0.7};
        var before=PartArtwork.Corners(original); var radians=PartArtwork.Degrees(original)*Math.PI/180;
        for(var corner=0;corner<4;corner++)
        {
            var anchor=before[(corner+2)%4];var size=PartArtwork.BaseBody(original);var x=(corner is 1 or 2?1:-1)*size.Width*original.Scale*1.8;var y=(corner>=2?1:-1)*size.Height*original.Scale*1.3;
            var pointer=new Point(anchor.X+x*Math.Cos(radians)-y*Math.Sin(radians),anchor.Y+x*Math.Sin(radians)+y*Math.Cos(radians));
            var next=PartArtwork.ResizeCorner(original,corner,pointer);var anchored=PartArtwork.Corners(next)[(corner+2)%4];
            Assert.Equal(anchor.X,anchored.X,7);Assert.Equal(anchor.Y,anchored.Y,7);Assert.Equal(1.8,next.StretchX,7);Assert.Equal(1.3,next.StretchY,7);
            var pin=PartArtwork.Pin(next,"1");var transformed=new Point(3,27.5).Transform(PartArtwork.TransformMatrix(next));
            Assert.Equal(pin.X-next.X,transformed.X,7);Assert.Equal(pin.Y-next.Y,transformed.Y,7);
            var uniform=PartArtwork.ResizeCorner(original,corner,pointer,true);Assert.Equal(original.StretchX/original.StretchY,uniform.StretchX/uniform.StretchY,7);
            project.Parts[1]=next;var json=project.Serialize();ElectronicsProjectFormat.Inspect(json);Assert.Equal(json,CircuitProject.Parse(json).Serialize());
        }
        foreach(var invalid in new[]{original with {Angle=double.NaN},original with {Angle=360},original with {StretchX=0},original with {StretchY=11}})
        {project.Parts[1]=invalid;Assert.Throws<InvalidDataException>(project.Validate);}
    }
    [AvaloniaTheory]
    [InlineData(900)] [InlineData(1320)]
    public void MouseHandlesRotateStretchUndoAndPersistSelectionColor(int width)
    {
        var dir=Path.Combine(Path.GetTempPath(),"classroom-free-transform-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var file=Path.Combine(dir,"project.json");File.WriteAllText(file,CircuitProject.Blink().Serialize());
        var window=new MainWindow(true,false){Width=width,Height=900};using var view=new ElectronicsScreen(window,file);window.Preview(view);window.Show();window.UpdateLayout();
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        if(width==900)typeof(ElectronicsScreen).GetField("zoom",flags)!.SetValue(view,0.45);
        typeof(ElectronicsScreen).GetField("selected",flags)!.SetValue(view,"r");typeof(ElectronicsScreen).GetMethod("Inspect",flags)!.Invoke(view,null);typeof(ElectronicsScreen).GetMethod("Draw",flags)!.Invoke(view,null);window.UpdateLayout();
        var canvas=(Canvas)typeof(ElectronicsScreen).GetField("canvas",flags)!.GetValue(view)!;
        var zoom=(double)typeof(ElectronicsScreen).GetField("zoom",flags)!.GetValue(view)!;
        Border Handle(string name)=>view.GetVisualDescendants().OfType<Border>().Single(b=>b.Name==name);
        Point WindowPoint(Point p)=>canvas.TranslatePoint(p*zoom,window)!.Value;
        Point MarkerPoint(string name)=>Handle(name).TranslatePoint(new(8,8),window)!.Value;
        void Click(string text){view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();}
        var part=CircuitProject.Parse(File.ReadAllText(file)).Parts[1];var size=PartArtwork.Body(part);var center=WindowPoint(new(part.X+size.Width/2,part.Y+size.Height/2));
        var start=MarkerPoint("RotateHandle");var radians=37*Math.PI/180;var x=start.X-center.X;var y=start.Y-center.Y;var end=new Point(center.X+x*Math.Cos(radians)-y*Math.Sin(radians),center.Y+x*Math.Sin(radians)+y*Math.Cos(radians));
        Assert.NotNull(Handle("RotateHandle").Cursor);Assert.NotNull(Handle("ResizeHandle2").Cursor);
        window.MouseDown(start,MouseButton.Left);window.MouseMove(end,RawInputModifiers.LeftMouseButton);window.MouseUp(end,MouseButton.Left);window.UpdateLayout();
        var rotated=CircuitProject.Parse(File.ReadAllText(file)).Parts[1];Assert.InRange(rotated.Angle,36.99,37.01);
        var corners=PartArtwork.Corners(rotated);var anchor=corners[0];var baseSize=PartArtwork.BaseBody(rotated);var target=new Point(anchor.X+baseSize.Width*1.4*Math.Cos(radians)-baseSize.Height*1.7*Math.Sin(radians),anchor.Y+baseSize.Width*1.4*Math.Sin(radians)+baseSize.Height*1.7*Math.Cos(radians));
        start=MarkerPoint("ResizeHandle2");end=WindowPoint(target);window.MouseDown(start,MouseButton.Left);window.MouseMove(new((start.X+end.X)/2,(start.Y+end.Y)/2),RawInputModifiers.LeftMouseButton);window.MouseMove(end,RawInputModifiers.LeftMouseButton);window.MouseUp(end,MouseButton.Left);window.UpdateLayout();
        var stretched=CircuitProject.Parse(File.ReadAllText(file)).Parts[1];Assert.Equal(1.4,stretched.StretchX,5);Assert.Equal(1.7,stretched.StretchY,5);Assert.Equal(anchor.X,PartArtwork.Corners(stretched)[0].X,5);Assert.Equal(anchor.Y,PartArtwork.Corners(stretched)[0].Y,5);
        var pin=PartArtwork.Pin(stretched,"1");Assert.Contains(view.GetVisualDescendants().OfType<Polyline>(),w=>w.Points.Count==4&&Math.Abs(w.Points[^1].X-pin.X*zoom)<0.0001&&Math.Abs(w.Points[^1].Y-pin.Y*zoom)<0.0001);
        Click("Отменить");var undone=CircuitProject.Parse(File.ReadAllText(file)).Parts[1];Assert.Equal(rotated.Angle,undone.Angle,7);Assert.Equal(1,undone.StretchX);Click("Повторить");
        typeof(ElectronicsScreen).GetField("selected",flags)!.SetValue(view,"r");typeof(ElectronicsScreen).GetMethod("Inspect",flags)!.Invoke(view,null);typeof(ElectronicsScreen).GetMethod("Draw",flags)!.Invoke(view,null);window.UpdateLayout();
        var input=view.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Цвет выделения");input.Text="#A844CC";Click("Применить цвет");
        Assert.Equal("#A844CC",File.ReadAllText(file+".selection-color"));var selection=view.GetVisualDescendants().OfType<Canvas>().Single(c=>c.Name=="PartSelection");Assert.Equal(Color.Parse("#A844CC"),((ISolidColorBrush)selection.Children.OfType<Polyline>().Single().Stroke!).Color);
        var svg=(string)typeof(ElectronicsScreen).GetMethod("Svg",flags)!.Invoke(view,null)!;System.Xml.Linq.XDocument.Parse(svg);
        var beforeCancel=File.ReadAllText(file);start=MarkerPoint("ResizeHandle2");end=new Point(start.X+20,start.Y+30);window.MouseDown(start,MouseButton.Left);window.MouseMove(end,RawInputModifiers.LeftMouseButton);window.KeyPressQwerty(PhysicalKey.Escape,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Escape,RawInputModifiers.None);window.MouseUp(end,MouseButton.Left);window.UpdateLayout();Assert.Equal(beforeCancel,File.ReadAllText(file));
        using(var frame=window.CaptureRenderedFrame()){var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(output);frame!.Save(Path.Combine(output,$"free-transform-{width}.png"));}
        window.Close();
        using var reopened=new ElectronicsScreen(new MainWindow(true,false),file);Assert.Equal("#A844CC",typeof(ElectronicsScreen).GetField("selectionColor",flags)!.GetValue(reopened));
    }
}
