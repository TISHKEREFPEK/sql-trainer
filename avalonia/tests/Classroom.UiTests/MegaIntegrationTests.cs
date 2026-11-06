using System.Text.Json;
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

public sealed class MegaIntegrationTests
{
    static readonly System.Reflection.BindingFlags Private=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
    static CircuitProject Project()
    {
        var project=CircuitProject.Blink();project.Name="Mega · проверка выводов и Serial2";
        project.Parts[0]=new("mega",PartKind.Mega,80,80,ModelId:"mega2560");project.Parts[1]=project.Parts[1] with{X=760};project.Parts[2]=project.Parts[2] with{X=1000};
        project.Wires[0]=project.Wires[0] with{From=new("mega","D53")};project.Wires[2]=project.Wires[2] with{To=new("mega","GND")};
        project.Code="void setup(){pinMode(53,OUTPUT);digitalWrite(53,HIGH);Serial2.begin(115200);}\nvoid loop(){if(Serial2.available())Serial2.write(Serial2.read());}";
        return project;
    }
    static async Task Wait(Func<bool> condition,Func<string>? diagnostics=null)
    {for(var i=0;i<200&&!condition();i++)await Task.Delay(40);Assert.True(condition(),diagnostics?.Invoke());}
    static void Click(Control root,string text)=>root.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static ComboBox Ports(Control root)=>root.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Items.OfType<string>().SequenceEqual(new[]{"Serial","Serial1","Serial2","Serial3"}));
    static TextBox Input(Control root)=>root.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark=="Данные для платы");
    static TextBox Monitor(ElectronicsScreen root)=>(TextBox)typeof(ElectronicsScreen).GetField("monitor",Private)!.GetValue(root)!;
    static async Task Start(ElectronicsScreen view)
    {
        SelectMonitor(view);view.UpdateLayout();
        await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Private)!.Invoke(view,[true])!;
        await Wait(()=>view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Отправить").IsEnabled);
    }
    static async Task Send(ElectronicsScreen view,string text)
    {
        Input(view).Text=text;
        await Wait(()=>view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Отправить").IsEnabled);
        Click(view,"Отправить");
        await Wait(()=>Monitor(view).Text?.Contains(text)==true,()=>"Status: "+((TextBlock)typeof(ElectronicsScreen).GetField("status",Private)!.GetValue(view)!).Text+"; monitor: "+Monitor(view).Text);
    }
    static void SelectMonitor(ElectronicsScreen view)
    {view.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="ElectronicsCodeTabs").SelectedIndex=1;view.UpdateLayout();}
    static void Capture(Window window,string name)
    {
        window.UpdateLayout();using var frame=window.CaptureRenderedFrame();Assert.NotNull(frame);
        var directory=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(directory);frame!.Save(Path.Combine(directory,name));
    }

    [Fact]
    public void MegaPinsRoundTripAndElectricalModelKeepLegacyVisualCopiesIntact()
    {
        var project=Project();var json=project.Serialize();Assert.Equal(json,CircuitProject.Parse(json).Serialize());ElectronicsProjectFormat.Inspect(json);
        var pins=PartLibrary.Pins(project.Parts[0]);Assert.Equal(75,pins.Length);Assert.Equal(75,pins.Distinct().Count());Assert.Contains("D53",pins);Assert.Contains("A15",pins);
        Assert.Equal(75,pins.Select(pin=>PartArtwork.Pin(project.Parts[0],pin)).Distinct().Count());
        var outputs=Enumerable.Range(0,70).Select(_=>new PinDrive(false,0)).ToArray();outputs[53]=new(true,5);
        Assert.True(CircuitSolver.Solve(project,outputs).Parts["led"].Lit);outputs[53]=new(true,0);Assert.False(CircuitSolver.Solve(project,outputs).Parts["led"].Lit);
        var guide=CircuitCodeGuide.Build(project);Assert.NotNull(guide.Sketch);Assert.Contains("Arduino Mega 2560",guide.Sketch);Assert.Contains("= 53;",guide.Sketch);
        var legacy=PartCatalog.All.Single(d=>d.ReferenceName=="Arduino Mega 2560");Assert.Equal(PartKind.Visual,legacy.Kind);Assert.False(legacy.Simulated);
        var old=new CircuitProject();old.Parts.Add(new("old",PartKind.Visual,5,10,ModelId:legacy.Id));var before=old.Serialize();Assert.Equal(new[]{"P1","P2","P3","P4"},PartLibrary.Pins(old.Parts[0]));Assert.Equal(before,CircuitProject.Parse(before).Serialize());
        var journal=ElectronicsJournal.Start(json,1);journal=ElectronicsJournal.Record(journal,json,json,"serial-input",2,force:true,serialIo:new(3,"порт 3"));Assert.Equal(json,Wire.Read<ElectronicsJournal>(Wire.Write(journal)).Replay());
        Assert.Throws<InvalidDataException>(()=>(journal with{Changes=[journal.Changes[0] with{SerialIo=new(4,"подмена") }]}).Replay());
        var uno=CircuitProject.Blink().Serialize();Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(ElectronicsJournal.Start(uno,1),uno,uno,"serial-input",2,force:true,serialIo:new(1,"не тот порт")));
        project.Parts.Add(new("other",PartKind.Uno,0,0));Assert.Throws<InvalidDataException>(project.Validate);
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task NativeMegaRunsActualWiringAndIndependentSerialChannels(int width)
    {
        var directory=Path.Combine(Path.GetTempPath(),"classroom-mega-editor-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var path=Path.Combine(directory,"project.json");File.WriteAllText(path,Project().Serialize());
        var window=new MainWindow(true,false){Width=width,Height=900};using var view=new ElectronicsScreen(window,path,teacher:true);window.Preview(view);window.Show();window.UpdateLayout();
        Assert.Contains(view.GetVisualDescendants().OfType<TabItem>(),t=>t.Header?.ToString()=="Код Arduino Mega 2560");
        Capture(window,"mega-editor-code-"+width+".png");
        await Start(view);await Wait(()=>((CircuitResult?)typeof(ElectronicsScreen).GetField("result",Private)!.GetValue(view))?.Parts["led"].Lit==true);
        SelectMonitor(view);window.UpdateLayout();Ports(view).SelectedIndex=2;await Send(view,"проверка платы");
        Ports(view).SelectedIndex=1;Assert.Equal("",Monitor(view).Text);Ports(view).SelectedIndex=2;Assert.Contains("проверка платы",Monitor(view).Text);
        Capture(window,"mega-editor-serial-"+width+".png");Click(view,"Очистить экран");Assert.Equal("",Monitor(view).Text);view.Suspend();Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Отправить").IsEnabled);
        Assert.Equal(Project().Serialize(),File.ReadAllText(path));window.Close();
    }

    [AvaloniaFact]
    public async Task AssignedMegaSerialHistoryAndFrozenTeacherCopySurviveBackup()
    {
        await using var f=await LiveClassroom.Start();var project=Project();var original=project.Serialize();var task=Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+task,new ElectronicsTaskRequest("Mega","Проверьте D53 и обмен по Serial2.",original));
        var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([task]));
        var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+task);var directory=Path.Combine(Path.GetTempPath(),"classroom-mega-assigned-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);var path=Path.Combine(directory,"project.json");
        var window=new MainWindow(false,false){Width=1000,Height=900};using var view=new ElectronicsScreen(window,path,f.Student,assignment:assignment);window.Preview(view);window.Show();window.UpdateLayout();await Start(view);SelectMonitor(view);window.UpdateLayout();Ports(view).SelectedIndex=2;await Send(view,"ввод ученика");Click(view,"Очистить экран");view.Suspend();Assert.True(await view.Synchronize());
        var streams=await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?assignmentId={assignment.Id}");var stream=Assert.Single(streams);
        var events=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");
        Assert.Contains(events.Events,e=>e.Change.Kind=="serial-input-request"&&e.Change.SerialIo==new ElectronicsSerialIo(2,"ввод ученика\n"));Assert.Contains(events.Events,e=>e.Change.Kind=="serial-input");
        Assert.Equal("ввод ученика\n",string.Concat(events.Events.Where(e=>e.Change.Kind=="serial-output").Select(e=>e.Change.SerialIo!.Text)));Assert.Contains(events.Events,e=>e.Change.Kind=="serial-clear");
        Assert.DoesNotContain("serialIo",Wire.Write(events.Events.First(e=>e.Change.SerialIo is null).Change));
        var sync=(ElectronicsSync)typeof(ElectronicsScreen).GetField("sync",Private)!.GetValue(view)!;var report=Guid.NewGuid().ToString("N");sync.Record("submitted",original,expectedAssignmentVersion:assignment.StateVersion,reportId:report);Assert.True(await view.Synchronize());
        var detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(original,detail.ProjectJson);
        view.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="ElectronicsCodeTabs").SelectedIndex=0;view.UpdateLayout();
        view.GetVisualDescendants().OfType<TextEditor>().Single().Text+="\n// новые правки ученика";Assert.True(await view.Synchronize());
        var copyWindow=new MainWindow(true,false){Width=1000,Height=900};using var copy=new ElectronicsReviewWorkspace(copyWindow,detail);copyWindow.Preview(copy);copyWindow.Show();copyWindow.UpdateLayout();var copied=copy.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await Start(copied);SelectMonitor(copied);copyWindow.UpdateLayout();Ports(copied).SelectedIndex=2;await Send(copied,"проверка учителя");copied.Suspend();copyWindow.Close();
        Assert.Equal(original,(await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}")).ProjectJson);
        Assert.Equal(events.Events.Count(e=>e.Change.Kind=="serial-output"),(await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes")).Events.Count(e=>e.Change.Kind=="serial-output"));
        var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));
        var restored=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");Assert.Contains(restored.Events,e=>e.Change.SerialIo==new ElectronicsSerialIo(2,"ввод ученика\n"));window.Close();
    }
}
