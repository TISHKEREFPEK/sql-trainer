using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsActivityApiTests
{
    static async Task<ElectronicsJournalBatch> Send(LiveClassroom f,string client,string json,string? assignment,ElectronicsActiveSpan[] spans,string? parent=null)
    {
        var at=spans[0].StartedAt;var journal=ElectronicsJournal.Start(json,at);
        foreach(var span in spans)journal=ElectronicsJournal.Record(journal,json,json,"activity",span.EndedAt,force:true,activeSpan:span);
        var batch=new ElectronicsJournalBatch(journal.StreamId,client,f.Student.LessonId!,assignment,at,json,journal.Changes.Length,journal.Changes,parent);
        var first=await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync",batch);Assert.Equal(first,await f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync",batch));return batch;
    }
    static async Task<(string Id,string Client,string Json)> Free(LiveClassroom f)
    {
        var id=Guid.NewGuid().ToString("N");var json=CircuitProject.Blink().Serialize();await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(json));
        var opened=await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{id}/open",new ElectronicsProjectOpen(f.Student.LessonId!));var client=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,1,json,[],FreeContext:new(id,f.Student.LessonId!,opened.Revision)));return(id,client,json);
    }
    static Task<ResultsOverview> Results(LiveClassroom f,string? lesson=null)=>f.Admin.Get<ResultsOverview>(lesson is null?"teacher/results?mode=course":"teacher/results?mode=lesson&lessonId="+lesson);
    [Fact]
    public async Task IntervalsUnionAcrossForksAndCopiesWithoutMixingLessonsAssignmentsOrProjects()
    {
        await using var f=await LiveClassroom.Start();var first=f.Student.LessonId!;var a=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-60000;var p=await Free(f);
        var spans=new[]{new ElectronicsActiveSpan(a,a+10000),new(a+5000,a+15000)};var batch=await Send(f,p.Client,p.Json,null,spans);
        await Send(f,p.Client,p.Json,null,spans,batch.StreamId);
        var unknown=await Free(f);var zero=await Free(f);await Send(f,zero.Client,zero.Json,null,[new(a,a)]);
        var task=Guid.NewGuid().ToString("N");await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+task,new ElectronicsTaskRequest("Измерение активности","Соберите схему",p.Json));
        await f.Admin.Put<ElectronicsSelection>("teacher/lessons/"+first+"/electronics-tasks",new ElectronicsSelectionRequest([task]));var assigned=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+task);var copy=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(copy,1,p.Json,[],assigned.Id));await Send(f,copy,p.Json,assigned.Id,[new(a,a+5000)]);
        var lesson=await Results(f,first);Assert.Equal(15,Assert.Single(lesson.Works,w=>w.FreeProjectId==p.Id).ActiveSeconds);Assert.Null(Assert.Single(lesson.Works,w=>w.FreeProjectId==unknown.Id).ActiveSeconds);
        Assert.Equal(0,Assert.Single(lesson.Works,w=>w.FreeProjectId==zero.Id).ActiveSeconds);Assert.Equal(5,Assert.Single(lesson.Works,w=>w.AssignmentId==assigned.Id).ActiveSeconds);
        await f.Admin.Post<LessonView>($"teacher/lessons/{first}/finish");var next=await f.Admin.Post<LessonView>("teacher/lessons",new CreateLessonRequest(Guid.NewGuid().ToString("N"),new(null,OpenToAllGroups:true,FreePractice:true)));await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start");await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var opened=await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{p.Id}/open",new ElectronicsProjectOpen(next.Id));var second=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(second,1,p.Json,[],FreeContext:new(p.Id,next.Id,opened.Revision)));await Send(f,second,p.Json,null,[new(a+14000,a+24000)]);
        Assert.Equal(15,Assert.Single((await Results(f,first)).Works,w=>w.FreeProjectId==p.Id).ActiveSeconds);
        Assert.Equal(10,Assert.Single((await Results(f,next.Id)).Works,w=>w.FreeProjectId==p.Id).ActiveSeconds);
        var course=await Results(f);Assert.Equal(24,Assert.Single(course.Works,w=>w.FreeProjectId==p.Id).ActiveSeconds);Assert.Null(Assert.Single(course.Works,w=>w.FreeProjectId==unknown.Id).ActiveSeconds);
        var csv=await f.Admin.Get<ResultsCsv>("teacher/results/csv?mode=course");Assert.Contains("\"24\"",csv.Csv);
    }
    [Fact]
    public async Task InvalidActivityIsAtomicAndBackupRejectsProjectionCorruption()
    {
        await using var f=await LiveClassroom.Start();var p=await Free(f);var at=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-20000;
        var batch=await Send(f,p.Client,p.Json,null,[new(at,at+10000)]);
        var bad=batch with{KnownSequence=2,Changes=[batch.Changes[0] with{Sequence=2,Id=Guid.NewGuid().ToString("N"),ActiveSpan=new(at,at+100000)}]};
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Post<ElectronicsJournalAck>("electronics-journal/sync",bad))).Status);
        Assert.Equal(10,Assert.Single((await Results(f)).Works,w=>w.FreeProjectId==p.Id).ActiveSeconds);
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");var name=backup.GetProperty("name").GetString()!;await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(name)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        Assert.Equal(10,Assert.Single((await Results(f)).Works,w=>w.FreeProjectId==p.Id).ActiveSeconds);
        var saved=Path.Combine(Path.GetDirectoryName(f.DatabasePath)!,"backups",name);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=saved,Pooling=false}.ToString()))
        {db.Open();using var command=db.CreateCommand();command.CommandText="UPDATE ElectronicsJournalChanges SET ActivityEndedAt=ActivityEndedAt+1000 WHERE ActivityEndedAt IS NOT NULL";command.ExecuteNonQuery();}
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(()=>f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(name)+"/restore"))).Status);
        Assert.Equal(10,Assert.Single((await Results(f)).Works,w=>w.FreeProjectId==p.Id).ActiveSeconds);
    }
}
