using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsActivityUiTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    // Headless Activate() does not issue the platform notification. Exercise
    // Window's real activation handler through the installed callback instead.
    static void Activate(MainWindow window)=>Assert.IsType<Action>(typeof(IWindowBaseImpl).GetProperty("Activated",Flags|BindingFlags.Public)!.GetValue(window.PlatformImpl)).Invoke();
    [AvaloniaFact]
    public async Task ForegroundActivityPersistsButDisabledDetachedAndMinimizedViewsDoNotAccrueTime()
    {
        await using var f=await LiveClassroom.Start();var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var root=Path.Combine(Path.GetTempPath(),"classroom-activity-ui-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(CircuitProject.Blink().Serialize()));
        var window=new MainWindow(false,false,root){Width=900,Height=900};typeof(MainWindow).GetField("api",Flags)!.SetValue(window,f.Student);typeof(MainWindow).GetField("login",Flags)!.SetValue(window,login);window.Show();ElectronicsScreen? source=null;
        try
        {
            await window.OpenFreeProject(id);Activate(window);window.UpdateLayout();source=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            Assert.True(window.IsActive);await Task.Delay(2200);source.IsEnabled=false;
            long Measured()=>source.LocalHistory!.Changes.Where(c=>c.ActiveSpan is not null).Sum(c=>c.ActiveSpan!.EndedAt-c.ActiveSpan.StartedAt);
            var first=Measured();Assert.InRange(first,1000,5000);await Task.Delay(2200);Assert.Equal(first,Measured());
            source.IsEnabled=true;window.WindowState=WindowState.Minimized;var minimized=Measured();await Task.Delay(2200);Assert.Equal(minimized,Measured());window.WindowState=WindowState.Normal;Activate(window);
            window.ShowPage("Просмотр",new TextBlock{Text="Read-only page"});var detached=Measured();await Task.Delay(2200);Assert.Equal(detached,Measured());window.ClosePage();
            Assert.DoesNotContain(source.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("длительность")==true||t.Text?.Contains("секунд")==true);
            Assert.True(await source.Synchronize());var result=await f.Admin.Get<ResultsOverview>("teacher/results?mode=lesson&lessonId="+f.Student.LessonId);Assert.True(Assert.Single(result.Works,w=>w.FreeProjectId==id).ActiveSeconds>=1);
            var teacher=new MainWindow(true,false){Width=900,Height=900};using var results=new ClassroomResultsView(teacher,f.Admin);
            typeof(ClassroomResultsView).GetField("data",Flags)!.SetValue(results,result);typeof(ClassroomResultsView).GetField("studentId",Flags)!.SetValue(results,f.StudentId);
            typeof(ClassroomResultsView).GetField("selectedWork",Flags)!.SetValue(results,result.Works.Single(w=>w.FreeProjectId==id));typeof(ClassroomResultsView).GetMethod("Render",Flags)!.Invoke(results,null);
            teacher.Preview(results);teacher.Show();teacher.UpdateLayout();
            try
            {
                Assert.Contains(results.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("Зафиксированная активная длительность")==true);
                Assert.Contains(results.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("Работа до начала записи сюда не входит")==true);
                using var frame=teacher.CaptureRenderedFrame();var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(output);frame!.Save(Path.Combine(output,"electronics-active-results-900.png"));
            }
            finally{teacher.Preview(new TextBlock());teacher.UpdateLayout();teacher.Close();}
        }
        finally
        {
            window.ClosePage(false);await (Task)typeof(MainWindow).GetMethod("StopElectronicsDelivery",Flags)!.Invoke(window,null)!;source?.Dispose();window.Preview(new TextBlock());window.UpdateLayout();window.Close();await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
}
