using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Classroom.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Classroom.UiTests;

public sealed class ClassroomResultsTests
{
    static async Task<LessonView> Lesson(LiveClassroom f, string? group = null, params string[] tasks)
    {
        if ((await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current is { } current)
            await f.Admin.Post<LessonView>($"teacher/lessons/{current.Id}/finish");
        var lesson = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(group, OpenToAllGroups:true, RegistrationOpen:group is not null)));
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest(tasks));
        return await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/start");
    }
    static Task<ResultsOverview> Results(LiveClassroom f, string suffix = "") => f.Admin.Get<ResultsOverview>("teacher/results"+suffix);
    static async Task<string> Student(LiveClassroom f,string login,string group)
        => (await f.Admin.Post<JsonElement>("teacher/students",new CreateStudentRequest(login,"synthetic-extra-password",group))).GetProperty("id").GetString()!;
    static string Solution(StudentTask task)
    {
        var definition=Catalog.Data.Tasks.Single(t=>t.Id==task.Id);
        return (task.VariantIndex==1?definition:Variants.Create(definition)[task.VariantIndex-2]).Solution;
    }

    [Fact]
    public async Task RosterIncludesUnjoinedAndKeepsNamesAndGroupsAfterTransfers()
    {
        await using var f=await LiveClassroom.Start();
        var first=await f.Admin.Post<GroupDto>("teacher/groups",new GroupRequest("191"));
        var second=await f.Admin.Post<GroupDto>("teacher/groups",new GroupRequest("192"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group",new GroupMembershipRequest(first.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name",new StudentNameRequest("Иванов Иван"));
        var absent=await Student(f,"absent",first.Id);var guest=await Student(f,"guest",second.Id);
        var lesson=await Lesson(f,first.Id,"database","columns");
        var initial=await Results(f);
        Assert.Equal(2,initial.Summary.Students);Assert.Equal(4,initial.Summary.Total);Assert.Equal(0,initial.Summary.Joined);
        Assert.Equal(0,initial.Summary.AveragePercent);Assert.All(initial.Works,w=>Assert.False(w.Started));
        await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        await f.Student.Get<StudentTask>("tasks/database");
        // Opening without running a query is a real start; issuing an unopened task isn't.
        Assert.Equal(1,(await Results(f)).Summary.Started);
        await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),"SELEC 1;",true));
        var working=await Results(f);Assert.Equal(1,working.Summary.Started);Assert.Equal(0,working.Summary.Completed);
        var hard=working.Difficulties.Single(d=>d.TaskId=="database");
        Assert.Equal(1,hard.Checked);Assert.Equal(100,hard.UnsolvedPercent);Assert.True(hard.SmallSample);Assert.Equal(1,hard.FirstChecksFailed);
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group",new GroupMembershipRequest(second.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name",new StudentNameRequest("Новое имя"));
        using var other=new ClassroomApi(f.Admin.Connection);await other.Login(new("student","guest","synthetic-extra-password"));
        var joined=await Results(f);Assert.Equal(3,joined.Summary.Students);Assert.Equal(2,joined.Summary.Joined);
        var saved=joined.Students.Single(p=>p.Id==f.StudentId);Assert.Equal(first.Id,saved.GroupKey);Assert.Equal("Иванов Иван",saved.Name);
        Assert.Equal(first.Id,joined.Students.Single(p=>p.Id==absent).GroupKey);Assert.Equal(second.Id,joined.Students.Single(p=>p.Id==guest).GroupKey);
        var registered=await Student(f,"new-during-lesson",first.Id);
        Assert.Contains((await Results(f)).Students,p=>p.Id==registered&&!p.Joined);
        var only=await Results(f,"?group="+first.Id+"&subject=sql");Assert.Equal(3,only.Summary.Students);Assert.Equal(6,only.Works.Length);
        var csv=await f.Admin.Get<ResultsCsv>("teacher/results/csv?group="+first.Id+"&subject=sql");
        Assert.Contains("absent",csv.Csv);Assert.DoesNotContain("guest",csv.Csv);Assert.Contains("not-started",csv.Csv);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/finish");
        var history=await Results(f,"?lessonId="+lesson.Id);Assert.Equal(4,history.Summary.Students);
        var course=await Results(f,"?mode=course&subject=sql");var old=await f.Admin.Get<LearningOverview>("teacher/learning");
        Assert.Equal(old.Students.Single(p=>p.Id==f.StudentId).Total,course.Students.Single(p=>p.Id==f.StudentId).Summary.Total);
        Assert.Equal(second.Id,course.Students.Single(p=>p.Id==f.StudentId).GroupKey);
    }

    [Fact]
    public async Task PendingResubmissionRetainsAcceptedCreditAndGradeUntilNewDecision()
    {
        await using var f=await LiveClassroom.Start();var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var id=Guid.NewGuid().ToString("N");await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+id,new ElectronicsTaskRequest("Светодиод","Соберите цепь.",CircuitProject.Blink().Serialize()));
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([id]));
        var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+id);var client=Guid.NewGuid().ToString("N");
        async Task<string> Submit(long sequence,int version)
        {
            var report=Guid.NewGuid().ToString("N");await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,sequence,assignment.ProjectJson,[new(report,"submitted",assignment.ProjectJson,"",version)],assignment.Id));return report;
        }
        async Task Decide(string report,string action,int? grade)
        {
            var route=$"teacher/reviews/electronics/{f.StudentId}/{report}";var detail=await f.Admin.Get<ElectronicsReviewDetail>(route);
            await f.Admin.Put<ElectronicsReviewDecision>(route+"/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest(action,"Проверено.",grade,detail.History.LastOrDefault()?.Id,detail.AssignmentVersion));
        }
        var first=await Submit(1,assignment.StateVersion);
        var pending=await Results(f,"?subject=electronics");Assert.Equal(1,pending.Summary.Pending);Assert.Equal(0,pending.Difficulties.Single().Checked);Assert.Null(pending.Difficulties.Single().UnsolvedPercent);
        await Decide(first,"accepted",4);await Decide(first,"allow-resubmit",null);
        var status=await f.Student.Get<ElectronicsAssignmentStatus>($"electronics/assignments/{assignment.Id}/status");var second=await Submit(2,status.StateVersion);
        var waiting=await Results(f,"?subject=electronics");var row=Assert.Single(waiting.Works);
        Assert.Equal("awaiting-review",row.State);Assert.True(row.Completed);Assert.Equal(4,row.Grade);Assert.Equal(100,waiting.Summary.AveragePercent);Assert.Equal(0,waiting.Difficulties.Single().Checked);
        var versions=await f.Admin.Get<ElectronicsReviewQueue>($"teacher/reviews/electronics?status=all&studentId={f.StudentId}&assignmentId={assignment.Id}");
        Assert.Equal(2,versions.Items.Length);Assert.Contains(versions.Items,v=>v.ReportId==first&&v.Grade==4);Assert.Contains(versions.Items,v=>v.ReportId==second&&v.State=="awaiting-review");
        await Decide(second,"accepted",5);var accepted=Assert.Single((await Results(f,"?subject=electronics")).Works);Assert.True(accepted.Completed);Assert.Equal(5,accepted.Grade);
        await Decide(second,"changes-requested",null);var returned=Assert.Single((await Results(f,"?subject=electronics")).Works);Assert.False(returned.Completed);Assert.Null(returned.Grade);
        Assert.Null(returned.ActiveSeconds);Assert.Null(returned.Hints);Assert.Null(returned.Violations);
    }

    [Fact]
    public async Task SqlDetailsPageExactErrorsWithoutMixingCourseAndLessonEvents()
    {
        await using var f=await LiveClassroom.Start();await f.Student.Get<StudentTask>("tasks/database");
        await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),"SELECT 999;",true));
        var lesson=await Lesson(f,null,"database");await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        await f.Student.Get<StudentTask>("tasks/database");
        using(var db=new SqliteConnection("Data Source="+f.DatabasePath+";Pooling=False"))
        {
            await db.OpenAsync();using var transaction=db.BeginTransaction();
            for(var i=0;i<105;i++)
            {
                using var cmd=db.CreateCommand();cmd.Transaction=transaction;
                cmd.CommandText="INSERT INTO Attempts(Id,StudentId,TaskId,Kind,At,Action,Code,Explanation,LessonId,WorkScope) VALUES($id,$student,'database',$error,$at,'check','SELEC 1;',$explanation,$lesson,$scope)";
                cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString());cmd.Parameters.AddWithValue("$student",f.StudentId);cmd.Parameters.AddWithValue("$error","near \"SELEC\": syntax error");
                cmd.Parameters.AddWithValue("$at",lesson.StartedAt!.Value+i);cmd.Parameters.AddWithValue("$explanation","Проверьте написание SELECT.");cmd.Parameters.AddWithValue("$lesson",lesson.Id);cmd.Parameters.AddWithValue("$scope","lesson:"+lesson.Id);await cmd.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        var route=$"teacher/results/sql/{f.StudentId}/database?lessonId={lesson.Id}";var first=await f.Admin.Get<ResultsSqlDetail>(route);var second=await f.Admin.Get<ResultsSqlDetail>(route+"&offset="+first.NextOffset);
        Assert.Equal(100,first.Events.Length);Assert.True(first.HasMore);Assert.Equal(5,second.Events.Length);Assert.False(second.HasMore);
        Assert.Equal(105,first.Events.Concat(second.Events).Select(e=>e.Id).Distinct().Count());Assert.All(first.Events.Concat(second.Events),e=>{Assert.Equal("near \"SELEC\": syntax error",e.Result);Assert.Equal("SELEC 1;",e.Code);});
        Assert.Single((await f.Admin.Get<ResultsSqlDetail>($"teacher/results/sql/{f.StudentId}/database")).Events);
    }

    [Fact]
    public async Task ReusedAndFreshSqlKeepSeparateLessonAndCourseProgress()
    {
        await using var f=await LiveClassroom.Start();var original=await f.Student.Get<StudentTask>("tasks/database");
        Assert.True((await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),Solution(original),true))).Correct);
        var reuse=await Lesson(f,null,"database");await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var credited=Assert.Single((await Results(f)).Works);Assert.True(credited.Completed);Assert.True(credited.Reused);Assert.False(credited.Started);Assert.Equal(0,credited.Checks);
        var prior=await f.Admin.Get<ResultsSqlDetail>($"teacher/results/sql/{f.StudentId}/database?lessonId={reuse.Id}");Assert.Equal("course",prior.WorkScope);Assert.Equal(Solution(original),prior.AcceptedCode);
        var retry=await Lesson(f,null,"database");await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{retry.Id}/sql-tasks",new LessonSqlSelectionRequest(["database"],["database"]));
        await f.Student.Login(new("student","ui-test","synthetic-student-password"));var fresh=Assert.Single((await Results(f)).Works);Assert.False(fresh.Completed);Assert.False(fresh.Started);
        var opened=await f.Student.Get<StudentTask>("tasks/database");await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),"SELECT 123;",true));
        fresh=Assert.Single((await Results(f)).Works);Assert.True(fresh.Started);Assert.False(fresh.Completed);Assert.Equal(1,fresh.Errors);
        var current=await f.Admin.Get<ResultsSqlDetail>($"teacher/results/sql/{f.StudentId}/database?lessonId={retry.Id}");Assert.Equal("lesson:"+retry.Id,current.WorkScope);Assert.Null(current.AcceptedCode);Assert.Single(current.Events);
        Assert.True((await Results(f,"?mode=course&subject=sql")).Works.Single(w=>w.StudentId==f.StudentId&&w.TaskId=="database").Completed);
    }

    [Fact]
    public async Task PreferencesCsvAndLegacyRosterRemainHonestAcrossRestartAndBackup()
    {
        await using var f=await LiveClassroom.Start();var group=await f.Admin.Post<GroupDto>("teacher/groups",new GroupRequest("191"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group",new GroupMembershipRequest(group.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name",new StudentNameRequest("=SUM(1;2)"));
        var lesson=await Lesson(f,group.Id);await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var empty=await Results(f);Assert.Null(empty.Summary.AveragePercent);Assert.Equal(0,empty.Summary.Total);
        Assert.Contains("'\u003dSUM(1;2)",(await f.Admin.Get<ResultsCsv>("teacher/results/csv")).Csv);
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Get<ResultsOverview>("teacher/results"))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(()=>f.Admin.Put<ResultsPreference>("teacher/results/preferences",new ResultsPreference("unknown")))).Status);
        await f.Admin.Put<ResultsPreference>("teacher/results/preferences",new ResultsPreference("course"));
        // Model an old lesson which only stored the name recorded at entry, not a roster or group ID.
        using(var db=new SqliteConnection("Data Source="+f.DatabasePath+";Pooling=False"))
        {await db.OpenAsync();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM LessonRoster; UPDATE LessonSessions SET RosterCapturedAt=NULL,RosterAtStart=0";await cmd.ExecuteNonQueryAsync();}
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group",new GroupMembershipRequest(null));
        var legacy=await Results(f);Assert.Contains("Исторический состав",legacy.Note);Assert.StartsWith("legacy:",Assert.Single(legacy.Students).GroupKey);
        await f.Restart();await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        Assert.Equal("course",(await f.Admin.Get<ResultsPreference>("teacher/results/preferences")).Mode);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/restore");var late=await Results(f);
        Assert.Contains("после начала",late.Note);Assert.StartsWith("legacy:",Assert.Single(late.Students).GroupKey);
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");
        await f.Admin.Login(new("teacher","","synthetic-teacher-password"));Assert.Equal("course",(await f.Admin.Get<ResultsPreference>("teacher/results/preferences")).Mode);
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task TeacherNavigatesGroupsPupilsAndExactSqlThenReturnsWithFilters(int width)
    {
        await using var f=await LiveClassroom.Start();var group=await f.Admin.Post<GroupDto>("teacher/groups",new GroupRequest("ИСП-191"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group",new GroupMembershipRequest(group.Id));
        await Lesson(f,group.Id,"database","columns");await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var work=await f.Student.Get<StudentTask>("tasks/database");await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),Solution(work),true));
        var window=new MainWindow(true,false){Width=width,Height=850};using var shell=new TeacherScreen(window,f.Admin,new HostManager());
        window.Preview(shell);window.Show();
        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        ((TabControl)typeof(TeacherScreen).GetField("tabs",flags)!.GetValue(shell)!).SelectedIndex=3;
        window.UpdateLayout();
        var view=window.GetVisualDescendants().OfType<ClassroomResultsView>().Single();
        for(var i=0;i<100&&!view.GetVisualDescendants().OfType<DataGrid>().Any();i++)await Task.Delay(30);
        Assert.NotEmpty(view.GetVisualDescendants().OfType<DataGrid>());window.UpdateLayout();
        void Click(string title)=>view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Wait(Func<bool> ready){for(var i=0;i<100&&!ready();i++)await Task.Delay(30);Assert.True(ready());}
        bool Has(string title)=>view.GetVisualDescendants().OfType<Button>().Any(b=>b.Content?.ToString()==title);
        view.GetVisualDescendants().OfType<DataGrid>().First().SelectedIndex=0;Click("Открыть группу");await Wait(()=>Has("Открыть ученика"));
        view.GetVisualDescendants().OfType<DataGrid>().First().SelectedIndex=0;Click("Открыть ученика");await Wait(()=>Has("Открыть работу"));
        view.GetVisualDescendants().OfType<DataGrid>().Single().SelectedIndex=0;Click("Открыть работу");await Wait(()=>Has("Пошаговая история SQL"));
        Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(),t=>t.IsReadOnly&&t.Text==Solution(work));
        foreach(var button in view.GetVisualDescendants().OfType<Button>().Where(b=>b.Content?.ToString() is "К ученикам группы" or "К работам ученика"))
        {var point=button.TranslatePoint(default,window);Assert.NotNull(point);Assert.True(point!.Value.X+button.Bounds.Width<=window.Bounds.Width+1);}
        Capture(window,"results-work-"+width+".png");Click("К работам ученика");Assert.True(Has("Открыть работу"));
        Click("К ученикам группы");Assert.True(Has("Открыть ученика"));Capture(window,"results-pupils-"+width+".png");
        window.Close();
    }
    static void Capture(Window window,string name)
    {
        window.UpdateLayout();using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);
        var directory=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(directory);frame!.Save(Path.Combine(directory,name));
    }

    [AvaloniaFact]
    public async Task ReportOpenedFromResultsUsesWholePaneAndCanBeGraded()
    {
        await using var f=await LiveClassroom.Start();var project=CircuitProject.Blink().Serialize();var client=Guid.NewGuid().ToString("N");var report=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,1,project,[new(report,"submitted",project,"")]));
        var window=new MainWindow(true,false){Width=900,Height=850};using var view=new ElectronicsReviewQueueView(window,f.Admin);
        window.Preview(view);window.Show();await view.OpenReport(f.StudentId,report);window.UpdateLayout();
        Assert.Empty(view.GetVisualDescendants().OfType<ListBox>());
        var grade=view.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Items.OfType<string>().Contains("Без оценки"));grade.SelectedIndex=5;
        view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Сохранить результат").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ElectronicsReviewDetail? detail=null;for(var i=0;i<100;i++){detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");if(detail.Item.Grade==5)break;await Task.Delay(30);}
        Assert.Equal(5,detail!.Item.Grade);Assert.Equal(project,detail.ProjectJson);Capture(window,"results-scheme-review-900.png");window.Close();
    }
}
