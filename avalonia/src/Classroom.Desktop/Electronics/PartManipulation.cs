using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    string selectionColor="#168BFF";
    Canvas? selectionOverlay;
    CircuitPart? manipulation;
    IPointer? manipulationPointer;
    Point manipulationStart;
    int manipulationCorner;
    bool manipulationChanged;

    void InitializeManipulation()
    {
        canvas.Focusable=true;
        try { if(File.Exists(savePath+".selection-color")) {var color=File.ReadAllText(savePath+".selection-color").Trim();if(ValidSelectionColor(color))selectionColor=color;} }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) { /* A missing visual preference never blocks the project. */ }
        canvas.PointerMoved+=(_,e)=>
        {
            if(manipulation is not {} original)return;
            var at=e.GetPosition(canvas)/zoom;
            if(!manipulationChanged && Math.Sqrt(Math.Pow(at.X-manipulationStart.X,2)+Math.Pow(at.Y-manipulationStart.Y,2))*zoom<3)return;
            if(!manipulationChanged){Checkpoint();manipulationChanged=true;}
            CircuitPart next;
            if(manipulationCorner<0)
            {
                var body=PartArtwork.Body(original);var center=new Point(original.X+body.Width/2,original.Y+body.Height/2);
                var delta=(Math.Atan2(at.Y-center.Y,at.X-center.X)-Math.Atan2(manipulationStart.Y-center.Y,manipulationStart.X-center.X))*180/Math.PI;
                var angle=original.Angle+delta;
                if(e.KeyModifiers.HasFlag(KeyModifiers.Shift))angle=Math.Round((angle+original.QuarterTurns*90)/15)*15-original.QuarterTurns*90;
                next=PartArtwork.WithAngle(original,angle);
            }
            else next=PartArtwork.ResizeCorner(original,manipulationCorner,at,e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            var index=project.Parts.FindIndex(p=>p.Id==original.Id);if(index<0){EndManipulation(true);return;}
            project.Parts[index]=next;Draw();e.Handled=true;
        };
        canvas.PointerReleased+=(_,e)=>{if(manipulation is null)return;EndManipulation(false);e.Handled=true;};
        canvas.PointerCaptureLost+=(_,_)=>{if(manipulation is not null)EndManipulation(false);};
        canvas.PointerPressed+=(_,e)=>{if(e.Source==canvas && manipulation is null){selected=null;selectedWire=null;DrawSelection();Inspect();}};
    }
    void EndManipulation(bool cancel)
    {
        if(manipulation is not {} original)return;
        var pointer=manipulationPointer;manipulation=null;manipulationPointer=null;
        if(cancel&&manipulationChanged){var index=project.Parts.FindIndex(p=>p.Id==original.Id);if(index>=0)project.Parts[index]=original;if(undo.Count>0)undo.RemoveAt(undo.Count-1);}
        pointer?.Capture(null);Draw();Inspect();if(manipulationChanged)_=Perform(Save);manipulationChanged=false;
    }
    static bool ValidSelectionColor(string color)=>System.Text.RegularExpressions.Regex.IsMatch(color,"^#[0-9a-fA-F]{6}$");
    void SelectionColorControls()
    {
        properties.Children.Add(Ui.Text("Цвет выделения",14));
        var input=Ui.Input(selectionColor);input.MaxLength=7;input.Watermark="#RRGGBB";
        Avalonia.Automation.AutomationProperties.SetName(input,"Цвет выделения");properties.Children.Add(input);
        properties.Children.Add(Ui.Button("Применить цвет",()=>Perform(()=>
        {
            var color=(input.Text??"").Trim();if(!ValidSelectionColor(color))throw new InvalidDataException("Введите цвет в формате #RRGGBB, например #168BFF.");
            File.WriteAllText(savePath+".selection-color",color);selectionColor=color;DrawSelection();
        })));
    }
    void DrawSelection()
    {
        if(selectionOverlay is not null)canvas.Children.Remove(selectionOverlay);
        selectionOverlay=null;
        if(selected is null||project.Parts.FirstOrDefault(p=>p.Id==selected) is not {} part)return;
        var overlay=selectionOverlay=new Canvas {Name="PartSelection",Width=canvas.Width,Height=canvas.Height};
        canvas.Children.Add(overlay);
        var brush=Brush.Parse(selectionColor);var corners=PartArtwork.Corners(part).Select(p=>p*zoom).ToArray();
        overlay.Children.Add(new Polyline{Points=new Points(corners.Append(corners[0])),Stroke=brush,StrokeThickness=2,IsHitTestVisible=false});
        for(var i=0;i<4;i++)
        {
            var opposite=corners[(i+2)%4];var angle=(Math.Atan2(corners[i].Y-opposite.Y,corners[i].X-opposite.X)*180/Math.PI+180)%180;
            var direction=(int)Math.Round(angle/45)%4;
            var cursor=direction switch {0=>StandardCursorType.SizeWestEast,1=>StandardCursorType.TopLeftCorner,2=>StandardCursorType.SizeNorthSouth,_=>StandardCursorType.TopRightCorner};
            Handle(corners[i],i,new Cursor(cursor),"Потяните угол: размер детали. Shift — сохранить пропорции.");
        }
        var midpoint=new Point((corners[0].X+corners[1].X)/2,(corners[0].Y+corners[1].Y)/2);
        var radians=PartArtwork.Degrees(part)*Math.PI/180;var knob=midpoint+new Vector(Math.Sin(radians)*34,-Math.Cos(radians)*34);var rotation=new Point(Math.Max(9,knob.X),Math.Max(9,knob.Y));
        overlay.Children.Add(new Line{StartPoint=midpoint,EndPoint=rotation,Stroke=brush,StrokeThickness=2,IsHitTestVisible=false});
        Handle(rotation,-1,new Cursor(StandardCursorType.Hand),"Потяните круг: свободный поворот. Shift — шаг 15°.");
        void Handle(Point point,int corner,Cursor cursor,string tip)
        {
            var marker=new Border{ Name=corner<0?"RotateHandle":"ResizeHandle"+corner,Width=16,Height=16,CornerRadius=new CornerRadius(corner<0?8:2),Background=Brushes.White,BorderBrush=brush,BorderThickness=new Thickness(2),Cursor=cursor};
            ToolTip.SetTip(marker,tip);Avalonia.Automation.AutomationProperties.SetName(marker,corner<0?"Вращение детали":"Размер детали: угол "+(corner+1));
            Canvas.SetLeft(marker,point.X-8);Canvas.SetTop(marker,point.Y-8);overlay.Children.Add(marker);
            marker.PointerPressed+=(_,e)=>
            {
                if(!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed)return;
                manipulation=project.Parts.Single(p=>p.Id==part.Id);manipulationCorner=corner;manipulationStart=e.GetPosition(canvas)/zoom;manipulationChanged=false;
                canvas.Focus();manipulationPointer=e.Pointer;e.Pointer.Capture(canvas);e.Handled=true;
            };
        }
    }
}
