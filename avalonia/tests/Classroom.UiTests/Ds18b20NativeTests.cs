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

public sealed class Ds18b20NativeTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static string Output(ElectronicsScreen v)=>string.Join("",(string[])typeof(ElectronicsScreen).GetField("serialText",Flags)!.GetValue(v)!);
    static async Task Advance(ElectronicsScreen v,string expected)
    {
        for(var i=0;i<300&&!Output(v).Contains(expected);i++)await (Task)typeof(ElectronicsScreen).GetMethod("Tick",Flags)!.Invoke(v,null)!;
        Assert.Contains(expected,Output(v));
    }
    static async Task Run(ElectronicsScreen v,string expected="C=25.0000")
    {
        await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Flags)!.Invoke(v,[true])!;((DispatcherTimer)typeof(ElectronicsScreen).GetField("timer",Flags)!.GetValue(v)!).Stop();
        var status=(TextBlock)typeof(ElectronicsScreen).GetField("status",Flags)!.GetValue(v)!;Assert.True((bool)typeof(ElectronicsScreen).GetField("running",Flags)!.GetValue(v)!,status.Text);await Advance(v,expected);
    }
    static CircuitProject Project(string board)
    {
        var p=Ds18b20Tests.Project(board,two:false);p.Parts[1]=p.Parts[1] with{Value=25};var guide=CircuitCodeGuide.Build(p);Assert.NotNull(guide.Sketch);
        p.Code=guide.Sketch.Replace("thermometers0.begin();","thermometers0.begin(); thermometers0.setResolution(9);");return p;
    }
    static Panel Inspect(ElectronicsScreen v){typeof(ElectronicsScreen).GetField("selected",Flags)!.SetValue(v,"cold");typeof(ElectronicsScreen).GetMethod("Inspect",Flags)!.Invoke(v,null);return (Panel)typeof(ElectronicsScreen).GetField("properties",Flags)!.GetValue(v)!;}
    static void Capture(Window w,string name){w.UpdateLayout();using var image=w.CaptureRenderedFrame();Assert.NotNull(image);var folder=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.GetTempPath();Directory.CreateDirectory(folder);image.Save(Path.Combine(folder,name));}
    [AvaloniaTheory]
    [InlineData("uno",900)]
    [InlineData("pico",1320)]
    public async Task EditorConvertsChangedTemperatureAndSavesFirmwareEeprom(string board,int width)
    {
        var p=Project(board);var folder=Path.Combine(Path.GetTempPath(),"classroom-ds18-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var file=Path.Combine(folder,"work.json");File.WriteAllText(file,p.Serialize());
        var w=new MainWindow(true,false){Width=width,Height=900};using var v=new ElectronicsScreen(w,file,teacher:true);w.Preview(v);w.Show();w.UpdateLayout();await Run(v);var properties=Inspect(v);Assert.Single(properties.GetVisualDescendants().OfType<Slider>()).Value=31.5;await Advance(v,"C=31.5000");
        var reading=(TextBlock)typeof(ElectronicsScreen).GetField("ds18Reading",Flags)!.GetValue(v)!;Assert.Contains("9 бит",reading.Text);Assert.True(reading.Text!.Contains("31,5")||reading.Text.Contains("31.5"));
        v.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Вписать").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Capture(w,$"ds18b20-{board}-{width}.png");v.Suspend();var saved=CircuitProject.Parse(File.ReadAllText(file));Assert.Equal(p.Code,saved.Code);Assert.Equal(31.5,saved.Parts[1].Value);Assert.Equal(31,saved.Parts[1].DeviceMemory![2]);
        var svg=(string)typeof(ElectronicsScreen).GetMethod("Svg",Flags)!.Invoke(v,null)!;System.Xml.Linq.XDocument.Parse(svg);Assert.Contains("DS18B20",svg);w.Close();
        saved.Code="""
            #include <OneWire.h>
            #include <DallasTemperature.h>
            OneWire w(6);DallasTemperature s(&w);
            void setup(){Serial.begin(115200);s.begin();DeviceAddress address;Serial.print("N=");Serial.print(s.getDeviceCount());Serial.print(";restored=");Serial.println(s.getAddress(address,0)?s.getResolution(address):0);}
            void loop(){}
            """;
        if(board=="pico"){saved.Code=saved.Code.Replace("Serial.","Serial1.");var image=await PicoCompiler.Compile(saved.Code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);var output="";for(var i=0;i<80&&!output.Contains("N=1;restored=9");i++)output+=(await runner.Step(milliseconds:50,ds18b20Devices:CircuitDs18b20.Devices(saved))).SerialPorts[1];Assert.Contains("N=1;restored=9",output);}
        else{var b=AvrBoards.Get(board);var image=await FirmwareRunner.Compile(saved.Code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(image,board);var output="";for(var i=0;i<50&&!output.Contains("N=1;restored=9");i++)output+=(await runner.Step(new bool[b.DigitalCount],new double[b.AnalogCount],ds18b20Devices:CircuitDs18b20.Devices(saved))).Serial;Assert.Contains("N=1;restored=9",output);}
    }
    [AvaloniaFact]
    public async Task EepromAndControlsReachTeacherAndImmutableSubmissionSurvivesBackup()
    {
        await using var f=await LiveClassroom.Start();var p=Project("mega2560");var json=p.Serialize();var task=Guid.NewGuid().ToString("N");await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+task,new ElectronicsTaskRequest("DS18B20","Прочитайте температуру и запишите точность в EEPROM.",json));var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([task]));var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+task);
        var folder=Path.Combine(Path.GetTempPath(),"classroom-ds18-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var file=Path.Combine(folder,"work.json");var w=new MainWindow(false,false){Width=1320,Height=900};using var v=new ElectronicsScreen(w,file,f.Student,assignment:assignment);w.Preview(v);w.Show();w.UpdateLayout();await Run(v);Assert.Single(Inspect(v).GetVisualDescendants().OfType<Slider>()).Value=31.5;await Advance(v,"C=31.5000");v.Suspend();json=File.ReadAllText(file);Assert.Equal(31,CircuitProject.Parse(json).Parts[1].DeviceMemory![2]);Assert.True(await v.Synchronize());
        var local=(ElectronicsJournal)typeof(ElectronicsScreen).GetProperty("LocalHistory",Flags)!.GetValue(v)!;Assert.Equal(json,local.Replay());Assert.Contains(local.Changes,c=>c.Kind=="device-memory-write");
        var sync=(ElectronicsSync)typeof(ElectronicsScreen).GetField("sync",Flags)!.GetValue(v)!;var report=Guid.NewGuid().ToString("N");sync.Record("submitted",json,expectedAssignmentVersion:assignment.StateVersion,reportId:report);Assert.True(await v.Synchronize());var detail=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(json,detail.ProjectJson);
        var tw=new MainWindow(true,false){Width=1320,Height=900};using var workspace=new ElectronicsReviewWorkspace(tw,detail);tw.Preview(workspace);tw.Show();tw.UpdateLayout();var copy=workspace.GetVisualDescendants().OfType<ElectronicsScreen>().Single();await Run(copy,"C=31.5000");Capture(tw,"ds18b20-teacher-review.png");copy.Suspend();tw.Close();
        var stream=Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?assignmentId={assignment.Id}"));var history=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");Assert.Contains(history.Events,e=>e.Change.Kind=="device-memory-write");Assert.Contains(history.Events,e=>e.Change.Kind=="control-change");Assert.Contains(history.Events,e=>e.Change.SerialIo?.Text.Contains("C=31.5000")==true);
        await f.Admin.Put<ElectronicsReviewDecision>($"teacher/reviews/electronics/{f.StudentId}/{report}/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest("accepted","Температура и EEPROM проверены.",5,null,detail.AssignmentVersion));var backup=await f.Admin.Post<JsonElement>("teacher/backups");await f.Admin.Post<JsonElement>("teacher/backups/"+Uri.EscapeDataString(backup.GetProperty("name").GetString()!)+"/restore");await f.Admin.Login(new("teacher","","synthetic-teacher-password"));var restored=await f.Admin.Get<ElectronicsReviewDetail>($"teacher/reviews/electronics/{f.StudentId}/{report}");Assert.Equal(json,restored.ProjectJson);Assert.Equal(5,restored.Item.Grade);Assert.Equal(Wire.Write(history),Wire.Write(await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes")));w.Close();
    }
}
