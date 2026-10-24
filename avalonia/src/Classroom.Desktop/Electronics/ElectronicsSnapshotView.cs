using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using Classroom.Contracts;
namespace Classroom.Desktop.Electronics;

public static class ElectronicsLabels
{
    public static string Kind(string? kind)=>kind switch {"submitted"=>"Работа отправлена","compiled"=>"Компиляция успешна","compile-error"=>"Ошибка компиляции","started"=>"Симуляция запущена","run-error"=>"Ошибка симуляции","draft"=>"Схема сохранена",_=>"Нет работ"};
    public static string Date(long at)=>DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss");
}
public sealed class ElectronicsSnapshotView : UserControl
{
    readonly CircuitProject project;
    readonly Canvas canvas=new(){Background=Brushes.WhiteSmoke};
    readonly CircuitViewport viewport;
    double zoom=1;
    public ElectronicsSnapshotView(ElectronicsSnapshot snapshot)
    {
        project=CircuitProject.Parse(snapshot.ProjectJson);
        var scroll=viewport=new CircuitViewport(canvas);viewport.ZoomRequested=(anchor,scale)=>{var world=viewport.ToWorld(anchor);zoom=scale;viewport.SetView(zoom,new(anchor.X-world.X*zoom,anchor.Y-world.Y*zoom));Draw();};
        var scene=new Grid{RowDefinitions=new("Auto,*"),RowSpacing=8};
        scene.Children.Add(Ui.Row(Ui.Button("−",()=>Change(-0.1)),Ui.Button("+",()=>Change(0.1)),Ui.Button("Вписать",()=>{Fit(scroll);return Task.CompletedTask;})));
        Grid.SetRow(scroll,1); scene.Children.Add(scroll);
        var code=new TextEditor{Text=project.Code,IsReadOnly=true,ShowLineNumbers=true,FontFamily=new FontFamily("Cascadia Code, Consolas"),FontSize=14,SyntaxHighlighting=AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition("C++")};
        Ui.BindBrush(code,TextEditor.BackgroundProperty,"Editor");Ui.BindBrush(code,TextEditor.ForegroundProperty,"EditorInk");code.Padding=new Thickness(16,20);code.SyntaxHighlighting=CodeAppearance.CppForTheme();
        void ApplyTheme()=>code.SyntaxHighlighting=CodeAppearance.CppForTheme();AttachedToVisualTree+=(_,_)=>{ThemeManager.Changed+=ApplyTheme;ApplyTheme();};DetachedFromVisualTree+=(_,_)=>ThemeManager.Changed-=ApplyTheme;
        var detail=Ui.Input(snapshot.Detail.Length==0?"Подробных сообщений нет.":snapshot.Detail,true); detail.IsReadOnly=true;
        var tabs=new TabControl{ItemsSource=new[]{new TabItem{Header="Схема · снимок",Content=scene},new TabItem{Header="Код ученика",Content=code},new TabItem{Header="Сообщение запуска",Content=detail}}};
        var root=new Grid{RowDefinitions=new("Auto,Auto,*"),RowSpacing=8};
        root.Children.Add(Ui.Text(project.Name.Length==0?"Без названия":project.Name,20));
        var description=Ui.Text(ElectronicsLabels.Kind(snapshot.Kind)+" · получено "+ElectronicsLabels.Date(snapshot.At)+"\nПросмотр без редактирования. Состояние запуска сообщено приложением ученика; это не оценка задания.",12); Grid.SetRow(description,1);root.Children.Add(description); Grid.SetRow(tabs,2);root.Children.Add(tabs); Content=root;
        Draw(); var first=true; scroll.LayoutUpdated+=(_,_)=>{if(first&&scroll.Bounds.Width>150){first=false;Fit(scroll);}};
    }
    Task Change(double delta){var anchor=new Point(viewport.Bounds.Width/2,viewport.Bounds.Height/2);var world=viewport.ToWorld(anchor);zoom=Math.Clamp(zoom+delta,0.1,3);viewport.SetView(zoom,new(anchor.X-world.X*zoom,anchor.Y-world.Y*zoom));Draw();return Task.CompletedTask;}
    void Fit(CircuitViewport scroll){scroll.Fit(PartArtwork.Bounds(project.Parts));zoom=scroll.Zoom;Draw();}
    void Draw()
    {
        canvas.Children.Clear();canvas.Width=Math.Max(1,viewport.Bounds.Width);canvas.Height=Math.Max(1,viewport.Bounds.Height);canvas.ClipToBounds=false;viewport.SetView(zoom,viewport.Pan);
        foreach(var wire in project.Wires)
        {
            var a=PartArtwork.Pin(project.Parts.Single(p=>p.Id==wire.From.Part),wire.From.Pin);var b=PartArtwork.Pin(project.Parts.Single(p=>p.Id==wire.To.Part),wire.To.Pin);
            canvas.Children.Add(new Polyline{Points=new Points([new(a.X*zoom,a.Y*zoom),new((a.X+b.X)/2*zoom,a.Y*zoom),new((a.X+b.X)/2*zoom,b.Y*zoom),new(b.X*zoom,b.Y*zoom)]),Stroke=Brush.Parse(wire.Color),StrokeThickness=3*zoom});
        }
        foreach(var part in project.Parts)
        {
            var size=PartArtwork.BaseBody(part); var face=new Canvas{Width=size.Width,Height=size.Height,RenderTransformOrigin=new RelativePoint(0,0,RelativeUnit.Relative),RenderTransform=new MatrixTransform{Matrix=PartArtwork.TransformMatrix(part,zoom)}};
            face.Children.Add(part.Kind==PartKind.Breadboard?ElectronicsScreen.BreadboardFace(1):PartArtwork.Thumbnail(PartCatalog.For(part).Art,size.Width,size.Height)); Canvas.SetLeft(face,part.X*zoom);Canvas.SetTop(face,part.Y*zoom);canvas.Children.Add(face);
            var title=Ui.Text(PartLibrary.Name(part),12*zoom);Canvas.SetLeft(title,part.X*zoom);Canvas.SetTop(title,(part.Y-22)*zoom);canvas.Children.Add(title);
            foreach(var pin in PartLibrary.Pins(part)){var at=PartArtwork.Pin(part,pin); var hole=new Ellipse{Width=8*zoom,Height=8*zoom,Fill=Brushes.DodgerBlue,Stroke=Brushes.White,StrokeThickness=1};ToolTip.SetTip(hole,pin);Canvas.SetLeft(hole,(at.X-4)*zoom);Canvas.SetTop(hole,(at.Y-4)*zoom);canvas.Children.Add(hole);}
        }
    }
}
