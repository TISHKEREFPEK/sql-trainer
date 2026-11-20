using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    readonly DispatcherTimer activityTimer=new(){Interval=TimeSpan.FromSeconds(2)};
    ElectronicsActivityClock? activity;
    bool ActivityAllowed=>!disposed&&historyVisible&&IsEffectivelyEnabled&&owner.IsActive&&owner.WindowState!=Avalonia.Controls.WindowState.Minimized;
    void ConfigureActivity()
    {
        // Legacy files have no reliable project/lesson attribution in results.
        if(history is null||assignment is null&&freeProject is null)return;
        activity=new(span=>history.Record(history.ProjectJson,"activity",true,activeSpan:span));
        activityTimer.Tick+=(_,_)=>_ = Perform(()=>SampleActivity());
        void Input(){if(!disposed)_ = Perform(()=>activity.Input(Environment.TickCount64,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),ActivityAllowed));}
        AddHandler(InputElement.KeyDownEvent,(_,_)=>Input(),RoutingStrategies.Tunnel,true);
        AddHandler(InputElement.PointerPressedEvent,(_,_)=>Input(),RoutingStrategies.Tunnel,true);
        AddHandler(InputElement.PointerWheelChangedEvent,(_,_)=>Input(),RoutingStrategies.Tunnel,true);
        AddHandler(InputElement.PointerMovedEvent,(_,e)=>{var p=e.GetCurrentPoint(this).Properties;if(p.IsLeftButtonPressed||p.IsMiddleButtonPressed||p.IsRightButtonPressed)Input();},RoutingStrategies.Tunnel,true);
        AttachedToVisualTree+=(_,_)=>{activityTimer.Start();_ = Perform(()=>SampleActivity());};
        DetachedFromVisualTree+=(_,_)=>{activityTimer.Stop();_ = Perform(()=>SampleActivity(false,true));};
        PropertyChanged+=(_,e)=>{if(e.Property==InputElement.IsEffectivelyEnabledProperty)_ = Perform(()=>SampleActivity(flush:!IsEffectivelyEnabled));};
    }
    void SampleActivity(bool? enabled=null,bool flush=false)
    {if(!disposed)activity?.Sample(Environment.TickCount64,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),enabled??ActivityAllowed,flush);}
}
