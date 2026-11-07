using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class FreeElectronicsResultsUiTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Click(Control root,string title)=>root.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task Wait(Func<bool> condition){for(var i=0;i<250&&!condition();i++)await Task.Delay(40);Assert.True(condition());}
    static void Capture(MainWindow window,string name)
    {window.UpdateLayout();var dir=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(dir);using var frame=window.CaptureRenderedFrame();frame!.Save(Path.Combine(dir,name));}

    [AvaloniaFact]
    public async Task TeacherOpensHistoricalSnapshotAndFilteredJournalWithoutChangingAnyStudentData()
    {
        await using var f=await LiveClassroom.Start();var lesson=f.Student.LessonId!;
        var root=Path.Combine(Path.GetTempPath(),"classroom-free-results-history-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");
        var project=CircuitProject.Blink();project.Name="Историческая схема";project.Code="void setup(){Serial.begin(9600);Serial.println(\"historical-snapshot\");} void loop(){}";var historical=project.Serialize();
        async Task Save(string key,string json)
        {
            await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+key,new ElectronicsProjectCreate(CircuitProject.Blink().Serialize()));
            var context=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,lesson,null,key);var file=Path.Combine(root,"electronics",context.ProjectFileName);
            using var history=new ElectronicsHistoryStore(file+".history",context,CircuitProject.Blink().Serialize());history.Record(json,"code-edit");
            var report=Guid.NewGuid().ToString("N");history.Record(json,"report-submitted",true,reportId:report,reportHash:SqlSyncHash.Text(json));
            ElectronicsSync? sync=null;
            using(sync=new ElectronicsSync(f.Student,file+".sync.json",_=>{},false,freeContext:context.FreeContext(),journalDelivery:token=>history.Deliver(f.Student,sync!.ClientId,token),journalPending:()=>history.Pending))
            {sync.QueueDraft(json);sync.Record("submitted",json,expectedAssignmentVersion:1,reportId:report);Assert.True(await sync.Drain());}
        }
        await Save(id,historical);await Save(Guid.NewGuid().ToString("N"),CircuitProject.Blink().Serialize());
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/finish");
        var next=await f.Admin.Post<LessonView>("teacher/lessons",new CreateLessonRequest(Guid.NewGuid().ToString("N"),new(null,OpenToAllGroups:true,FreePractice:true)));
        await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start");await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{id}/open",new ElectronicsProjectOpen(next.Id));
        project.Code="// future lesson";var current=project.Serialize();await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,current,[],FreeContext:new(id,next.Id,1)));
        var window=new MainWindow(true,false){Width=900,Height=900};using var view=new ClassroomResultsView(window,f.Admin);
        typeof(ClassroomResultsView).GetField("lessonId",Hidden)!.SetValue(view,lesson);window.Preview(view);window.Show();ElectronicsJournalView? journal=null;
        try
        {
            await view.Refresh();window.UpdateLayout();view.GetVisualDescendants().OfType<DataGrid>().First().SelectedIndex=0;Click(view,"Открыть группу");
            await Wait(()=>view.GetVisualDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Открыть ученика"));window.UpdateLayout();
            view.GetVisualDescendants().OfType<DataGrid>().First().SelectedIndex=0;Click(view,"Открыть ученика");window.UpdateLayout();
            var table=view.GetVisualDescendants().OfType<DataGrid>().Single();table.SelectedItem=table.ItemsSource.Cast<object>().Single(r=>((ResultsWork)r.GetType().GetProperty("Work")!.GetValue(r)!).FreeProjectId==id);
            Click(view,"Открыть работу");await Wait(()=>view.GetVisualDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Открыть сохранённую схему"));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("Не входит в обязательный прогресс")==true);
            var submissions=(ElectronicsReviewQueue)typeof(ClassroomResultsView).GetField("submissions",Hidden)!.GetValue(view)!;Assert.Single(submissions.Items);Assert.Equal(id,submissions.Items[0].FreeProjectId);
            Capture(window,"results-free-scheme-900.png");window.Width=1320;Capture(window,"results-free-scheme-1320.png");window.Width=900;
            Click(view,"Пошаговая история схемы");await Wait(()=>window.GetVisualDescendants().OfType<ElectronicsJournalView>().Any());journal=window.GetVisualDescendants().OfType<ElectronicsJournalView>().Single();
            await Wait(()=>((ComboBox)typeof(ElectronicsJournalView).GetField("streams",Hidden)!.GetValue(journal)!).Items.Count==1);
            var stream=Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?projectId={id}&lessonId={lesson}"));
            var link=await f.Admin.Get<ElectronicsJournalReportLink>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes/2/report");
            Assert.Equal("available",link.Availability);Assert.Equal(submissions.Items[0].ReportId,link.ReportId);
            Assert.Equal(2,(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}")).Length);
            window.ClosePage();journal=null;Click(view,"Открыть сохранённую схему");window.UpdateLayout();
            var snapshot=window.GetVisualDescendants().OfType<ElectronicsReviewWorkspace>().Single();var editor=snapshot.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            Assert.Equal(CircuitProject.Parse(historical).Code,editor.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Hidden)!.Invoke(editor,[true])!;
            var monitor=(TextBox)typeof(ElectronicsScreen).GetField("monitor",Hidden)!.GetValue(editor)!;await Wait(()=>monitor.Text?.Contains("historical-snapshot")==true);
            editor.Suspend();editor.GetVisualDescendants().OfType<TextEditor>().Single().Text="// isolated teacher edits";Click(snapshot,"Восстановить снимок");window.UpdateLayout();
            Assert.Equal(CircuitProject.Parse(historical).Code,snapshot.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            Capture(window,"results-free-snapshot-900.png");
            Assert.Equal(historical,(await f.Admin.Get<ResultsElectronicsDetail>($"teacher/results/electronics/{f.StudentId}/{id}?lessonId={lesson}")).ProjectJson);
            Assert.Equal(current,(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson);
            Assert.Equal(1,(await f.Admin.Get<ResultsOverview>($"teacher/results?lessonId={lesson}&subject=electronics")).Works.Single(w=>w.FreeProjectId==id).Checks);
        }
        finally{journal?.Dispose();window.ClosePage();window.Preview(new TextBlock());window.UpdateLayout();window.Close();}
    }
}
