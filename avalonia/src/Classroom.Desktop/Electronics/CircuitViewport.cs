using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Classroom.Desktop.Electronics;

// Only the visible grid is drawn. Panning does not allocate a larger bitmap or
// change any project coordinates; the scene is translated beneath this clip.
public sealed class CircuitViewport : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty=AvaloniaProperty.Register<CircuitViewport,IBrush?>(nameof(Background));
    public IBrush? Background {get=>GetValue(BackgroundProperty);set=>SetValue(BackgroundProperty,value);}
    static CircuitViewport(){AffectsRender<CircuitViewport>(BackgroundProperty);}
    readonly Canvas scene;
    readonly TranslateTransform translation=new();
    Point pointerStart;
    Vector panStart;
    bool panning,space;
    public Vector Pan {get;private set;}
    public double Zoom {get;private set;}=1;
    public bool ShowGrid {get;set;}=true;
    public Action<Point,double>? ZoomRequested {get;set;}
    public Action? BackgroundPressed {get;set;}
    public CircuitViewport(Canvas scene)
    {
        this.scene=scene;ClipToBounds=true;Focusable=true;MinWidth=100;MinHeight=100;
        Ui.BindBrush(this,BackgroundProperty,"Background");scene.RenderTransform=translation;scene.Background=Brushes.Transparent;scene.HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Left;scene.VerticalAlignment=Avalonia.Layout.VerticalAlignment.Top;Child=scene;
        AddHandler(PointerPressedEvent,(_,e)=>
        {
            var properties=e.GetCurrentPoint(this).Properties;
            if(properties.IsMiddleButtonPressed||properties.IsRightButtonPressed||space&&properties.IsLeftButtonPressed)
            {Focus();panning=true;pointerStart=e.GetPosition(this);panStart=Pan;e.Pointer.Capture(this);Cursor=new Cursor(StandardCursorType.SizeAll);e.Handled=true;}
            else if(e.Source==this&&properties.IsLeftButtonPressed){Focus();BackgroundPressed?.Invoke();}
        },RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent,(_,e)=>{if(!panning)return;var point=e.GetPosition(this);SetView(Zoom,panStart+new Vector(point.X-pointerStart.X,point.Y-pointerStart.Y));e.Handled=true;},RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent,(_,e)=>{if(!panning)return;panning=false;e.Pointer.Capture(null);Cursor=space?new Cursor(StandardCursorType.SizeAll):null;e.Handled=true;},RoutingStrategies.Tunnel);
        PointerCaptureLost+=(_,_)=>{panning=false;Cursor=null;};
        AddHandler(PointerWheelChangedEvent,(_,e)=>{ZoomRequested?.Invoke(e.GetPosition(this),Math.Clamp(Zoom*Math.Pow(1.15,e.Delta.Y),0.1,3));e.Handled=true;},RoutingStrategies.Tunnel);
        KeyDown+=(_,e)=>{if(e.Key==Key.Space){space=true;Cursor=new Cursor(StandardCursorType.SizeAll);e.Handled=true;}};
        KeyUp+=(_,e)=>{if(e.Key==Key.Space){space=false;if(!panning)Cursor=null;e.Handled=true;}};
        LostFocus+=(_,_)=>{space=false;if(!panning)Cursor=null;};
        SizeChanged+=(_,e)=>{if(e.PreviousSize.Width>100&&e.PreviousSize.Height>100)SetView(Zoom,Pan+new Vector((e.NewSize.Width-e.PreviousSize.Width)/2,(e.NewSize.Height-e.PreviousSize.Height)/2));};
    }
    public Point ToWorld(Point point)=>new((point.X-Pan.X)/Zoom,(point.Y-Pan.Y)/Zoom);
    public void SetView(double zoom,Vector pan)
    {Zoom=zoom;Pan=pan;translation.X=pan.X;translation.Y=pan.Y;InvalidateVisual();}
    public void Fit(Rect bounds,double maximum=1.4)
    {
        if(Bounds.Width<1||Bounds.Height<1)return;
        var zoom=Math.Clamp(Math.Min(Bounds.Width/Math.Max(1,bounds.Width),Bounds.Height/Math.Max(1,bounds.Height)),0.1,maximum);
        SetView(zoom,new(Bounds.Width/2-bounds.Center.X*zoom,Bounds.Height/2-bounds.Center.Y*zoom));
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);if(Background is {} background)context.DrawRectangle(background,null,new Rect(Bounds.Size));if(!ShowGrid)return;
        var spacing=25*Zoom;while(spacing<14)spacing*=2;while(spacing>55)spacing/=2;
        var brush=Application.Current?.Resources["LineBrush"] as IBrush??Brushes.LightGray;var pen=new Pen(brush,0.6);
        var offsetX=(Pan.X%spacing+spacing)%spacing;var offsetY=(Pan.Y%spacing+spacing)%spacing;
        for(var x=offsetX;x<Bounds.Width;x+=spacing)context.DrawLine(pen,new(x,0),new(x,Bounds.Height));
        for(var y=offsetY;y<Bounds.Height;y+=spacing)context.DrawLine(pen,new(0,y),new(Bounds.Width,y));
    }
}
