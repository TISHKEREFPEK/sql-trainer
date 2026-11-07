using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsProjectApiTests
{
    [Fact]
    public async Task SameProjectNumberNeverGrantsAccessToAnotherStudentsWork()
    {
        await using var f=await LiveClassroom.Start();
        var id=Guid.NewGuid().ToString("N");var initial=CircuitProject.Blink();initial.Code="// first pupil";
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(initial.Serialize()));
        var created=await f.Admin.Post<JsonElement>("teacher/students",new CreateStudentRequest("other-project-pupil","synthetic-other-password",null));
        using var other=new ClassroomApi(f.Admin.Connection);await other.Login(new("student","other-project-pupil","synthetic-other-password"));
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>other.Get<ElectronicsProjectDto>("electronics/projects/"+id))).Status);
        Assert.Empty((await other.Get<ElectronicsProjectList>("electronics/projects")).Projects);
        initial.Code="// second pupil";
        await other.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(initial.Serialize()));
        Assert.NotEqual(initial.Serialize(),(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson);
        await f.Admin.Delete<JsonElement>("teacher/students/"+created.GetProperty("id").GetString());
        Assert.Single((await f.Student.Get<ElectronicsProjectList>("electronics/projects")).Projects);
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>f.Admin.Get<ElectronicsProjectDto>("electronics/projects/"+id))).Status);
        await f.Admin.Post<JsonElement>("teacher/backups");
    }

    [Fact]
    public async Task LateDraftKeepsItsOriginalLessonAndDoesNotReplaceAnotherLessonsSnapshot()
    {
        await using var f=await LiveClassroom.Start();
        var first=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var id=Guid.NewGuid().ToString("N");var project=CircuitProject.Blink();
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(project.Serialize()));
        await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{id}/open",new ElectronicsProjectOpen(first.Id));
        var old=new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,project.Serialize(),[],FreeContext:new(id,first.Id,0));
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",old);
        await f.Admin.Post<LessonView>($"teacher/lessons/{first.Id}/finish");
        var next=await f.Admin.Post<LessonView>("teacher/lessons",new CreateLessonRequest(Guid.NewGuid().ToString("N"),new(null,OpenToAllGroups:true,FreePractice:true)));
        await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start");
        await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{id}/open",new ElectronicsProjectOpen(next.Id));
        project.Code="// second lesson";var second=project.Serialize();
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,second,[],FreeContext:new(id,next.Id,1)));
        project.Code="// late first lesson";var late=project.Serialize();
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",old with { Sequence=2,ProjectJson=late,FreeContext=new(id,first.Id,2) });
        Assert.Equal(late,await Snapshot(first.Id));Assert.Equal(second,await Snapshot(next.Id));
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",old with { Sequence=3,FreeContext=new(id,next.Id,3) }))).Status);
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",old with { ClientId=Guid.NewGuid().ToString("N"),FreeContext=new(id,Guid.NewGuid().ToString("N"),3) }))).Status);
        await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/pause");
        Assert.Equal(423,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{id}/open",new ElectronicsProjectOpen(next.Id)))).Status);
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");
        await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");
        await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        Assert.Equal(late,await Snapshot(first.Id));Assert.Equal(second,await Snapshot(next.Id));

        async Task<string> Snapshot(string lesson)
        {
            await using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=f.DatabasePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
            await db.OpenAsync();await using var command=db.CreateCommand();
            command.CommandText="SELECT ProjectJson FROM ElectronicsPractice WHERE StudentId=$student AND ProjectId=$project AND LessonId=$lesson";
            command.Parameters.AddWithValue("$student",f.StudentId);command.Parameters.AddWithValue("$project",id);command.Parameters.AddWithValue("$lesson",lesson);
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }

    [Fact]
    public async Task FreeProjectResubmissionRequiresCurrentPermissionAndSurvivesBackupRestore()
    {
        await using var f=await LiveClassroom.Start();var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var id=Guid.NewGuid().ToString("N");var json=CircuitProject.Blink().Serialize();var report=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(json));
        var input=new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,json,[new(report,"submitted",json,"",1)],FreeContext:new(id,lesson.Id,0));
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",input);
        await Decide(report,"accepted",5);await Decide(report,"allow-resubmit",null);
        var permission=await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id);Assert.True(permission.CanSubmit);Assert.Equal(4,permission.StateVersion);
        var second=Guid.NewGuid().ToString("N");var stale=input with { Reports=[new(second,"submitted",json,"",1)] };
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",stale))).Status);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",stale with { Reports=[new(second,"submitted",json,"",permission.StateVersion)] });
        await Decide(report,"rejected",2);
        Assert.Equal("awaiting-review",(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).State);
        await Decide(second,"changes-requested",null);
        var expected=await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id);
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");
        await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");
        await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/resume");
        await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        Assert.Equal(expected,await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id));
        await f.Restart();await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        var detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{second}");
        Assert.Equal(json,detail.ProjectJson);Assert.True(detail.CanSubmit);Assert.Equal("changes-requested",detail.Item.State);
        var saved=Path.Combine(Path.GetDirectoryName(f.DatabasePath)!,"backups",backup.GetProperty("name").GetString()!);
        await using(var damaged=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=saved,Pooling=false}.ToString()))
        {
            await damaged.OpenAsync();await using var command=damaged.CreateCommand();command.CommandText="UPDATE ElectronicsProjects SET ProjectHash='corrupted'";await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(()=>f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore"))).Status);
        Assert.Equal(json,(await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{second}")).ProjectJson);

        async Task Decide(string reportId,string action,int? grade)
        {
            var route=$"teacher/reviews/electronics/{f.StudentId}/{reportId}";
            var detail=await f.Admin.Get<ElectronicsReviewDetail>(route);
            await f.Admin.Put<ElectronicsReviewDecision>(route+"/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest(action,"Review",grade,detail.History.LastOrDefault()?.Id,detail.AssignmentVersion));
        }
    }

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
