using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;
namespace Classroom.UiTests;
public sealed class ElectronicsSyncTests
{
    [Fact]
    public async Task DrainWaitsForInflightHistoryAndContinuesItsRemainingBatches()
    {
        await using var f=await LiveClassroom.Start();var root=Path.Combine(Path.GetTempPath(),"classroom-history-drain-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(root,"queue.json");
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var acknowledged=0;var total=3;var calls=0;
        using var sync=new ElectronicsSync(f.Student,path,_=>{},startTimer:false,journalPending:()=>acknowledged<total,journalDelivery:async token=>
        {
            if(Interlocked.Increment(ref calls)==1){started.SetResult();await release.Task.WaitAsync(token);}
            acknowledged=Math.Min(total,acknowledged+3);return acknowledged==total;
        });
        var project=CircuitProject.Blink().Serialize();sync.QueueDraft(project);var originalClient=sync.ClientId;var inFlight=sync.Flush();await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        total=10;var drain=sync.Drain();Assert.False(drain.IsCompleted);release.SetResult();await inFlight;
        Assert.True(await drain);Assert.False(sync.Pending);Assert.Equal(10,acknowledged);Assert.Equal(4,calls);Assert.Equal(originalClient,sync.ClientId);
    }
    [Fact]
    public async Task FailedWindowsQueueWriteBlocksNetworkUntilTheSameIdsAreDurable()
    {
        if(!OperatingSystem.IsWindows()) return;
        await using var f=await LiveClassroom.Start(); var root=Path.Combine(Path.GetTempPath(),"classroom-durable-outbox-"+Guid.NewGuid().ToString("N")); var path=Path.Combine(root,"queue.json");
        var project=CircuitProject.Blink(); var original=project.Serialize(); var report=Guid.NewGuid().ToString("N");
        using var sync=new ElectronicsSync(f.Student,path,_=>{},startTimer:false); sync.QueueDraft(original); sync.Record("submitted",original,reportId:report); var client=sync.ClientId;
        project.Code="// правка при отказе записи"; var latest=project.Serialize();
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var failure=Assert.ThrowsAny<Exception>(()=>sync.QueueDraft(latest)); Assert.Contains(failure.HResult&0xffff,new[]{5,32,33});
            Assert.False(await sync.Flush()); Assert.True(sync.Pending);
            Assert.Empty((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works);
        }
        Assert.True(await sync.Flush()); Assert.Equal(client,ElectronicsSync.ReadOutbox(path,null).ClientId);
        var teacher=await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}"); Assert.Equal(client,Assert.Single(teacher.Works).ClientId); Assert.Equal(report,Assert.Single(teacher.Reports).Id);
        Assert.Equal(latest,(await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/work/{client}")).ProjectJson);
    }
    [Fact]
    public async Task OfflineOutboxSurvivesRestartAndReportsKeepOriginalSnapshots()
    {
        await using var f=await LiveClassroom.Start();
        var dir=Path.Combine(Path.GetTempPath(),"classroom-outbox-"+Guid.NewGuid().ToString("N")); var path=Path.Combine(dir,"queue.json");
        var project=CircuitProject.Blink(); var original=project.Serialize(); project.Code="void setup(){} void loop(){}"; var changed=project.Serialize();
        using(var offlineApi=new ClassroomApi(new("https://127.0.0.1:1",new string('0',64))))
        using(var offline=new ElectronicsSync(offlineApi,path,_=>{},false))
        { offline.QueueDraft(original); offline.Record("submitted",original); offline.QueueDraft(changed); Assert.False(await offline.Flush()); Assert.True(offline.Pending); }
        using(var online=new ElectronicsSync(f.Student,path,_=>{},false))
        { online.QueueDraft(changed); Assert.True(await online.Flush()); Assert.False(online.Pending); Assert.True(await online.Flush()); }
        var info=await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}"); Assert.Single(info.Works); Assert.Single(info.Reports);
        Assert.Equal(original,(await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/report/{info.Reports[0].Id}")).ProjectJson);
        Assert.Equal(changed,(await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/work/{info.Works[0].ClientId}")).ProjectJson);
        using(var again=new ElectronicsSync(f.Student,path,_=>{},false)) { again.QueueDraft(changed); Assert.True(await again.Flush()); }
        Assert.Single((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Reports);
        using(var concurrent=new ElectronicsSync(f.Student,path,_=>{},false))
        {
            concurrent.QueueDraft(changed); var inFlight=concurrent.Flush(); project.Name="Правка во время передачи"; var newest=project.Serialize(); concurrent.QueueDraft(newest); concurrent.Record("compiled",newest);
            Assert.True(await concurrent.Drain()); await inFlight;
            Assert.Equal(newest,(await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/work/{info.Works[0].ClientId}")).ProjectJson);
        }
        File.WriteAllText(path,"broken");
        Assert.Throws<System.Text.Json.JsonException>(()=>new ElectronicsSync(f.Student,path,_=>{},false));
        Assert.Equal("broken",File.ReadAllText(path)); Assert.Empty(Directory.GetFiles(dir,"*.corrupt-*"));
        Assert.Single((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works);
    }
}
