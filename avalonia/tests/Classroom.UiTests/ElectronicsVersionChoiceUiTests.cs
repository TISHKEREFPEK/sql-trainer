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

public sealed class ElectronicsVersionChoiceUiTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static Button Button(Control view,string title)=>view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==title);
    static Button Click(Control view,string title){var button=Button(view,title);button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));return button;}
    static async Task Wait(Func<bool> condition){for(var i=0;i<200&&!condition();i++)await Task.Delay(50);Assert.True(condition());}
    static async Task Refresh(MainWindow window)=>await (Task)typeof(MainWindow).GetMethod("RefreshLessonPermission",Flags)!.Invoke(window,null)!;
    static TextEditor Code(MainWindow window,ElectronicsScreen view){window.UpdateLayout();return view.GetVisualDescendants().OfType<TextEditor>().Single();}
    static ElectronicsVersionChoiceView Choice(MainWindow window){window.UpdateLayout();return window.GetVisualDescendants().OfType<ElectronicsVersionChoiceView>().Single();}
    static void Tab(MainWindow window,Control view,string title){window.UpdateLayout();view.GetVisualDescendants().OfType<TabControl>().First().SelectedItem=view.GetVisualDescendants().OfType<TabControl>().First().Items.OfType<TabItem>().Single(t=>t.Header?.ToString()==title);window.UpdateLayout();}
    static void Capture(MainWindow window,string name)
    {
        window.UpdateLayout();var directory=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(directory);
        using var frame=window.CaptureRenderedFrame();frame!.Save(Path.Combine(directory,name));
    }
    [AvaloniaFact]
    public async Task NativeChoiceRefreshesChangedServerPreservesLocalCopyAndCanCloneItLater()
    {
        await using var f=await LiveClassroom.Start();var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var root=Path.Combine(Path.GetTempPath(),"classroom-version-ui-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");
        var project=CircuitProject.Blink();project.Name="Выбор версии";project.Code="// initial";
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(project.Serialize()));
        var window=new MainWindow(false,false,root){Width=1320,Height=900};
        typeof(MainWindow).GetField("api",Flags)!.SetValue(window,f.Student);typeof(MainWindow).GetField("login",Flags)!.SetValue(window,login);window.Show();
        var editors=new List<ElectronicsScreen>();
        try
        {
            await window.OpenFreeProject(id);window.UpdateLayout();var source=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();editors.Add(source);Assert.True(await source.Synchronize());
            async Task Remote(string code)
            {
                var head=await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id);var circuit=CircuitProject.Parse(head.ProjectJson);circuit.Code=code;
                await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,circuit.Serialize(),[],FreeContext:new(id,f.Student.LessonId!,head.Revision)));
            }
            await Remote("// remote first");Code(window,source).Text="// retained local conflict";Assert.False(await source.Synchronize());
            var originalPath=source.SavePath;var context=source.HistoryContext!;
            var compare=Click(source,"Версии и копии");await Wait(()=>compare.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsVersionChoiceView>().Any());var choice=Choice(window);
            Click(choice,"Продолжить локальную");await Wait(()=>window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            Assert.Same(source,window.GetVisualDescendants().OfType<ElectronicsScreen>().Single());Assert.Equal("// retained local conflict",Code(window,source).Text);
            compare=Click(source,"Версии и копии");await Wait(()=>compare.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsVersionChoiceView>().Any());choice=Choice(window);
            Capture(window,"free-version-choice-1320.png");window.Width=900;Capture(window,"free-version-choice-900.png");
            await Remote("// remote changed while comparing");var select=Click(choice,"Продолжить с серверной версией");await Wait(()=>select.IsEnabled);
            Assert.Same(choice,Choice(window));Assert.False(source.IsDisposed);Assert.Equal(originalPath,ElectronicsCopySelection.Resolve(Path.Combine(root,"electronics"),context).Path);
            Tab(window,choice,"Серверная версия");var remoteView=choice.GetVisualDescendants().OfType<ElectronicsSnapshotView>().Single();Tab(window,remoteView,"Код");
            Assert.Equal("// remote changed while comparing",remoteView.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            select=Click(choice,"Продолжить с серверной версией");await Wait(()=>select.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            var selected=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();editors.Add(selected);Assert.True(source.IsDisposed);Assert.NotEqual(originalPath,selected.SavePath);
            Assert.Equal("// remote changed while comparing",Code(window,selected).Text);Assert.Equal("// retained local conflict",CircuitProject.Parse(File.ReadAllText(originalPath)).Code);
            var oldQueue=File.ReadAllBytes(originalPath+".sync.json");var oldMirror=File.ReadAllBytes(originalPath);Assert.True(await selected.Synchronize());
            compare=Click(selected,"Версии и копии");await Wait(()=>compare.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsVersionChoiceView>().Any());choice=Choice(window);
            Tab(window,choice,"Локальные копии");Click(choice,"Просмотреть");var archived=choice.GetVisualDescendants().OfType<ElectronicsSnapshotView>().Single();Tab(window,archived,"Код");
            Assert.Equal("// retained local conflict",archived.GetVisualDescendants().OfType<TextEditor>().Single().Text);Assert.True(archived.GetVisualDescendants().OfType<TextEditor>().Single().IsReadOnly);
            Capture(window,"free-version-archive-900.png");
            var clone=Click(choice,"Сохранить отдельной схемой");await Wait(()=>clone.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            var copied=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();editors.Add(copied);Assert.NotEqual(id,copied.FreeProjectId);Assert.Equal("// retained local conflict",Code(window,copied).Text);Assert.True(await copied.Synchronize());
            Assert.Equal(oldQueue,File.ReadAllBytes(originalPath+".sync.json"));Assert.Equal(oldMirror,File.ReadAllBytes(originalPath));
            Assert.Equal("// remote changed while comparing",CircuitProject.Parse((await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson).Code);
            await window.ShowStudentHome();window.UpdateLayout();Assert.Empty(window.GetVisualDescendants().OfType<ElectronicsVersionChoiceView>());
        }
        finally
        {
            window.ClosePage(false);await (Task)typeof(MainWindow).GetMethod("StopElectronicsDelivery",Flags)!.Invoke(window,null)!;
            foreach(var editor in editors.Distinct())editor.Dispose();window.Preview(new TextBlock());window.UpdateLayout();window.Close();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
    [AvaloniaFact]
    public async Task VersionPageUsesElectronicsPermissionAndHonorsLessonPause()
    {
        await using var f=await LiveClassroom.Start();var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var lesson=f.Student.LessonId!;await f.Admin.Put<LessonView>("teacher/lessons/"+lesson+"/policy",new LessonPolicyRequest(null,AllowSql:false,OpenToAllGroups:true,FreePractice:true));
        var root=Path.Combine(Path.GetTempPath(),"classroom-version-ui-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(CircuitProject.Blink().Serialize()));
        var window=new MainWindow(false,false,root){Width=900,Height=900};typeof(MainWindow).GetField("api",Flags)!.SetValue(window,f.Student);typeof(MainWindow).GetField("login",Flags)!.SetValue(window,login);window.Show();ElectronicsScreen? source=null;
        try
        {
            await window.OpenFreeProject(id);window.UpdateLayout();source=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            await window.CompareFreeProject(source);var choice=Choice(window);await Refresh(window);Assert.True(choice.IsEffectivelyEnabled);
            await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/pause");await Refresh(window);Assert.False(choice.IsEffectivelyEnabled);
            await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/resume");await Refresh(window);Assert.True(choice.IsEffectivelyEnabled);
            window.ClosePage();Assert.Same(source,window.GetVisualDescendants().OfType<ElectronicsScreen>().Single());Assert.False(source.IsDisposed);Assert.True(await source.Synchronize());
        }
        finally
        {
            window.ClosePage(false);await (Task)typeof(MainWindow).GetMethod("StopElectronicsDelivery",Flags)!.Invoke(window,null)!;
            source?.Dispose();window.Preview(new TextBlock());window.UpdateLayout();window.Close();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
}
