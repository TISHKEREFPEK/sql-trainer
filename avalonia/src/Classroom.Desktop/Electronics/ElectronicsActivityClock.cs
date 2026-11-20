using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

// Counts only observed foreground intervals. Sleep, a stalled dispatcher and
// wall-clock jumps never turn into inferred work. The caller persists each span
// before this clock forgets it; restart begins a new observation, without catchup.
internal sealed class ElectronicsActivityClock(Action<ElectronicsActiveSpan> persist)
{
    long? previousTick,lastInput;
    long previousWall;
    bool previousEnabled,marked;
    ElectronicsActiveSpan? pending;
    public void Input(long tick,long wall,bool enabled)
    {
        // Frequent typing/dragging refreshes the idle deadline without writing
        // tiny intervals for every input event. Only an idle/focus boundary needs
        // an immediate sample; the regular timer measures continuous work.
        if(previousTick is null||enabled!=previousEnabled||lastInput is null||tick-lastInput>=120000)Sample(tick,wall,enabled);
        lastInput=tick;
    }
    void Flush()
    {if(pending is not null){persist(pending);pending=null;}}
    public void Sample(long tick,long wall,bool enabled,bool flush=false)
    {
        if(wall<=0)throw new InvalidDataException("Некорректное время активности.");
        if(pending?.EndedAt>wall)pending=null; // A clock rollback makes this unfinished span uncertain.
        if(previousTick is { }prior)
        {
            var elapsed=tick-prior;var wallElapsed=wall-previousWall;
            if(previousEnabled&&elapsed>0&&elapsed<=5000&&Math.Abs(wallElapsed-elapsed)<=250&&lastInput is { }input)
            {
                var end=Math.Min(tick,input+120000);var duration=Math.Min(Math.Max(0,end-prior),Math.Max(0,wallElapsed));
                if(duration>0)
                {
                    if(pending is not null&&pending.EndedAt!=previousWall)Flush();
                    pending=new(pending?.StartedAt??previousWall,previousWall+duration);
                    if(pending.EndedAt-pending.StartedAt>=10000||end<tick)Flush();
                }
            }
            else Flush();
        }
        if(enabled&&!marked)
        {persist(new(wall,wall));marked=true;lastInput??=tick;}
        previousTick=tick;previousWall=wall;previousEnabled=enabled;
        if(!enabled||flush)Flush();
    }
}
