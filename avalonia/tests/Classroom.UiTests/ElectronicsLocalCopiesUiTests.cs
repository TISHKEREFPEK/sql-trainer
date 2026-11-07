using System.Reflection;
using Avalonia;
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

public sealed class ElectronicsLocalCopiesUiTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static async Task Wait(Func<bool> condition){for(var i=0;i<200&&!condition();i++)await Task.Delay(50);Assert.True(condition());}
    static Button Click(Control view,string title)
    {var button=view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==title);button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));return button;}
    static void Open(MainWindow window,ElectronicsLocalCopiesView view,ElectronicsHistoryContext context)
    {
        window.UpdateLayout();view.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Локальная копия "+context.LessonId+":"+(context.LocalCopyId??"original")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
    }
    static string PreviewCode(MainWindow window,ElectronicsLocalCopiesView view)
    {
        window.UpdateLayout();var snapshot=view.GetVisualDescendants().OfType<ElectronicsSnapshotView>().Single();var tabs=snapshot.GetVisualDescendants().OfType<TabControl>().Single();tabs.SelectedItem=tabs.Items.OfType<TabItem>().Single(t=>t.Header?.ToString()=="Код");window.UpdateLayout();return snapshot.GetVisualDescendants().OfType<TextEditor>().Single().Text;
    }
    static async Task Stop(MainWindow window)=>await (Task)typeof(MainWindow).GetMethod("StopElectronicsDelivery",Flags)!.Invoke(window,null)!;
    static async Task Refresh(MainWindow window)=>await (Task)typeof(MainWindow).GetMethod("RefreshLessonPermission",Flags)!.Invoke(window,null)!;
    static MainWindow Window(LiveClassroom f,LoginDto login,string root)
    {var window=new MainWindow(false,false,root){Width=900,Height=900};typeof(MainWindow).GetField("api",Flags)!.SetValue(window,f.Student);typeof(MainWindow).GetField("login",Flags)!.SetValue(window,login);window.Show();return window;}
    static void Capture(MainWindow window,string name)
    {window.UpdateLayout();var directory=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(directory);using var frame=window.CaptureRenderedFrame();frame!.Save(Path.Combine(directory,name));}
    [AvaloniaFact]
    public async Task PickerShowsPastLessonCopiesAndDamagedHistoryDoesNotBlockPreviewOrIndependentClone()
    {
        await using var f=await LiveClassroom.Start();var root=Path.Combine(Path.GetTempPath(),"classroom-catalog-ui-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");
        var old=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null,id);
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(ElectronicsLocalCopiesTests.Json("// server original")));
        var oldFile=ElectronicsLocalCopiesTests.Seed(root,old,f,"// previous lesson work");var damagedContext=old with{LocalCopyId=Guid.NewGuid().ToString("N")};
        var damaged=ElectronicsLocalCopiesTests.Seed(root,damagedContext,f,"// mirror of damaged history");File.WriteAllText(Directory.GetFiles(damaged+".history","c-*.json").Single(),"damaged archive");
        await f.Admin.Post<LessonView>($"teacher/lessons/{old.LessonId}/finish");
        var next=await f.Admin.Post<LessonView>("teacher/lessons",new CreateLessonRequest(Guid.NewGuid().ToString("N"),new(null,OpenToAllGroups:true,FreePractice:true)));await f.Admin.Post<LessonView>($"teacher/lessons/{next.Id}/start");
        var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));var window=Window(f,login,root);ElectronicsScreen? copied=null;
        try
        {
            await window.EnterLesson(new("electronics"));window.UpdateLayout();
            var picker=window.GetVisualDescendants().OfType<ElectronicsProjectPicker>().Single();var button=Click(picker,"Копии на этом ПК");
            await Wait(()=>button.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsLocalCopiesView>().Any());window.UpdateLayout();var view=window.GetVisualDescendants().OfType<ElectronicsLocalCopiesView>().Single();
            var retained=ElectronicsLocalCopiesTests.Hashes(oldFile);Open(window,view,damagedContext);Assert.Equal("// mirror of damaged history",PreviewCode(window,view));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("только файл схемы")==true);
            Open(window,view,old);Assert.Equal("// previous lesson work",PreviewCode(window,view));ElectronicsLocalCopiesTests.Unchanged(retained);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Другое занятие");Capture(window,"free-local-copies-900.png");
            var clone=Click(view,"Сохранить отдельной схемой");await Wait(()=>clone.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());window.UpdateLayout();
            copied=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();Assert.NotEqual(id,copied.FreeProjectId);Assert.Equal("// previous lesson work",copied.GetVisualDescendants().OfType<TextEditor>().Single().Text);Assert.True(await copied.Synchronize());
            Assert.Equal("// server original",CircuitProject.Parse((await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson).Code);
            Assert.Equal("damaged archive",File.ReadAllText(Directory.GetFiles(damaged+".history","c-*.json").Single()));
        }
        finally
        {window.ClosePage(false);await Stop(window);copied?.Dispose();window.Preview(new TextBlock());window.UpdateLayout();window.Close();await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.ApplicationIdle);}
    }
    [AvaloniaFact]
    public async Task BrokenSelectionOpensRecoveryAndRequiresExplicitResumptionBeforeReopeningDuringPause()
    {
        await using var f=await LiveClassroom.Start();var root=Path.Combine(Path.GetTempPath(),"classroom-selection-recovery-ui-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");var report=Guid.NewGuid().ToString("N");
        var context=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null,id);var directory=Path.Combine(root,"electronics");
        var remote=await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(ElectronicsLocalCopiesTests.Json("// server")));
        var original=ElectronicsLocalCopiesTests.Seed(root,context,f,"// chosen original",report);ElectronicsCopySelection.SelectServer(directory,context,original,remote,f.Student);
        var pointer=ElectronicsCopySelection.PointerPath(directory,context);
        var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));var window=Window(f,login,root);ElectronicsScreen? source=null;
        try
        {
            await window.OpenFreeProject(id);window.UpdateLayout();var cached=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await cached.PauseDelivery();
            await f.Admin.Put<LessonView>($"teacher/lessons/{context.LessonId}/policy",new LessonPolicyRequest(null,AllowSql:false,OpenToAllGroups:true,FreePractice:true));
            File.WriteAllText(pointer,"broken selected copy");
            await window.Safe(()=>window.OpenFreeProject(id));window.UpdateLayout();var view=window.GetVisualDescendants().OfType<ElectronicsLocalCopiesView>().Single();
            Assert.True(cached.IsDisposed);
            Assert.Empty(window.GetVisualDescendants().OfType<ElectronicsScreen>());Assert.Equal("broken selected copy",File.ReadAllText(pointer));
            Open(window,view,context);Assert.Equal("// chosen original",PreviewCode(window,view));
            var choose=view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Выбрать эту копию для продолжения");Assert.False(choose.IsEnabled);
            var actionsScroll=choose.GetVisualAncestors().OfType<ScrollViewer>().First();var top=choose.TranslatePoint(new(0,0),actionsScroll)!.Value.Y;
            Assert.True(top>=0&&top+choose.Bounds.Height<=actionsScroll.Bounds.Height,"Кнопка восстановления должна быть видна полностью без прокрутки предупреждения.");
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("из них сдач: 1")==true);Capture(window,"free-selection-recovery-900.png");
            await f.Admin.Post<LessonView>($"teacher/lessons/{context.LessonId}/pause");await Refresh(window);Assert.True(view.IsEffectivelyEnabled);
            view.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked=true;Assert.True(choose.IsEnabled);
            Click(view,"Выбрать эту копию для продолжения");await Wait(()=>choose.IsEnabled&&window.GetVisualDescendants().OfType<ElectronicsScreen>().Any());window.UpdateLayout();source=window.GetVisualDescendants().OfType<ElectronicsScreen>().Single();
            Assert.False(source.IsEffectivelyEnabled);Assert.Equal(original,source.SavePath);Assert.Equal("// chosen original",source.GetVisualDescendants().OfType<TextEditor>().Single().Text);
            await f.Admin.Post<LessonView>($"teacher/lessons/{context.LessonId}/resume");await Refresh(window);Assert.True(await source.Synchronize());
            var detail=await f.Admin.Get<ElectronicsStudentDetail>("teacher/electronics/"+f.StudentId);Assert.Equal(report,Assert.Single(detail.Reports).Id);
        }
        finally
        {window.ClosePage(false);await Stop(window);source?.Dispose();window.Preview(new TextBlock());window.UpdateLayout();window.Close();await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>{},Avalonia.Threading.DispatcherPriority.ApplicationIdle);}
    }
}
