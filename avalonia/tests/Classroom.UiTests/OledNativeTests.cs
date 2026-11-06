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

public sealed class OledNativeTests
{
    static readonly System.Reflection.BindingFlags Private=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
    static async Task Start(ElectronicsScreen view)
    {
        await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Private)!.Invoke(view,[true])!;
        bool Ready()=>((Dictionary<string,OledFrame>)typeof(ElectronicsScreen).GetField("oledDisplays",Private)!.GetValue(view)!).Values.Any(f=>f.Display&&OledTests.Pixel(f,127,63));
        for(var i=0;i<220&&!Ready();i++)await Task.Delay(40);
        Assert.True(Ready(),((TextBlock)typeof(ElectronicsScreen).GetField("status",Private)!.GetValue(view)!).Text);
    }
    static void Capture(Window window,string name)
    {window.UpdateLayout();using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);var directory=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(directory);frame!.Save(Path.Combine(directory,name));}
    [AvaloniaTheory]
    [InlineData(900,"uno")]
    [InlineData(1320,"mega2560")]
    public async Task GraphicsTemplateRunsAndAddressIsSavedThroughNativeProperties(int width,string board)
    {
        var project=OledTests.Project(board);var directory=Path.Combine(Path.GetTempPath(),"classroom-oled-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var path=Path.Combine(directory,"project.json");File.WriteAllText(path,project.Serialize());
        var window=new MainWindow(true,false){Width=width,Height=900};using var view=new ElectronicsScreen(window,path,teacher:true);window.Preview(view);window.Show();window.UpdateLayout();await Start(view);Assert.Single(view.GetVisualDescendants().OfType<OledDisplayFace>());Capture(window,$"oled-i2c-{width}.png");view.Suspend();
        typeof(ElectronicsScreen).GetField("selected",Private)!.SetValue(view,"oled");typeof(ElectronicsScreen).GetMethod("Inspect",Private)!.Invoke(view,null);view.UpdateLayout();
        view.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Items.OfType<string>().Contains("0x3C")).SelectedIndex=1;
        view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Применить адрес").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));view.UpdateLayout();
        var saved=CircuitProject.Parse(File.ReadAllText(path));Assert.Equal(61,saved.Parts.Single(p=>p.Id=="oled").Value);Assert.Equal(project.Code,saved.Code);Assert.Equal(Wire.Write(project.Wires),Wire.Write(saved.Wires));Capture(window,$"oled-i2c-properties-{width}.png");window.Close();
    }
    [AvaloniaFact]
    public async Task SharedDisplaySubmissionAndI2cEvidenceSurviveIndependentReviewAndRestore()
    {
        await using var f=await LiveClassroom.Start();var project=OledTests.Project("mega2560",true);var json=project.Serialize();var task=Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+task,new ElectronicsTaskRequest("OLED","Проверьте LCD и OLED на одной шине.",json));var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([task]));var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+task);
        var directory=Path.Combine(Path.GetTempPath(),"classroom-oled-assigned-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var window=new MainWindow(false,false){Width=1000,Height=900};using var view=new ElectronicsScreen(window,Path.Combine(directory,"project.json"),f.Student,assignment:assignment);window.Preview(view);window.Show();window.UpdateLayout();await Start(view);
        view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Вписать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();var viewport=view.GetVisualDescendants().OfType<CircuitViewport>().Single();
        foreach(var part in project.Parts)foreach(var corner in PartArtwork.Corners(part)){var y=corner.Y*viewport.Zoom+viewport.Pan.Y;Assert.InRange(y,56,viewport.Bounds.Height-60);}
        Capture(window,"oled-lcd-shared-submission.png");view.Suspend();Assert.True(await view.Synchronize());
        var stream=Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?assignmentId={assignment.Id}"));
        async Task<ElectronicsI2cIo[]> Packets()=>(await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes")).Events.SelectMany(e=>e.Change.I2cIo??[]).ToArray();
        var packets=await Packets();Assert.Contains(packets,p=>p.Address==60&&p.Ack);Assert.Contains(packets,p=>p.Address==39&&p.Ack);
        var sync=(ElectronicsSync)typeof(ElectronicsScreen).GetField("sync",Private)!.GetValue(view)!;var report=Guid.NewGuid().ToString("N");sync.Record("submitted",json,expectedAssignmentVersion:assignment.StateVersion,reportId:report);Assert.True(await view.Synchronize());
        var detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");var copyWindow=new MainWindow(true,false){Width=1000,Height=900};using var copy=new ElectronicsReviewWorkspace(copyWindow,detail);copyWindow.Preview(copy);copyWindow.Show();copyWindow.UpdateLayout();var copied=copy.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await Start(copied);copied.Suspend();copyWindow.Close();
        Assert.Equal(json,(await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}")).ProjectJson);Assert.Equal(Wire.Write(packets),Wire.Write(await Packets()));
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));Assert.Equal(Wire.Write(packets),Wire.Write(await Packets()));window.Close();
    }
}
