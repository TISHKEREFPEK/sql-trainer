using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsActivityTests
{
    const long At=1_000_000;
    [Fact]
    public void CountsOnlyObservedEnabledTimeAndStopsAtIdleUntilNewInput()
    {
        var saved=new List<ElectronicsActiveSpan>();var clock=new ElectronicsActivityClock(saved.Add);clock.Sample(0,At,true);
        for(var tick=2000;tick<=130000;tick+=2000)clock.Sample(tick,At+tick,true);
        Assert.Equal(120000,saved.Sum(s=>s.EndedAt-s.StartedAt));
        clock.Input(130000,At+130000,true);clock.Sample(132000,At+132000,false);clock.Sample(140000,At+140000,false);
        Assert.Equal(122000,saved.Sum(s=>s.EndedAt-s.StartedAt));Assert.All(saved,s=>Assert.InRange(s.EndedAt-s.StartedAt,0,15000));
    }
    [Fact]
    public void SleepClockRollbackAndRestartNeverFillUnobservedGaps()
    {
        var saved=new List<ElectronicsActiveSpan>();var clock=new ElectronicsActivityClock(saved.Add);
        clock.Sample(0,At,true);clock.Sample(2000,At+2000,true);clock.Sample(3600000,At+3600000,true);
        Assert.Equal(2000,saved.Sum(s=>s.EndedAt-s.StartedAt));clock.Input(3600000,At+3600000,true);clock.Sample(3602000,At+3602000,false);
        Assert.Equal(4000,saved.Sum(s=>s.EndedAt-s.StartedAt));
        var rollback=new ElectronicsActivityClock(saved.Add);rollback.Sample(0,At,true);rollback.Sample(2000,At+2000,true);rollback.Sample(4000,At-10000,true);rollback.Sample(6000,At-8000,false);
        Assert.Equal(6000,saved.Sum(s=>s.EndedAt-s.StartedAt));
        var restarted=new ElectronicsActivityClock(saved.Add);restarted.Sample(9999999,At+9999999,true);restarted.Sample(10001999,At+10001999,false);
        Assert.Equal(8000,saved.Sum(s=>s.EndedAt-s.StartedAt));
    }
    [Fact]
    public void SmallClockRoundingDifferencesNeverProduceAnIntervalInTheFuture()
    {
        var saved=new List<ElectronicsActiveSpan>();var now=At;var clock=new ElectronicsActivityClock(s=>{Assert.True(s.EndedAt<=now);saved.Add(s);});clock.Sample(0,now,true);
        for(var i=1;i<=20;i++){now+=i%2==0?2001:1999;clock.Sample(i*2000,now,i<20);}
        Assert.InRange(saved.Sum(s=>s.EndedAt-s.StartedAt),39980,40000);
    }
    [Fact]
    public void FrequentInputRefreshesIdleWithoutWritingThousandsOfTinyIntervals()
    {
        var saved=new List<ElectronicsActiveSpan>();var clock=new ElectronicsActivityClock(saved.Add);clock.Sample(0,At,true);
        for(var i=1;i<=2000;i++)clock.Input(i,At+i+i%2,true);
        clock.Sample(2000,At+2000,false);Assert.Equal(2,saved.Count);Assert.Equal(2000,saved.Sum(s=>s.EndedAt-s.StartedAt));
    }
    [Fact]
    public void FailedLocalPersistenceRetainsTheMeasuredSpanForRetry()
    {
        var saved=new List<ElectronicsActiveSpan>();var fail=true;
        var clock=new ElectronicsActivityClock(s=>{if(s.EndedAt>s.StartedAt&&fail){fail=false;throw new IOException("synthetic full disk");}saved.Add(s);});
        clock.Sample(0,At,true);for(var tick=2000;tick<10000;tick+=2000)clock.Sample(tick,At+tick,true);
        Assert.Throws<IOException>(()=>clock.Sample(10000,At+10000,true));Assert.Single(saved);
        clock.Sample(12000,At+12000,false);Assert.Contains(new ElectronicsActiveSpan(At,At+10000),saved);
        Assert.All(saved,s=>Assert.InRange(s.EndedAt-s.StartedAt,0,15000));
    }
    [Fact]
    public void ActivityIsOptionalInOldJsonAndCannotAlterCodeOrClaimUnboundedTime()
    {
        var json=CircuitProject.Blink().Serialize();var journal=ElectronicsJournal.Start(json,At);
        var old=ElectronicsJournal.Record(journal,json,json,"work-open",At,force:true);Assert.DoesNotContain("activeSpan",Wire.Write(old));Assert.Equal(json,Wire.Read<ElectronicsJournal>(Wire.Write(old)).Replay());
        var known=ElectronicsJournal.Record(journal,json,json,"activity",At+10000,force:true,activeSpan:new(At,At+10000));Assert.Equal(json,known.Replay());
        foreach(var span in new[]{new ElectronicsActiveSpan(0,1),new(At+1,At),new(At,At+15001),new(At,At+20000)})
            Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(journal,json,json,"activity",At+10000,force:true,activeSpan:span));
        Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(journal,json,json,"work-open",At+10000,force:true,activeSpan:new(At,At+1000)));
        var changed=CircuitProject.Blink();changed.Code="// invalid activity edit";
        Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(journal,json,changed.Serialize(),"activity",At+10000,force:true,activeSpan:new(At,At+1000)));
        Assert.Throws<InvalidDataException>(()=>(known with{Changes=[known.Changes[0] with{ActiveSpan=new(At,At+100000)}]}).Replay());
    }
}
