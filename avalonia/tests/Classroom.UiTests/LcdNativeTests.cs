using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class LcdNativeTests
{
    static readonly System.Reflection.BindingFlags Private=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
    static async Task Start(ElectronicsScreen view)
    {
        await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Private)!.Invoke(view,[true])!;
        for(var i=0;i<200&&!Display(view);i++)await Task.Delay(40);
        Assert.True(Display(view),((TextBlock)typeof(ElectronicsScreen).GetField("status",Private)!.GetValue(view)!).Text);
    }
    static bool Display(ElectronicsScreen view)=>((Dictionary<string,LcdFrame>)typeof(ElectronicsScreen).GetField("displays",Private)!.GetValue(view)!).Values.Any(f=>f.Display&&f.Cells.Take(9).Select(v=>(char)v).SequenceEqual("LCD ready"));
    static void Capture(Window window,string name)
    {window.UpdateLayout();using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);var directory=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(directory);frame!.Save(Path.Combine(directory,name));}
    static CircuitProject Template(bool large)
    {var p=LcdI2cTests.Project(large?"mega2560":"uno",large);p.Code=CircuitCodeGuide.Build(p).Sketch!;return p;}
    [AvaloniaTheory]
    [InlineData(900,false)]
    [InlineData(1320,true)]
    public async Task EditorRunsWiredTemplateAndRendersPixels(int width,bool large)
    {
        var directory=Path.Combine(Path.GetTempPath(),"classroom-lcd-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var path=Path.Combine(directory,"project.json");var project=Template(large);File.WriteAllText(path,project.Serialize());
        var window=new MainWindow(true,false){Width=width,Height=900};using var view=new ElectronicsScreen(window,path,teacher:true);window.Preview(view);window.Show();window.UpdateLayout();await Start(view);
        Assert.Single(view.GetVisualDescendants().OfType<LcdDisplayFace>());Capture(window,$"lcd-i2c-{width}.png");view.Suspend();Assert.Equal(project.Serialize(),File.ReadAllText(path));
        typeof(ElectronicsScreen).GetField("selected",Private)!.SetValue(view,"lcd");typeof(ElectronicsScreen).GetMethod("Inspect",Private)!.Invoke(view,null);view.UpdateLayout();
        var address=view.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Items.OfType<string>().Contains("0x27"));address.SelectedIndex=6;
        view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Применить адрес").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));view.UpdateLayout();
        var saved=CircuitProject.Parse(File.ReadAllText(path));Assert.Equal(38,saved.Parts.Single(p=>p.Id=="lcd").Value);Assert.Equal(project.Code,saved.Code);Assert.Equal(Wire.Write(project.Wires),Wire.Write(saved.Wires));
        Capture(window,$"lcd-i2c-properties-{width}.png");window.Close();
    }
    [AvaloniaFact]
    public async Task SubmittedLcdRunsIndependentlyAndTypedBusHistorySurvivesBackup()
    {
        await using var f=await LiveClassroom.Start();var project=Template(true);var original=project.Serialize();var task=Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+task,new ElectronicsTaskRequest("LCD","Проверьте подключение и вывод LCD.",original));
        var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([task]));
        var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+task);var directory=Path.Combine(Path.GetTempPath(),"classroom-lcd-assigned-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var window=new MainWindow(false,false){Width=1000,Height=900};using var view=new ElectronicsScreen(window,Path.Combine(directory,"project.json"),f.Student,assignment:assignment);window.Preview(view);window.Show();window.UpdateLayout();await Start(view);view.Suspend();Assert.True(await view.Synchronize(),((TextBlock)typeof(ElectronicsScreen).GetField("syncStatus",Private)!.GetValue(view)!).Text);
        var streams=await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?assignmentId={assignment.Id}");var stream=Assert.Single(streams);
        var events=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");
        var packets=events.Events.SelectMany(e=>e.Change.I2cIo??[]).ToArray();Assert.NotEmpty(packets);Assert.All(packets,p=>{Assert.Equal(39,p.Address);Assert.True(p.Ack);});
        Assert.DoesNotContain("i2cIo",Wire.Write(events.Events.First(e=>e.Change.I2cIo is null).Change));
        var sync=(ElectronicsSync)typeof(ElectronicsScreen).GetField("sync",Private)!.GetValue(view)!;var report=Guid.NewGuid().ToString("N");sync.Record("submitted",original,expectedAssignmentVersion:assignment.StateVersion,reportId:report);Assert.True(await view.Synchronize());
        var detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");
        var copyWindow=new MainWindow(true,false){Width=1000,Height=900};using var copy=new ElectronicsReviewWorkspace(copyWindow,detail);copyWindow.Preview(copy);copyWindow.Show();copyWindow.UpdateLayout();var copied=copy.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await Start(copied);copied.Suspend();copyWindow.Close();
        Assert.Equal(original,(await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}")).ProjectJson);
        Assert.Equal(Wire.Write(packets),Wire.Write((await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes")).Events.SelectMany(e=>e.Change.I2cIo??[]).ToArray()));
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        var restored=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");Assert.Equal(Wire.Write(packets),Wire.Write(restored.Events.SelectMany(e=>e.Change.I2cIo??[]).ToArray()));window.Close();
    }
}
