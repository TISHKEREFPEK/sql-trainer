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

public sealed class ElectronicsProjectUiTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static async Task Wait(Func<bool> condition)
    {for(var i=0;i<200&&!condition();i++)await Task.Delay(50);Assert.True(condition());}
    static void Click(Control view,string title)=>view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static TextEditor Code(MainWindow window,ElectronicsScreen view){window.UpdateLayout();view.UpdateLayout();return view.GetVisualDescendants().OfType<TextEditor>().Single();}
    static async Task Refresh(MainWindow window)=>await (Task)typeof(MainWindow).GetMethod("RefreshLessonPermission",Flags)!.Invoke(window,null)!;
    static void Capture(MainWindow window,string name)
    {
        window.UpdateLayout();var directory=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(directory);
        using var frame=window.CaptureRenderedFrame();frame!.Save(Path.Combine(directory,name));
    }
    [AvaloniaFact]
    public async Task FreeQueueRecoveryDuringPauseKeepsProjectLessonAndOriginalSubmission()
    {
        await using var f=await LiveClassroom.Start();var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var root=Path.Combine(Path.GetTempPath(),"classroom-free-recovery-ui-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");
        var context=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null,id);
        var project=CircuitProject.Blink();var initial=project.Serialize();await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(initial));
        var path=Path.Combine(root,"electronics",context.ProjectFileName);var report=Guid.NewGuid().ToString("N");string client;
        using(var history=new ElectronicsHistoryStore(path+".history",context,initial))
        using(var queue=new ElectronicsSync(f.Student,path+".sync.json",_=>{},false,freeContext:context.FreeContext()))
        {
            queue.QueueDraft(initial);queue.Record("submitted",initial,expectedAssignmentVersion:1,reportId:report);client=queue.ClientId;
            project.Code="// retained after recovery";history.Record(project.Serialize(),"code-edit");queue.QueueDraft(project.Serialize());ElectronicsHistoryStore.Mirror(path,project.Serialize());
        }
        File.WriteAllText(path+".sync.json","broken");
        var window=new MainWindow(false,false,root){Width=900,Height=900};
        typeof(MainWindow).GetField("api",Flags)!.SetValue(window,f.Student);typeof(MainWindow).GetField("login",Flags)!.SetValue(window,login);window.Show();ElectronicsScreen? view=null;
        try
        {
            await window.Safe(()=>window.OpenFreeProject(id));window.UpdateLayout();Assert.Single(window.GetVisualDescendants().OfType<ElectronicsOutboxRecoveryView>());
            await f.Admin.Post<LessonView>($"teacher/lessons/{context.LessonId}/pause");
            Click(window,"Восстановить предыдущую очередь");await Wait(()=>window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            view=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await Refresh(window);
            Assert.False(view.IsEffectivelyEnabled);Assert.Equal(id,view.FreeProjectId);Assert.Equal(project.Code,Code(window,view).Text);
            await f.Admin.Post<LessonView>($"teacher/lessons/{context.LessonId}/resume");await Refresh(window);Assert.True(view.IsEffectivelyEnabled);
            Assert.True(await view.Synchronize());var item=Assert.Single((await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?projectId="+id)).Items);
            Assert.Equal(report,item.ReportId);Assert.Equal(context.LessonId,item.LessonId);
            Assert.Equal(client,ElectronicsSync.ReadOutbox(path+".sync.json",null,freeContext:context.FreeContext()).ClientId);
        }
        finally
        {
            await (Task)typeof(MainWindow).GetMethod("StopElectronicsDelivery",Flags)!.Invoke(window,null)!;
            view?.Dispose();window.Preview(new TextBlock());window.UpdateLayout();window.Close();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
    [AvaloniaFact]
    public async Task LegacyLocalProjectRemainsSeparateAndIsNotConvertedWhenOpeningThePicker()
    {
        await using var f=await LiveClassroom.Start();var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var root=Path.Combine(Path.GetTempPath(),"classroom-legacy-picker-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"electronics"));
        var context=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null);
        var path=Path.Combine(root,"electronics",context.ProjectFileName);var project=CircuitProject.Blink();project.Code="// original legacy work";File.WriteAllText(path,project.Serialize());
        var window=new MainWindow(false,false,root){Width=900,Height=900};
        typeof(MainWindow).GetField("api",Flags)!.SetValue(window,f.Student);typeof(MainWindow).GetField("login",Flags)!.SetValue(window,login);window.Show();ElectronicsScreen? view=null;
        try
        {
            await window.EnterLesson(new("electronics"));window.UpdateLayout();Assert.Equal(project.Serialize(),File.ReadAllText(path));
            Assert.Empty((await f.Student.Get<ElectronicsProjectList>("electronics/projects")).Projects);
            var button=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Прежняя локальная схема");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>button.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            view=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();Assert.Null(view.FreeProjectId);Assert.Equal(path,view.SavePath);
            Assert.Equal(project.Code,Code(window,view).Text);Assert.True(await view.Synchronize());
            Assert.Null(ElectronicsSync.ReadOutbox(path+".sync.json",null).FreeContext);
            Assert.Empty((await f.Student.Get<ElectronicsProjectList>("electronics/projects")).Projects);
        }
        finally
        {
            await (Task)typeof(MainWindow).GetMethod("StopElectronicsDelivery",Flags)!.Invoke(window,null)!;
            view?.Dispose();window.Preview(new TextBlock());window.UpdateLayout();window.Close();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
    [AvaloniaFact]
    public async Task PickerCreatesSeparateEditorsSubmitsReviewsAndCopiesThroughNativeControls()
    {
        await using var f=await LiveClassroom.Start();var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var root=Path.Combine(Path.GetTempPath(),"classroom-free-ui-"+Guid.NewGuid().ToString("N"));
        var window=new MainWindow(false,false,root){Width=1320,Height=900};
        typeof(MainWindow).GetField("api",Flags)!.SetValue(window,f.Student);typeof(MainWindow).GetField("login",Flags)!.SetValue(window,login);window.Show();
        var editors=new List<ElectronicsScreen>();
        try
        {
            await window.EnterLesson(new("electronics"));
            async Task<ElectronicsScreen> Create(string title)
            {
                window.UpdateLayout();
                var picker=window.GetVisualDescendants().OfType<ElectronicsProjectPicker>().Single();picker.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="FreeProjectName").Text=title;
                var button=picker.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Создать схему");
                Click(picker,"Создать схему");await Wait(()=>button.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
                var view=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();editors.Add(view);return view;
            }
            var first=await Create("Первая свободная схема");Assert.NotNull(first.FreeProjectId);Assert.Null(first.AssignmentId);
            Code(window,first).Text="// first pupil draft";Assert.True(await first.Synchronize());
            Click(first,"Мои схемы");await Wait(()=>window.GetVisualDescendants().OfType<ElectronicsProjectPicker>().Any());
            var second=await Create("Вторая свободная схема");Assert.NotEqual(first.FreeProjectId,second.FreeProjectId);
            Code(window,second).Text="// second pupil draft";Assert.True(await second.Synchronize());
            Click(second,"Мои схемы");await Wait(()=>window.GetVisualDescendants().OfType<ElectronicsProjectPicker>().Any());
            var picker=window.GetVisualDescendants().OfType<ElectronicsProjectPicker>().Single();
            await Wait(()=>picker.GetVisualDescendants().OfType<Button>().Any(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Открыть «Первая свободная схема»"));
            Capture(window,"free-project-picker-1320.png");window.Width=900;Capture(window,"free-project-picker-900.png");
            picker.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Открыть «Первая свободная схема»").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            Assert.Same(first,window.GetVisualDescendants().OfType<ElectronicsScreen>().Single());Assert.Equal("// first pupil draft",Code(window,first).Text);
            Click(first,"Отправить на проверку");await Wait(()=>first.GetVisualDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Ожидает проверки"));
            var item=Assert.Single((await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?projectId="+first.FreeProjectId)).Items);
            var route=$"teacher/reviews/electronics/{f.StudentId}/{item.ReportId}";var detail=await f.Admin.Get<ElectronicsReviewDetail>(route);
            await f.Admin.Put<ElectronicsReviewDecision>(route+"/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest("accepted","Схема проверена.",5,null,detail.AssignmentVersion));
            await Refresh(window);Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("Оценка: 5")==true);
            Capture(window,"free-project-review-900.png");
            var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause");await Refresh(window);
            Assert.False(((ContentControl)typeof(MainWindow).GetField("body",Flags)!.GetValue(window)!).IsEnabled);
            await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/resume");await Refresh(window);
            var head=await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+first.FreeProjectId);
            var remote=CircuitProject.Parse(head.ProjectJson);remote.Code="// changed on another device";
            await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,remote.Serialize(),[],FreeContext:new(head.Id,lesson.Id,head.Revision)));
            Code(window,first).Text="// local conflict copy";Assert.False(await first.Synchronize());
            Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(),t=>t.IsVisible&&t.Text?.Contains("другом устройстве")==true);
            var copyButton=first.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Сохранить отдельную схему");
            Click(first,"Сохранить отдельную схему");await Wait(()=>copyButton.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsScreen>().Any(v=>v.FreeProjectId!=first.FreeProjectId));
            var copy=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();editors.Add(copy);Assert.True(await copy.Synchronize());window.UpdateLayout();
            Assert.Equal("// local conflict copy",Code(window,copy).Text);
            Assert.Equal(3,(await f.Student.Get<ElectronicsProjectList>("electronics/projects")).Projects.Length);
            Assert.Equal("// second pupil draft",CircuitProject.Parse((await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+second.FreeProjectId)).ProjectJson).Code);
            Assert.Equal(remote.Serialize(),(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+first.FreeProjectId)).ProjectJson);
        }
        catch(Exception error){Console.WriteLine("Free project UI failure: "+error);throw;}
        finally
        {
            await (Task)typeof(MainWindow).GetMethod("StopElectronicsDelivery",Flags)!.Invoke(window,null)!;
            foreach(var editor in editors.Distinct())editor.Dispose();window.Preview(new TextBlock());window.UpdateLayout();window.Close();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
}
