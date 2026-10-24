using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;
namespace Classroom.UiTests;
public sealed class ElectronicsSyncTests
{
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
        using(var recovered=new ElectronicsSync(f.Student,path,_=>{},false)){recovered.QueueDraft(changed); Assert.Single(Directory.GetFiles(dir,"*.corrupt-*")); Assert.True(File.Exists(path));}
    }
}
