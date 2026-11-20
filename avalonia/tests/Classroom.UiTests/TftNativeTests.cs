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

public sealed class TftNativeTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    public static CircuitProject Project(string board,int bus=0)
    {
        var pico=board=="pico";var mega=board=="mega2560";
        var cs=pico?(bus==0?17:9):mega?53:10;var dc=pico?(bus==0?20:12):mega?49:9;var rst=pico?(bus==0?21:13):mega?48:8;
        var mosi=pico?(bus==0?19:11):mega?51:11;var sck=pico?(bus==0?18:10):mega?52:13;var miso=pico?(bus==0?16:8):mega?50:12;
        string Pin(int n)=>pico?PicoBoard.Gpio(n)!:"D"+n;
        var project=new CircuitProject{Name="TFT · "+board+" · SPI"+bus};
        project.Parts.AddRange([new("board",pico?PartKind.Pico:mega?PartKind.Mega:PartKind.Uno,80,80,ModelId:pico?PicoBoard.ModelId:board),new("tft",PartKind.TftSpi,mega?820:540,80,ModelId:CircuitSpi.ModelId)]);
        var wires=new[]{("VCC",pico?PicoBoard.V3:"5V"),("GND",pico?PicoBoard.Ground:"GND"),("CS",Pin(cs)),("RST",Pin(rst)),("D/C",Pin(dc)),("MOSI",Pin(mosi)),("SCK",Pin(sck)),("LED",pico?PicoBoard.V3:"5V"),("MISO",Pin(miso))};
        for(var i=0;i<wires.Length;i++)project.Wires.Add(new("w"+i,new("board",wires[i].Item2),new("tft",wires[i].Item1)));
        return project;
    }
    public static bool Red(TftDisplayFrame frame){var rgb=Convert.FromBase64String(frame.Pixels);return frame.Display&&rgb.AsSpan((8*240+8)*3,3).SequenceEqual(new byte[]{255,0,0});}
    static Dictionary<string,TftDisplayFrame> Displays(ElectronicsScreen view)=>(Dictionary<string,TftDisplayFrame>)typeof(ElectronicsScreen).GetField("tftDisplays",Flags)!.GetValue(view)!;
    static string Status(ElectronicsScreen view)=>((TextBlock)typeof(ElectronicsScreen).GetField("status",Flags)!.GetValue(view)!).Text??"";
    static async Task Run(ElectronicsScreen view)
    {
        await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Flags)!.Invoke(view,[true])!;
        ((DispatcherTimer)typeof(ElectronicsScreen).GetField("timer",Flags)!.GetValue(view)!).Stop();
        Assert.True((bool)typeof(ElectronicsScreen).GetField("running",Flags)!.GetValue(view)!,Status(view));
        for(var i=0;i<220&&!Displays(view).Values.Any(Red);i++)await (Task)typeof(ElectronicsScreen).GetMethod("Tick",Flags)!.Invoke(view,null)!;
        Assert.True(Displays(view).Values.Any(Red),Status(view));
    }
    static void Capture(Window window,string name)
    {
        window.UpdateLayout();using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);
        var path=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(path);frame!.Save(Path.Combine(path,name));
    }
    [Theory]
    [InlineData("uno",0)]
    [InlineData("mega2560",0)]
    [InlineData("pico",0)]
    [InlineData("pico",1)]
    public void PhysicalBindingsAndLegacyProjectsStayDistinct(string board,int bus)
    {
        var project=Project(board,bus);var json=project.Serialize();Assert.Equal(json,CircuitProject.Parse(json).Serialize());ElectronicsProjectFormat.Inspect(json);
        var device=Assert.Single(CircuitSpi.Devices(project));Assert.True(device.Connected&&device.Powered);Assert.Equal(bus,device.Controller);Assert.True(CircuitSpi.Hardware(project.Parts[0],device));
        var guide=CircuitCodeGuide.Build(project);Assert.NotNull(guide.Sketch);Assert.Contains("Adafruit_ILI9341",guide.Sketch);if(bus==1)Assert.Contains("&SPI1",guide.Sketch);
        Assert.Equal(json,project.Serialize());var journal=ElectronicsJournal.Start(json,1);var changed=CircuitProject.Parse(json);changed.Parts[1]=changed.Parts[1] with{QuarterTurns=1,Scale=1.2};
        journal=ElectronicsJournal.Record(journal,json,changed.Serialize(),"part-layout",2);Assert.Equal(changed.Serialize(),Wire.Read<ElectronicsJournal>(Wire.Write(journal)).Replay());
        var legacy=PartCatalog.All.Single(p=>p.ReferenceName=="ILI9341");Assert.Equal(PartKind.Visual,legacy.Kind);Assert.False(legacy.Simulated);
        var old=new CircuitProject();old.Parts.Add(new("old",PartKind.Visual,0,0,ModelId:legacy.Id));var oldJson=old.Serialize();Assert.Equal(oldJson,CircuitProject.Parse(oldJson).Serialize());
        project.Parts.Add(new("bread",PartKind.Breadboard,0,0));var clock=project.Wires.Single(w=>w.To.Pin=="SCK");project.Wires.Remove(clock);
        project.Wires.Add(clock with{To=new("bread","a1")});project.Wires.Add(new("strip",new("bread","e1"),new("tft","SCK")));Assert.True(Assert.Single(CircuitSpi.Devices(project)).Connected);
        project.Wires.RemoveAt(project.Wires.Count-1);var absent=Assert.Single(CircuitSpi.Devices(project));Assert.False(absent.Connected);Assert.NotEqual(absent.Mosi,absent.Sck);Assert.Null(CircuitCodeGuide.Build(project).Sketch);
        project.Wires.Add(new("short",new("tft","MOSI"),new("tft","SCK")));Assert.False(Assert.Single(CircuitSpi.Devices(project)).Connected);
        var noPower=CircuitProject.Parse(json);noPower.Wires.RemoveAll(w=>w.To.Pin=="GND");Assert.False(Assert.Single(CircuitSpi.Devices(noPower)).Powered);
        var noMiso=CircuitProject.Parse(json);noMiso.Wires.RemoveAll(w=>w.To.Pin=="MISO");Assert.Null(Assert.Single(CircuitSpi.Devices(noMiso)).Miso);Assert.NotNull(CircuitCodeGuide.Build(noMiso).Sketch);
        var sourceShort=CircuitProject.Parse(json);sourceShort.Parts.Add(new("source",PartKind.Battery,0,0,5));sourceShort.Wires.Add(new("source-short",new("source","+"),new("tft","MOSI")));Assert.False(Assert.Single(CircuitSpi.Devices(sourceShort)).Connected);
        var controlShort=CircuitProject.Parse(json);controlShort.Wires.Add(new("control-short",new("tft","CS"),new("tft","D/C")));Assert.False(Assert.Single(CircuitSpi.Devices(controlShort)).Connected);
        if(board=="pico"){
            var result=CircuitSolver.Solve(Project(board,bus));var inputs=CircuitPico.Inputs(Project(board,bus),result,[],[device]);Assert.Null(inputs.Digital[device.Mosi]);Assert.Null(inputs.Digital[device.Sck]);
            var over=CircuitProject.Parse(json);var supply=over.Wires.Single(w=>w.To.Pin=="VCC");over.Wires.Add(new("over",new("board",PicoBoard.Vbus),new("tft","MOSI")));over.Wires[over.Wires.IndexOf(supply)]=supply with{From=new("board",PicoBoard.Vbus)};
            var dc=CircuitSolver.Solve(over);Assert.Throws<InvalidDataException>(()=>CircuitPico.Inputs(over,dc,[],CircuitSpi.Devices(over,dc)));
        }
    }
    [AvaloniaTheory]
    [InlineData("uno",0,900)]
    [InlineData("mega2560",0,1320)]
    [InlineData("pico",0,1320)]
    [InlineData("pico",1,900)]
    [InlineData("pico",-1,900)]
    public async Task GeneratedSketchRunsThroughNativeEditorAndStopsWithoutChangingTheProject(string board,int bus,int width)
    {
        var project=bus<0?SoftwareSpiTests.Project(board):Project(board,bus);var guide=CircuitCodeGuide.Build(project);Assert.NotNull(guide.Sketch);project.Code=guide.Sketch;
        var directory=Path.Combine(Path.GetTempPath(),"classroom-tft-native-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var file=Path.Combine(directory,"work.json");File.WriteAllText(file,project.Serialize());
        var window=new MainWindow(true,false){Width=width,Height=900};using var view=new ElectronicsScreen(window,file,teacher:true);window.Preview(view);window.Show();window.UpdateLayout();await Run(view);
        var scene=(Canvas)typeof(ElectronicsScreen).GetField("canvas",Flags)!.GetValue(view)!;Assert.Single(scene.GetVisualDescendants().OfType<TftDisplayFace>());Assert.DoesNotContain("Unimplemented",Status(view));
        view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Вписать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        var viewport=view.GetVisualDescendants().OfType<CircuitViewport>().Single();foreach(var part in project.Parts)foreach(var point in PartArtwork.Corners(part))Assert.InRange(point.Y*viewport.Zoom+viewport.Pan.Y,56,viewport.Bounds.Height-60);
        Capture(window,$"tft-{board}-{(bus<0?"gpio":"spi"+bus)}-{width}.png");var svg=(string)typeof(ElectronicsScreen).GetMethod("Svg",Flags)!.Invoke(view,null)!;System.Xml.Linq.XDocument.Parse(svg);Assert.Contains("#17639a",svg);
        view.Suspend();Assert.Empty(Displays(view));Assert.Equal(project.Serialize(),File.ReadAllText(file));window.Close();
    }
    [AvaloniaFact]
    public Task SubmittedSpi1WorkReplaysInTeacherWorkspaceAndSurvivesBackup()=>AssignedReview(false);
    [AvaloniaFact]
    public Task SubmittedGpioWorkReplaysInTeacherWorkspaceAndSurvivesBackup()=>AssignedReview(true);
    async Task AssignedReview(bool software)
    {
        await using var f=await LiveClassroom.Start();var project=software?SoftwareSpiTests.Project("pico"):Project("pico",1);project.Code=CircuitCodeGuide.Build(project).Sketch!;var json=project.Serialize();var task=Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+task,new ElectronicsTaskRequest(software?"TFT GPIO":"TFT SPI1","Подключите дисплей и нарисуйте фигуры.",json));var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([task]));var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+task);
        var directory=Path.Combine(Path.GetTempPath(),"classroom-tft-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var file=Path.Combine(directory,"student.json");
        var window=new MainWindow(false,false){Width=1320,Height=900};using var view=new ElectronicsScreen(window,file,f.Student,assignment:assignment);window.Preview(view);window.Show();window.UpdateLayout();await Run(view);view.Suspend();Assert.True(await view.Synchronize());
        var sync=(ElectronicsSync)typeof(ElectronicsScreen).GetField("sync",Flags)!.GetValue(view)!;var report=Guid.NewGuid().ToString("N");sync.Record("submitted",json,expectedAssignmentVersion:assignment.StateVersion,reportId:report);Assert.True(await view.Synchronize());
        var stream=Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?assignmentId={assignment.Id}"));var history=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");Assert.Contains(history.Events,e=>e.Change.Kind=="run-request");
        var trace=history.Events.SelectMany(e=>e.Change.SpiIo??[]).ToArray();Assert.Contains(trace,t=>t.Software==software&&(software||t.Controller==1)&&t.Commands>0);Assert.Contains(trace,t=>t.Bytes>0&&t.DataBytes>0);Assert.All(trace,t=>Assert.InRange(t.TxPrefix.Length,0,32));
        var detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(json,detail.ProjectJson);
        var teacher=new MainWindow(true,false){Width=1320,Height=900};using var review=new ElectronicsReviewWorkspace(teacher,detail);teacher.Preview(review);teacher.Show();teacher.UpdateLayout();var copy=review.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await Run(copy);Capture(teacher,software?"tft-teacher-gpio-review.png":"tft-teacher-spi1-review.png");copy.Suspend();teacher.Close();
        var historyWindow=new MainWindow(true,false){Width=1320,Height=900};using(var journalView=new ElectronicsJournalView(historyWindow,f.Admin,f.StudentId,"Тестовый ученик",assignmentId:assignment.Id)){
            historyWindow.Preview(journalView);historyWindow.Show();await journalView.Load();historyWindow.UpdateLayout();var steps=journalView.GetVisualDescendants().OfType<ListBox>().Single();steps.SelectedItem=steps.Items.Cast<object>().Last(s=>s.ToString()!.Contains("Обмен SPI"));
            var metadata=(TextBox)typeof(ElectronicsJournalView).GetField("serialDetail",Flags)!.GetValue(journalView)!;
            for(var i=0;i<150&&metadata.Text?.Contains("CRC32")!=true;i++)await Task.Delay(25);Assert.True(metadata.IsVisible);Assert.Contains("не полный поток пикселей",metadata.Text);if(software)Assert.Contains("программный SPI",metadata.Text);Capture(historyWindow,software?"tft-gpio-history-1320.png":"tft-spi-history-1320.png");historyWindow.Close();
        }
        await f.Admin.Put<ElectronicsReviewDecision>($"teacher/reviews/electronics/{f.StudentId}/{report}/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest("accepted","Цвета и SPI1 проверены.",5,null,detail.AssignmentVersion));
        view.IsEnabled=false;view.Suspend();Assert.True(await view.Synchronize());
        var gradedHistory=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");Assert.Equal(Wire.Write(history.Events),Wire.Write(gradedHistory.Events.Take(history.Events.Length).ToArray()));
        var graded=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(5,Assert.Single(graded.History).Grade);
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        var restored=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(json,restored.ProjectJson);Assert.Equal(5,restored.Item.Grade);Assert.Equal(Wire.Write(gradedHistory),Wire.Write(await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes")));Assert.Equal(json,File.ReadAllText(file));window.Close();
    }
}
