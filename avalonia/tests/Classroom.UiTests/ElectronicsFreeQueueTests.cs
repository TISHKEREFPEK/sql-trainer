using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using System.Text.Json;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsFreeQueueTests
{
    static string Root()=>Path.Combine(Path.GetTempPath(),"classroom-free-queue-"+Guid.NewGuid().ToString("N"));
    static ElectronicsHistoryContext Context(LiveClassroom f,string id)=>new(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null,id);

    [Fact]
    public async Task LostAckThenMoreEditsReplaysTheDurableRequestBeforeTheNewDraft()
    {
        await using var f=await LiveClassroom.Start();var root=Root();Directory.CreateDirectory(root);
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var file=Path.Combine(root,"queue.json");
            var project=CircuitProject.Blink();var initial=project.Serialize();
            await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(initial));
            using(var offlineApi=new ClassroomApi(new("https://127.0.0.1:1",new string('0',64))))
            using(var offline=new ElectronicsSync(offlineApi,file,_=>{},false,freeContext:context.FreeContext()))
            {
                offline.QueueDraft(initial);offline.Record("compiled",initial);
                Assert.False(await offline.Flush());
                project.Code="// edits after uncertain delivery";offline.QueueDraft(project.Serialize());
            }
            var disk=ElectronicsSync.ReadOutbox(file,null,freeContext:context.FreeContext());var sent=Assert.IsType<ElectronicsSyncRequest>(disk.InFlight);
            Assert.Equal(initial,sent.ProjectJson);Assert.NotEqual(sent.Sequence,disk.Sequence);
            await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",sent); // The response was never recorded on disk.
            using(var resumed=new ElectronicsSync(f.Student,file,_=>{},false,freeContext:context.FreeContext()))
            {Assert.True(await resumed.Drain());Assert.False(resumed.Pending);Assert.Equal(2,resumed.FreeContext!.ExpectedRevision);}
            var accepted=ElectronicsSync.ReadOutbox(file,null,freeContext:context.FreeContext());
            Assert.Null(accepted.InFlight);Assert.Empty(accepted.Reports);Assert.Equal(disk.ClientId,accepted.ClientId);
            Assert.Equal(project.Serialize(),(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson);
            Assert.Single((await f.Admin.Get<ElectronicsStudentDetail>("teacher/electronics/"+f.StudentId)).Reports);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task RecoveryPreservesFreeBindingAndOriginalSubmissionIdentity()
    {
        await using var f=await LiveClassroom.Start();var root=Root();
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var project=CircuitProject.Blink();var initial=project.Serialize();
            await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(initial));
            var path=Path.Combine(root,"electronics",context.ProjectFileName);var report=Guid.NewGuid().ToString("N");string client;
            using(var history=new ElectronicsHistoryStore(path+".history",context,initial))
            using(var sync=new ElectronicsSync(f.Student,path+".sync.json",_=>{},false,freeContext:context.FreeContext()))
            {
                sync.QueueDraft(initial);sync.Record("submitted",initial,expectedAssignmentVersion:1,reportId:report);client=sync.ClientId;
                project.Code="// later local draft";history.Record(project.Serialize(),"code-edit");sync.QueueDraft(project.Serialize());
            }
            File.WriteAllText(path+".sync.json","broken");
            var candidate=ElectronicsOutboxRecovery.Inspect(path,context,"Broken queue");
            Assert.NotNull(candidate.Backup);Assert.Equal(context.FreeContext(),candidate.Backup.FreeContext);
            Assert.Equal(report,Assert.Single(candidate.Backup.Reports).Id);
            Assert.Null(ElectronicsOutboxRecovery.Inspect(path,context with{LessonId=Guid.NewGuid().ToString("N")},"Wrong lesson").Backup);
            var damaged=ElectronicsOutboxRecovery.Restore(candidate);Assert.NotNull(damaged);Assert.Equal("broken",File.ReadAllText(damaged));
            using(var restored=new ElectronicsSync(f.Student,path+".sync.json",_=>{},false,freeContext:context.FreeContext(9)))
            {
                Assert.Equal(client,restored.ClientId);Assert.Equal(0,restored.FreeContext!.ExpectedRevision);
                restored.QueueDraft(project.Serialize());Assert.True(await restored.Flush());
            }
            Assert.Equal(report,Assert.Single((await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?projectId="+id)).Items).ReportId);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public void LegacyContextSerializationAndFileIdentityStayByteCompatible()
    {
        var legacy=new ElectronicsHistoryContext(Guid.NewGuid().ToString("N"),new string('A',64),Guid.NewGuid().ToString("N"),null);
        Assert.Equal(Wire.Write(new{legacy.ProfileId,legacy.Fingerprint,legacy.LessonId,legacy.AssignmentId}),Wire.Write(legacy));
        Assert.Equal(SqlSyncHash.Text(legacy.Fingerprint+":"+legacy.ProfileId)+".json",legacy.ProjectFileName);
        var free=legacy with{FreeProjectId=Guid.NewGuid().ToString("N")};
        Assert.NotEqual(free.ProjectFileName,(free with{LessonId=Guid.NewGuid().ToString("N")}).ProjectFileName);
    }

    [Fact]
    public async Task LostAckNeverAdoptsAnotherDevicesHeadOrRebindsAnExistingQueue()
    {
        await using var f=await LiveClassroom.Start();var root=Root();Directory.CreateDirectory(root);
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var file=Path.Combine(root,"queue.json");
            var project=CircuitProject.Blink();var json=project.Serialize();
            await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(json));
            using(var sync=new ElectronicsSync(f.Student,file,_=>{},false,freeContext:context.FreeContext()))
            {sync.QueueDraft(json);sync.Record("submitted",json,expectedAssignmentVersion:1);}
            var saved=ElectronicsSync.ReadOutbox(file,null,freeContext:context.FreeContext());
            await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(saved.ClientId,saved.Sequence,saved.ProjectJson,saved.Reports.ToArray(),FreeContext:saved.FreeContext));
            project.Code="// another device";var remote=project.Serialize();
            await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,remote,[],FreeContext:context.FreeContext(1)));
            using(var sync=new ElectronicsSync(f.Student,file,_=>{},false,freeContext:context.FreeContext(2)))
            {
                Assert.Equal(0,sync.FreeContext!.ExpectedRevision);Assert.Equal(saved.ClientId,sync.ClientId);
                Assert.True(await sync.Flush());Assert.Equal(1,sync.FreeContext.ExpectedRevision);Assert.False(sync.PendingSubmission);
                project.Code="// local continuation";sync.QueueDraft(project.Serialize());
                Assert.False(await sync.Flush());Assert.True(sync.Pending);Assert.NotNull(sync.DeliveryError);
            }
            var pending=ElectronicsSync.ReadOutbox(file,null,freeContext:context.FreeContext());
            Assert.Equal(1,pending.FreeContext!.ExpectedRevision);Assert.Equal(saved.ClientId,pending.ClientId);Assert.Equal(project.Serialize(),pending.ProjectJson);
            var bytes=File.ReadAllBytes(file);
            Assert.Throws<InvalidDataException>(()=>new ElectronicsSync(f.Student,file,_=>{},false));
            Assert.Throws<InvalidDataException>(()=>new ElectronicsSync(f.Student,file,_=>{},false,freeContext:context.FreeContext()! with{LessonId=Guid.NewGuid().ToString("N")}));
            Assert.Equal(bytes,File.ReadAllBytes(file));
            Assert.Equal(remote,(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task ClosedFreeWorkAndItsHistoryDeliverToOriginalLessonDuringTheNextLesson()
    {
        await using var f=await LiveClassroom.Start();var root=Root();
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var project=CircuitProject.Blink();
            await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(project.Serialize()));
            var path=Path.Combine(root,"electronics",context.ProjectFileName);var report=Guid.NewGuid().ToString("N");
            using(var history=new ElectronicsHistoryStore(path+".history",context,project.Serialize()))
            using(var sync=new ElectronicsSync(f.Student,path+".sync.json",_=>{},false,freeContext:context.FreeContext()))
            {
                project.Code="// saved first lesson";var json=project.Serialize();history.Record(json,"code-edit");
                history.Record(json,"report-submitted",true,reportId:report,reportHash:SqlSyncHash.Text(json));
                ElectronicsHistoryStore.Mirror(path,json);sync.QueueDraft(json);sync.Record("submitted",json,expectedAssignmentVersion:1,reportId:report);
            }
            await f.Admin.Post<LessonView>($"teacher/lessons/{context.LessonId}/finish");
            var next=await f.Admin.Post<LessonView>("teacher/lessons",new CreateLessonRequest(Guid.NewGuid().ToString("N"),new(null,AllowElectronics:false,OpenToAllGroups:true,FreePractice:true)));
            await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start");await f.Student.Login(new("student","ui-test","synthetic-student-password"));
            await using(var delivery=new ElectronicsBackgroundDelivery(root,f.Student))
                for(var i=0;i<4;i++)Assert.Null((await delivery.DrainOnce()).Error);
            var item=Assert.Single((await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?projectId="+id)).Items);
            Assert.Equal(report,item.ReportId);Assert.Equal(context.LessonId,item.LessonId);
            var stream=Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}"));
            Assert.Equal(context.LessonId,stream.LessonId);Assert.Equal(2L,stream.Sequence);
            Assert.Equal(1,ElectronicsSync.ReadOutbox(path+".sync.json",null,freeContext:context.FreeContext()).FreeContext!.ExpectedRevision);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
