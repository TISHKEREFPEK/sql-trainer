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
using Classroom.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlPracticeResultsTests
{
    static string Solution(StudentTask task)
    {
        var definition=Catalog.Data.Tasks.Single(t=>t.Id==task.Id);
        return (task.VariantIndex==1?definition:Variants.Create(definition)[task.VariantIndex-2]).Solution;
    }
    static Task<ResultsOverview> Results(LiveClassroom f,string lesson)=>f.Admin.Get<ResultsOverview>("teacher/results?lessonId="+lesson);
    static Task<ResultsSqlDetail> Detail(LiveClassroom f,string lesson,string task="database")=>f.Admin.Get<ResultsSqlDetail>($"teacher/results/sql/{f.StudentId}/{task}?lessonId={lesson}&workScope=course");
    static async Task<LessonView> Next(LiveClassroom f)
    {
        var current=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;await f.Admin.Post<LessonView>($"teacher/lessons/{current.Id}/finish");
        var next=await f.Admin.Post<LessonView>("teacher/lessons",new CreateLessonRequest(Guid.NewGuid().ToString("N"),new(null,OpenToAllGroups:true,FreePractice:true)));
        next=await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start");await f.Student.Login(new("student","ui-test","synthetic-student-password"));return next;
    }
    static async Task Mutate(LiveClassroom f,string command)
    {using var db=new SqliteConnection("Data Source="+f.DatabasePath+";Pooling=False");await db.OpenAsync();using var cmd=db.CreateCommand();cmd.CommandText=command;await cmd.ExecuteNonQueryAsync();}

    [Fact]
    public async Task FreeWorkHasItsOwnLessonCountsAndSurvivesLaterIssueAndAnotherLesson()
    {
        await using var f=await LiveClassroom.Start();var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks",new LessonSqlSelectionRequest(["columns"]));
        var task=await f.Student.Get<StudentTask>("tasks/database");Assert.Equal("course",task.WorkScope);
        await f.Student.Post<StudentTask>("tasks/database/hint",new HintRequest(0,lesson.Id,"course"));
        var activity=new ActivityRequest(10,Guid.NewGuid().ToString("N"),1,lesson.Id,"course");await f.Student.Post<JsonElement>("tasks/database/activity",activity);await f.Student.Post<JsonElement>("tasks/database/activity",activity);
        var wrong=new ExecuteRequest(Guid.NewGuid().ToString(),"SELEC 1;",true,lesson.Id,"course");await f.Student.Post<ExecutionDto>("tasks/database/execute",wrong);await f.Student.Post<ExecutionDto>("tasks/database/execute",wrong);
        Assert.True((await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),Solution(task),true,lesson.Id,"course"))).Correct);
        await f.Student.Put<JsonElement>("tasks/database/draft",new DraftRequest("SELECT 17; -- старый черновик",lesson.Id,"course"));
        var data=await Results(f,lesson.Id);Assert.Equal(1,data.Summary.Total);Assert.Equal(0,data.Summary.Completed);Assert.Equal(0,data.Summary.AveragePercent);Assert.Equal(1,data.Summary.Started);
        var practice=data.Works.Single(w=>w.WorkScope=="course");Assert.False(practice.Required);Assert.False(practice.Partial);Assert.True(practice.Completed);Assert.NotNull(practice.StartedAt);
        Assert.Equal(2,practice.Checks);Assert.Equal(1,practice.Errors);Assert.Equal(1,practice.Hints);Assert.Equal(2,practice.ActiveSeconds);Assert.Single(data.Difficulties);Assert.Equal("columns",data.Difficulties[0].TaskId);
        var original=await Detail(f,lesson.Id);Assert.Equal(Solution(task),original.AcceptedCode);Assert.Contains("старый черновик",original.Draft);
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks",new LessonSqlSelectionRequest(["database","columns"]));await f.Student.Get<StudentTask>("tasks/database");
        data=await Results(f,lesson.Id);Assert.Equal(2,data.Works.Count(w=>w.TaskId=="database"));Assert.Equal(2,data.Summary.Total);Assert.Equal(1,data.Summary.Completed);Assert.Equal(50,data.Summary.AveragePercent);
        Assert.Equal(0,data.Works.Single(w=>w.TaskId=="database"&&w.Required).Checks);
        var next=await Next(f);var again=await f.Student.Get<StudentTask>("tasks/database");
        var nextPractice=Assert.Single((await Results(f,next.Id)).Works);Assert.False(nextPractice.Completed);Assert.Equal(0,nextPractice.Checks);Assert.Equal(0,nextPractice.Hints);
        await f.Student.Put<JsonElement>("tasks/database/draft",new DraftRequest("SELECT 23; -- новая пара",next.Id,"course"));
        await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),Solution(again)+" -- вторая пара",true,next.Id,"course"));
        Assert.Equal(Wire.Write(original),Wire.Write(await Detail(f,lesson.Id)));Assert.NotEqual(original.AcceptedCode,(await Detail(f,next.Id)).AcceptedCode);
        var csv=await f.Admin.Get<ResultsCsv>("teacher/results/csv?lessonId="+lesson.Id);Assert.Contains("free-practice",csv.Csv);Assert.Contains("recorded",csv.Csv);
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        Assert.Equal(Wire.Write(original),Wire.Write(await Detail(f,lesson.Id)));
    }

    [Fact]
    public async Task DelayedDraftAndActivityKeepTheirOriginalLessonAndDoNotInflateTime()
    {
        await using var f=await LiveClassroom.Start();var old=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;await f.Student.Get<StudentTask>("tasks/database");
        var at=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();var active=new SqlActionSync(Guid.NewGuid().ToString("N"),"database",old.Id,"course","activity",at,10);
        await f.Student.Post<JsonElement>("sql-sync/action",active);await f.Student.Post<JsonElement>("sql-sync/action",active);
        var draft=new SqlDraftSync(Guid.NewGuid().ToString("N"),"database",old.Id,"course",SqlSyncHash.Text(""),"SELECT 11;",at);
        await f.Student.Put<SqlDraftReceipt>("sql-sync/draft",draft);await f.Student.Put<SqlDraftReceipt>("sql-sync/draft",draft);
        var next=await Next(f);await f.Student.Get<StudentTask>("tasks/database");await f.Student.Put<JsonElement>("tasks/database/draft",new DraftRequest("SELECT 22;",next.Id,"course"));
        var delayed=draft with{Id=Guid.NewGuid().ToString("N"),BaseHash=SqlSyncHash.Text("SELECT 11;"),Code="SELECT 33;"};
        Assert.Equal("conflict",(await f.Student.Put<SqlDraftReceipt>("sql-sync/draft",delayed)).Status);Assert.Equal("SELECT 11;",(await Detail(f,old.Id)).Draft);
        delayed=delayed with{BaseHash=SqlSyncHash.Text("SELECT 22;")};Assert.Equal("saved",(await f.Student.Put<SqlDraftReceipt>("sql-sync/draft",delayed)).Status);
        Assert.Equal("SELECT 33;",(await Detail(f,old.Id)).Draft);Assert.Equal("SELECT 22;",(await Detail(f,next.Id)).Draft);
        await f.Student.Post<JsonElement>("sql-sync/action",active with{Id=Guid.NewGuid().ToString("N")});Assert.Equal(2,Assert.Single((await Results(f,old.Id)).Works).ActiveSeconds);
        Assert.Equal(0,Assert.Single((await Results(f,next.Id)).Works).ActiveSeconds);
        await f.Restart();await f.Admin.Login(new("teacher","","synthetic-teacher-password"));Assert.Equal("SELECT 33;",(await Detail(f,old.Id)).Draft);
    }

    [Fact]
    public async Task LegacyHistoryUsesFrozenEvidenceAndNeverTodaysConditionOrInventedTimes()
    {
        await using var f=await LiveClassroom.Start();var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;var task=await f.Student.Get<StudentTask>("tasks/database");
        await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),Solution(task),true));
        await Mutate(f,"DELETE FROM LessonSqlPractice");
        var definition=Catalog.Data.Tasks.Single(t=>t.Id=="database") with{Title="Новое имя задания",Prompt="Совсем другое условие."};
        await f.Admin.Put<JsonElement>("teacher/tasks/database",new SaveTaskRequest(Catalog.Write(definition),false));
        var legacy=Assert.Single((await Results(f,lesson.Id)).Works);Assert.True(legacy.Partial);Assert.True(legacy.Completed);Assert.Null(legacy.StartedAt);Assert.Null(legacy.ActiveSeconds);Assert.Null(legacy.Hints);Assert.Equal(task.Title,legacy.Title);
        var detail=await Detail(f,lesson.Id);Assert.True(detail.Partial);Assert.Equal(task.Prompt,detail.Prompt);Assert.Equal(Solution(task),detail.AcceptedCode);
        await Mutate(f,$"INSERT INTO Attempts(Id,StudentId,TaskId,Kind,At,Action,Code,LessonId,WorkScope) VALUES('{Guid.NewGuid():N}','{f.StudentId}','columns','different',{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},'check','SELECT 0;','{lesson.Id}','course')");
        var unknown=await Detail(f,lesson.Id,"columns");Assert.Contains("не записывалось",unknown.Prompt);Assert.Null(unknown.Variant);Assert.True(unknown.Partial);
        Assert.Equal(0,(await Results(f,lesson.Id)).Summary.Total);
    }

    [Fact]
    public async Task LessonAlreadyRunningAtUpgradeCannotClaimCompletePracticeHistory()
    {
        await using var f=await LiveClassroom.Start();var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await Mutate(f,"UPDATE LessonSessions SET SqlPracticeTrackingAt=NULL");await f.Student.Get<StudentTask>("tasks/database");
        var row=Assert.Single((await Results(f,lesson.Id)).Works);Assert.True(row.Partial);Assert.Null(row.StartedAt);Assert.Null(row.ActiveSeconds);Assert.Null(row.Hints);
        var detail=await Detail(f,lesson.Id);Assert.True(detail.Partial);Assert.Equal("",detail.Draft);
        await f.Restart();await f.Admin.Login(new("teacher","","synthetic-teacher-password"));await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/restore");
        Assert.True(Assert.Single((await Results(f,lesson.Id)).Works).Partial);
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task TeacherCanOpenFreeAndIssuedCopiesOfTheSameTask(int width)
    {
        await using var f=await LiveClassroom.Start();var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;var task=await f.Student.Get<StudentTask>("tasks/database");
        await f.Student.Post<ExecutionDto>("tasks/database/execute",new ExecuteRequest(Guid.NewGuid().ToString(),Solution(task)+" -- практика",true));
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks",new LessonSqlSelectionRequest(["database"],["database"]));await f.Student.Get<StudentTask>("tasks/database");
        var window=new MainWindow(true,false){Width=width,Height=850};using var shell=new TeacherScreen(window,f.Admin,new HostManager());window.Preview(shell);window.Show();
        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;((TabControl)typeof(TeacherScreen).GetField("tabs",flags)!.GetValue(shell)!).SelectedIndex=3;window.UpdateLayout();
        var view=window.GetVisualDescendants().OfType<ClassroomResultsView>().Single();
        async Task Wait(Func<bool> ready){for(var i=0;i<100&&!ready();i++)await Task.Delay(30);Assert.True(ready());}
        void Click(string title)=>view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        bool Has(string title)=>view.GetVisualDescendants().OfType<Button>().Any(b=>b.Content?.ToString()==title);
        await Wait(()=>view.GetVisualDescendants().OfType<DataGrid>().Any());Click("Все ученики");view.GetVisualDescendants().OfType<DataGrid>().First().SelectedIndex=0;Click("Открыть ученика");
        var grid=view.GetVisualDescendants().OfType<DataGrid>().Single();var rows=grid.ItemsSource.Cast<object>().ToArray();Assert.Equal(2,rows.Length);
        var free=rows.Single(row=>((ResultsWork)row.GetType().GetProperty("Work")!.GetValue(row)!).WorkScope=="course");grid.SelectedItem=free;Click("Открыть работу");
        await Wait(()=>Has("Пошаговая история SQL"));Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(),t=>t.IsReadOnly&&t.Text==Solution(task)+" -- практика");
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("Не входит в обязательный прогресс")==true);
        window.UpdateLayout();using(var frame=window.CaptureRenderedFrame()){Assert.NotNull(frame);var directory=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(directory);frame!.Save(Path.Combine(directory,"results-free-practice-"+width+".png"));}
        Click("К работам ученика");grid=view.GetVisualDescendants().OfType<DataGrid>().Single();grid.SelectedItem=grid.ItemsSource.Cast<object>().Single(row=>((ResultsWork)row.GetType().GetProperty("Work")!.GetValue(row)!).Required);Click("Открыть работу");await Wait(()=>Has("Пошаговая история SQL"));
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBox>(),t=>t.Text?.Contains("-- практика")==true);window.Close();
    }
}
