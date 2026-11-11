using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class Dht11NativeTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static string Output(ElectronicsScreen view)=>string.Join("",(string[])typeof(ElectronicsScreen).GetField("serialText",Flags)!.GetValue(view)!);
    static string Status(ElectronicsScreen view)=>((TextBlock)typeof(ElectronicsScreen).GetField("status",Flags)!.GetValue(view)!).Text??"";
    static async Task Run(ElectronicsScreen view,int humidity=53,int temperature=24)
    {
        await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Flags)!.Invoke(view,[true])!;
        ((DispatcherTimer)typeof(ElectronicsScreen).GetField("timer",Flags)!.GetValue(view)!).Stop();
        Assert.True((bool)typeof(ElectronicsScreen).GetField("running",Flags)!.GetValue(view)!,Status(view));
        var expected=$"{humidity}.00 T={temperature}.00";
        for(var i=0;i<360&&!Output(view).Contains(expected);i++)await (Task)typeof(ElectronicsScreen).GetMethod("Tick",Flags)!.Invoke(view,null)!;
        Assert.Contains(expected,Output(view));
    }
    static void Capture(Window w,string name)
    {
        w.UpdateLayout();using var image=w.CaptureRenderedFrame();Assert.NotNull(image);var folder=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.GetTempPath();Directory.CreateDirectory(folder);image.Save(Path.Combine(folder,name));
    }
    [AvaloniaTheory]
    [InlineData("uno",900)]
    [InlineData("pico",1320)]
    public async Task NativeEditorReadsSavedDeviceAndPersistsInteractiveInputs(string board,int width)
    {
        var p=Dht11Tests.Project(board);p.Code=CircuitCodeGuide.Build(p).Sketch!;var directory=Path.Combine(Path.GetTempPath(),"classroom-dht-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var file=Path.Combine(directory,"work.json");File.WriteAllText(file,p.Serialize());
        var w=new MainWindow(true,false){Width=width,Height=900};using var view=new ElectronicsScreen(w,file,teacher:true);w.Preview(view);w.Show();w.UpdateLayout();await Run(view);
        typeof(ElectronicsScreen).GetField("selected",Flags)!.SetValue(view,"sensor");typeof(ElectronicsScreen).GetMethod("Inspect",Flags)!.Invoke(view,null);
        var panel=(Panel)typeof(ElectronicsScreen).GetField("properties",Flags)!.GetValue(view)!;var sliders=panel.GetVisualDescendants().OfType<Slider>().ToArray();Assert.Equal(2,sliders.Length);sliders[0].Value=31;sliders[1].Value=71;
        Assert.True((bool)typeof(ElectronicsScreen).GetField("running",Flags)!.GetValue(view)!);
        for(var i=0;i<560&&!Output(view).Contains("71.00 T=31.00");i++)await (Task)typeof(ElectronicsScreen).GetMethod("Tick",Flags)!.Invoke(view,null)!;
        Assert.Contains("71.00 T=31.00",Output(view));view.Suspend();
        var saved=CircuitProject.Parse(File.ReadAllText(file));Assert.Equal(31,saved.Parts.Single(p=>p.Kind==PartKind.Dht11).Value);Assert.Equal(71,CircuitDht11.Humidity(saved.Parts.Single(p=>p.Kind==PartKind.Dht11)));Assert.Equal(p.Code,saved.Code);
        view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Вписать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));w.UpdateLayout();Capture(w,$"dht11-{board}-{width}.png");
        var svg=(string)typeof(ElectronicsScreen).GetMethod("Svg",Flags)!.Invoke(view,null)!;System.Xml.Linq.XDocument.Parse(svg);Assert.Contains("DHT11",svg);var scene=(Canvas)typeof(ElectronicsScreen).GetField("canvas",Flags)!.GetValue(view)!;Assert.Single(scene.GetVisualDescendants().OfType<Dht11Face>());w.Close();
    }
    [AvaloniaFact]
    public async Task SubmittedDhtWorkRunsAtTeacherAndSurvivesGradeAndBackup()
    {
        await using var f=await LiveClassroom.Start();var p=Dht11Tests.Project("mega2560");p.Code=CircuitCodeGuide.Build(p).Sketch!;var json=p.Serialize();var task=Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+task,new ElectronicsTaskRequest("DHT11","Подключите датчик и прочитайте показания.",json));var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([task]));var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+task);
        var directory=Path.Combine(Path.GetTempPath(),"classroom-dht-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var file=Path.Combine(directory,"student.json");
        var w=new MainWindow(false,false){Width=1320,Height=900};using var view=new ElectronicsScreen(w,file,f.Student,assignment:assignment);w.Preview(view);w.Show();w.UpdateLayout();await Run(view);
        typeof(ElectronicsScreen).GetField("selected",Flags)!.SetValue(view,"sensor");typeof(ElectronicsScreen).GetMethod("Inspect",Flags)!.Invoke(view,null);
        var properties=(Panel)typeof(ElectronicsScreen).GetField("properties",Flags)!.GetValue(view)!;var inputs=properties.GetVisualDescendants().OfType<Slider>().ToArray();inputs[0].Value=31;inputs[1].Value=71;
        view.Suspend();json=File.ReadAllText(file);Assert.True(await view.Synchronize());
        var local=(ElectronicsJournal)typeof(ElectronicsScreen).GetProperty("LocalHistory",Flags)!.GetValue(view)!;Assert.Equal(json,local.Replay());Assert.True(local.Changes.Count(c=>c.Kind=="control-change")>=2);
        var sync=(ElectronicsSync)typeof(ElectronicsScreen).GetField("sync",Flags)!.GetValue(view)!;var report=Guid.NewGuid().ToString("N");sync.Record("submitted",json,expectedAssignmentVersion:assignment.StateVersion,reportId:report);Assert.True(await view.Synchronize());
        var detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(json,detail.ProjectJson);
        var tw=new MainWindow(true,false){Width=1320,Height=900};using var workspace=new ElectronicsReviewWorkspace(tw,detail);tw.Preview(workspace);tw.Show();tw.UpdateLayout();var copy=workspace.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await Run(copy,71,31);Capture(tw,"dht11-teacher-review.png");copy.Suspend();tw.Close();
        var stream=Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?assignmentId={assignment.Id}"));var history=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");Assert.Contains(history.Events,e=>e.Change.SerialIo?.Text.Contains("53.00 T=24.00")==true);Assert.True(history.Events.Count(e=>e.Change.Kind=="control-change")>=2);
        await f.Admin.Put<ElectronicsReviewDecision>($"teacher/reviews/electronics/{f.StudentId}/{report}/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest("accepted","Показания и подключение проверены.",5,null,detail.AssignmentVersion));
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        var restored=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(json,restored.ProjectJson);Assert.Equal(5,restored.Item.Grade);Assert.Equal(Wire.Write(history),Wire.Write(await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes")));w.Close();
    }
}
