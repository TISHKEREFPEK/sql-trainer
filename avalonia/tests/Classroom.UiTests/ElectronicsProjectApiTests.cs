using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsProjectApiTests
{
    [Fact]
    public async Task SharedDraftRejectsStaleDevicesAndOldReceiptsKeepTheirAcceptedRevision()
    {
        await using var f = await LiveClassroom.Start();
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var id = Guid.NewGuid().ToString("N");
        var initial = CircuitProject.Blink().Serialize();
        var created = await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(initial));
        Assert.Equal(0,created.Revision);
        Assert.Equal(id,(await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(initial))).Id);
        var changed = CircuitProject.Blink(); changed.Code="// first device";
        var first = new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,changed.Serialize(),[],FreeContext:new(id,lesson.Id,0));
        Assert.Equal(1,(await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",first)).ProjectRevision);
        var second = first with { ClientId=Guid.NewGuid().ToString("N") };
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",second))).Status);
        changed.Code="// second device";
        second=second with { ProjectJson=changed.Serialize(),FreeContext=new(id,lesson.Id,1) };
        Assert.Equal(2,(await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",second)).ProjectRevision);
        Assert.Equal(1,(await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",first)).ProjectRevision);
        Assert.Equal(changed.Serialize(),(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson);
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",first with { Sequence=2 }))).Status);
        var otherId=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+otherId,new ElectronicsProjectCreate(initial));
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",first with { FreeContext=new(otherId,lesson.Id,0) }))).Status);
        Assert.Equal(2,(await f.Student.Get<ElectronicsProjectList>("electronics/projects")).Projects.Length);
    }

    [Fact]
    public async Task SubmissionIsImmutableAndRetriesDoNotConsumePermissionAgain()
    {
        await using var f=await LiveClassroom.Start();
        var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var id=Guid.NewGuid().ToString("N");var json=CircuitProject.Blink().Serialize();
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(json));
        var report=new ElectronicsReportRequest(Guid.NewGuid().ToString("N"),"submitted",json,"",1);
        var input=new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,json,[report],FreeContext:new(id,lesson.Id,0));
        var receipt=await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",input);
        Assert.False(receipt.CanSubmit);Assert.Equal(2,receipt.AssignmentVersion);
        var retry=await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",input);
        Assert.Equal(receipt.ProjectRevision,retry.ProjectRevision);Assert.Equal(receipt.ReportIds,retry.ReportIds);Assert.Equal(receipt.AssignmentVersion,retry.AssignmentVersion);
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",input with { Reports=[report with { Id=Guid.NewGuid().ToString("N") }] }))).Status);
        var queue=await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?projectId="+id);
        var item=Assert.Single(queue.Items);Assert.Equal(id,item.FreeProjectId);Assert.Equal(lesson.Id,item.LessonId);
        Assert.Equal("awaiting-review",item.State);
    }
}
