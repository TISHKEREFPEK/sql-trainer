using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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

public sealed class Hcsr04NativeTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static string Output(ElectronicsScreen v)=>string.Join("",(string[])typeof(ElectronicsScreen).GetField("serialText",Flags)!.GetValue(v)!);
    static bool Measured(ElectronicsScreen v,int cm)=>Regex.Matches(Output(v),@"HC-SR04 cm=(\d+(?:\.\d+)?)").Any(m=>Math.Abs(double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)-cm)<2);
    static async Task Advance(ElectronicsScreen v,Func<bool> until)
    {
        for(var i=0;i<260&&!until();i++)await (Task)typeof(ElectronicsScreen).GetMethod("Tick",Flags)!.Invoke(v,null)!;
        Assert.True(until(),Output(v));
    }
    static async Task Run(ElectronicsScreen v,int cm=100)
    {
        await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Flags)!.Invoke(v,[true])!;((DispatcherTimer)typeof(ElectronicsScreen).GetField("timer",Flags)!.GetValue(v)!).Stop();
        var status=(TextBlock)typeof(ElectronicsScreen).GetField("status",Flags)!.GetValue(v)!;Assert.True((bool)typeof(ElectronicsScreen).GetField("running",Flags)!.GetValue(v)!,status.Text);await Advance(v,()=>Measured(v,cm));
    }
    static Panel Inspect(ElectronicsScreen v)
    {
        typeof(ElectronicsScreen).GetField("selected",Flags)!.SetValue(v,"sonar");typeof(ElectronicsScreen).GetMethod("Inspect",Flags)!.Invoke(v,null);return (Panel)typeof(ElectronicsScreen).GetField("properties",Flags)!.GetValue(v)!;
    }
    static void Capture(Window w,string name){w.UpdateLayout();using var image=w.CaptureRenderedFrame();Assert.NotNull(image);var folder=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.GetTempPath();Directory.CreateDirectory(folder);image.Save(Path.Combine(folder,name));}
    [AvaloniaTheory]
    [InlineData("uno",900)]
    [InlineData("pico",1320)]
    public async Task EditorMeasuresChangedDistanceAndMissingTargetAndPreservesProject(string board,int width)
    {
        var p=Hcsr04Tests.Project(board);p.Code=CircuitCodeGuide.Build(p).Sketch!;var folder=Path.Combine(Path.GetTempPath(),"classroom-hcsr-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var file=Path.Combine(folder,"work.json");File.WriteAllText(file,p.Serialize());
        var w=new MainWindow(true,false){Width=width,Height=900};using var v=new ElectronicsScreen(w,file,teacher:true);w.Preview(v);w.Show();w.UpdateLayout();await Run(v);var properties=Inspect(v);Assert.Single(properties.GetVisualDescendants().OfType<Slider>()).Value=200;await Advance(v,()=>Measured(v,200));
        properties.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Убрать цель").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Advance(v,()=>Output(v).Contains("HC-SR04: no echo"));v.Suspend();
        var saved=CircuitProject.Parse(File.ReadAllText(file));Assert.Equal(p.Code,saved.Code);Assert.Equal(200,saved.Parts[1].Value);Assert.False(saved.Parts[1].Active);
        v.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Вписать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Capture(w,$"hcsr04-{board}-{width}.png");
        var svg=(string)typeof(ElectronicsScreen).GetMethod("Svg",Flags)!.Invoke(v,null)!;System.Xml.Linq.XDocument.Parse(svg);Assert.Contains("HC-SR04",svg);var scene=(Canvas)typeof(ElectronicsScreen).GetField("canvas",Flags)!.GetValue(v)!;Assert.Single(scene.GetVisualDescendants().OfType<Hcsr04Face>());w.Close();
    }
    [AvaloniaFact]
    public async Task ChangedDistanceIsJournaledAndSubmittedCopyRunsAfterGradeAndRestore()
    {
        await using var f=await LiveClassroom.Start();var p=Hcsr04Tests.Project("mega2560");p.Code=CircuitCodeGuide.Build(p).Sketch!;var json=p.Serialize();var task=Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+task,new ElectronicsTaskRequest("HC-SR04","Измерьте расстояние до цели.",json));var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([task]));var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+task);
        var folder=Path.Combine(Path.GetTempPath(),"classroom-hcsr-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var file=Path.Combine(folder,"work.json");var w=new MainWindow(false,false){Width=1320,Height=900};using var v=new ElectronicsScreen(w,file,f.Student,assignment:assignment);w.Preview(v);w.Show();w.UpdateLayout();await Run(v);Assert.Single(Inspect(v).GetVisualDescendants().OfType<Slider>()).Value=200;await Advance(v,()=>Measured(v,200));v.Suspend();json=File.ReadAllText(file);Assert.True(await v.Synchronize());
        var local=(ElectronicsJournal)typeof(ElectronicsScreen).GetProperty("LocalHistory",Flags)!.GetValue(v)!;Assert.Equal(json,local.Replay());Assert.Contains(local.Changes,c=>c.Kind=="control-change");
        var sync=(ElectronicsSync)typeof(ElectronicsScreen).GetField("sync",Flags)!.GetValue(v)!;var report=Guid.NewGuid().ToString("N");sync.Record("submitted",json,expectedAssignmentVersion:assignment.StateVersion,reportId:report);Assert.True(await v.Synchronize());
        var detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(json,detail.ProjectJson);var tw=new MainWindow(true,false){Width=1320,Height=900};using var workspace=new ElectronicsReviewWorkspace(tw,detail);tw.Preview(workspace);tw.Show();tw.UpdateLayout();var copy=workspace.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await Run(copy,200);Capture(tw,"hcsr04-teacher-review.png");copy.Suspend();tw.Close();
        var stream=Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?assignmentId={assignment.Id}"));var history=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");Assert.Contains(history.Events,e=>e.Change.Kind=="control-change");Assert.Contains(history.Events,e=>e.Change.SerialIo?.Text.Contains("HC-SR04 cm=")==true);
        await f.Admin.Put<ElectronicsReviewDecision>($"teacher/reviews/electronics/{f.StudentId}/{report}/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest("accepted","Измерение проверено.",5,null,detail.AssignmentVersion));var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        var restored=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(json,restored.ProjectJson);Assert.Equal(5,restored.Item.Grade);Assert.Equal(Wire.Write(history),Wire.Write(await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes")));w.Close();
    }
}
