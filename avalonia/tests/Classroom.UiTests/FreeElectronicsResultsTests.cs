using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class FreeElectronicsResultsTests
{
    static Task<ResultsOverview> Results(LiveClassroom f,string? lesson=null,bool course=false)=>f.Admin.Get<ResultsOverview>("teacher/results?mode="+(course?"course":"lesson")+(lesson is null?"":"&lessonId="+lesson));
    static ResultsWork Work(ResultsOverview data,string id)=>Assert.Single(data.Works,w=>w.FreeProjectId==id);
    static async Task<LessonView> Next(LiveClassroom f,string group)
    {
        if((await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current is { } previous)await f.Admin.Post<LessonView>($"teacher/lessons/{previous.Id}/finish");
        var lesson=await f.Admin.Post<LessonView>("teacher/lessons",new CreateLessonRequest(Guid.NewGuid().ToString("N"),new(group,OpenToAllGroups:true,FreePractice:true)));
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks",new LessonSqlSelectionRequest(["database"]));
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/start");await f.Student.Login(new("student","ui-test","synthetic-student-password"));return lesson;
    }
    static async Task Decide(LiveClassroom f,string report,string action,int? grade)
    {
        var route=$"teacher/reviews/electronics/{f.StudentId}/{report}";var detail=await f.Admin.Get<ElectronicsReviewDetail>(route);
        await f.Admin.Put<ElectronicsReviewDecision>(route+"/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest(action,"Checked",grade,detail.History.LastOrDefault()?.Id,detail.AssignmentVersion));
    }
    [Fact]
    public async Task ProjectResultsPreserveLessonSnapshotsPriorCreditRosterAndMandatoryDenominator()
    {
        await using var f=await LiveClassroom.Start();var a=await f.Admin.Post<GroupDto>("teacher/groups",new GroupRequest("191"));var b=await f.Admin.Post<GroupDto>("teacher/groups",new GroupRequest("192"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group",new GroupMembershipRequest(a.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name",new StudentNameRequest("Историческое ФИО"));
        var first=await Next(f,a.Id);var baseline=(await Results(f,first.Id)).Summary;
        var id=Guid.NewGuid().ToString("N");var project=CircuitProject.Blink();project.Name="Первая схема";var original=project.Serialize();
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(original));
        await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{id}/open",new ElectronicsProjectOpen(first.Id));
        var report=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,original,
            [new(Guid.NewGuid().ToString("N"),"compile-error",original,"exact compiler error"),new(report,"submitted",original,"",1)],FreeContext:new(id,first.Id,0)));
        await Decide(f,report,"accepted",5);
        var initial=await Results(f,first.Id);var row=Work(initial,id);
        Assert.False(row.Required);Assert.True(row.Completed);Assert.Equal(5,row.Grade);Assert.Equal(1,row.Checks);Assert.Equal(1,row.Errors);
        Assert.NotNull(row.StartedAt);Assert.Null(row.ActiveSeconds);Assert.Null(row.Hints);Assert.Null(row.Violations);
        Assert.Equal(baseline.Total,initial.Summary.Total);Assert.Equal(baseline.Completed,initial.Summary.Completed);Assert.Equal(baseline.AveragePercent,initial.Summary.AveragePercent);
        Assert.DoesNotContain(initial.Difficulties,d=>d.Subject=="electronics");
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group",new GroupMembershipRequest(b.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name",new StudentNameRequest("Текущее ФИО"));
        var second=await Next(f,b.Id);await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{id}/open",new ElectronicsProjectOpen(second.Id));
        project.Name="Новое название";project.Code="// next lesson draft";var later=project.Serialize();var client=Guid.NewGuid().ToString("N");
        var draft=new ElectronicsSyncRequest(client,1,later,[],FreeContext:new(id,second.Id,1));await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",draft);
        var old=await Results(f,first.Id);Assert.Equal("Первая схема",Work(old,id).Title);Assert.Equal(5,Work(old,id).Grade);
        var historical=Assert.Single(old.Students);Assert.Equal("Историческое ФИО",historical.Name);Assert.Equal(a.Id,historical.GroupKey);
        var current=Work(await Results(f,second.Id),id);Assert.False(current.Completed);Assert.Null(current.Grade);Assert.Equal(0,current.Checks);
        var course=Work(await Results(f,course:true),id);Assert.True(course.Completed);Assert.Equal(5,course.Grade);Assert.Null(course.LessonId);
        Assert.Equal(original,(await f.Admin.Get<ResultsElectronicsDetail>($"teacher/results/electronics/{f.StudentId}/{id}?lessonId={first.Id}")).ProjectJson);
        Assert.Equal(later,(await f.Admin.Get<ResultsElectronicsDetail>($"teacher/results/electronics/{f.StudentId}/{id}?lessonId={second.Id}")).ProjectJson);
        await Decide(f,report,"allow-resubmit",null);var retry=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",draft with{Reports=[new(retry,"submitted",later,"",4)],FreeContext=new(id,second.Id,2)});
        current=Work(await Results(f,second.Id),id);Assert.Equal("awaiting-review",current.State);Assert.True(current.Completed);Assert.True(current.Reused);Assert.Equal(5,current.Grade);
        Assert.True(Work(await Results(f,course:true),id).Completed);
        await Decide(f,retry,"changes-requested",null);Assert.False(Work(await Results(f,second.Id),id).Completed);Assert.False(Work(await Results(f,course:true),id).Completed);
        Assert.Equal(5,Work(await Results(f,first.Id),id).Grade);
        await Decide(f,retry,"accepted",4);Assert.Equal(4,Work(await Results(f,course:true),id).Grade);Assert.Equal(5,Work(await Results(f,first.Id),id).Grade);
        var sameName=Guid.NewGuid().ToString("N");await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+sameName,new ElectronicsProjectCreate(later));
        var legacy=CircuitProject.Blink();legacy.Name="Legacy-only";
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,legacy.Serialize(),[new(Guid.NewGuid().ToString("N"),"submitted",legacy.Serialize(),"")]));
        var all=await Results(f,course:true);Assert.Equal(2,all.Works.Count(w=>w.FreeProjectId is not null));Assert.DoesNotContain(all.Works,w=>w.Title=="Legacy-only");
        Assert.Equal(2,Work(all,id).Checks);Assert.DoesNotContain(all.Difficulties,d=>d.Subject=="electronics");
        var saved=all.Works.Where(w=>w.FreeProjectId is not null).OrderBy(w=>w.TaskId).ToArray();
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");
        await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        Assert.Equal(saved,(await Results(f,course:true)).Works.Where(w=>w.FreeProjectId is not null).OrderBy(w=>w.TaskId).ToArray());
        await f.Restart();await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        Assert.Equal(original,(await f.Admin.Get<ResultsElectronicsDetail>($"teacher/results/electronics/{f.StudentId}/{id}?lessonId={first.Id}")).ProjectJson);
    }
    [Fact]
    public async Task SnapshotAccessAndCsvNeverGuessMissingContextOrInventMeasurements()
    {
        await using var f=await LiveClassroom.Start();var id=Guid.NewGuid().ToString("N");var project=CircuitProject.Blink();project.Name="=SUM(1;2)";
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(project.Serialize()));
        var route=$"teacher/results/electronics/{f.StudentId}/{id}";
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Get<ResultsElectronicsDetail>(route))).Status);
        using var anonymous=new ClassroomApi(f.Admin.Connection);Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>anonymous.Get<ResultsElectronicsDetail>(route))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>f.Admin.Get<ResultsElectronicsDetail>(route+"?lessonId="+Guid.NewGuid().ToString("N")))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>f.Admin.Get<ResultsElectronicsDetail>($"teacher/results/electronics/{Guid.NewGuid():N}/{id}"))).Status);
        var csv=await f.Admin.Get<ResultsCsv>("teacher/results/csv?mode=course&subject=electronics");
        var rows=Parse(csv.Csv);Assert.All(rows,r=>Assert.Equal(19,r.Length));var free=Assert.Single(rows.Skip(1));
        Assert.Equal("free-project",free[16]);Assert.Equal(id,free[18]);Assert.StartsWith("'=SUM",free[3]);Assert.Equal("0",free[5]);Assert.Equal("",free[14]);Assert.Equal("",free[8]);
        var other=(await f.Admin.Post<JsonElement>("teacher/students",new CreateStudentRequest("no-projects","synthetic-other-password",null))).GetProperty("id").GetString();
        var empty=Parse((await f.Admin.Get<ResultsCsv>("teacher/results/csv?mode=course&subject=electronics&studentId="+other)).Csv);Assert.Equal(2,empty.Length);Assert.All(empty,r=>Assert.Equal(19,r.Length));Assert.Equal("",empty[1][18]);
    }
    static string[][] Parse(string csv)
    {
        using var parser=new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv)){Delimiters=[";"],HasFieldsEnclosedInQuotes=true};
        var rows=new List<string[]>();while(!parser.EndOfData)rows.Add(parser.ReadFields()!);return rows.ToArray();
    }
}
