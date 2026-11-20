using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoNativeTests
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    public static CircuitProject Project()
    {
        var project=new CircuitProject{Name="Pico · LED, ADC и OLED"};
        project.Parts.AddRange([new("pico",PartKind.Pico,100,80,ModelId:PicoBoard.ModelId),new("r",PartKind.Resistor,460,100,330),new("led",PartKind.Led,700,100,2),new("pot",PartKind.Pot3,470,400,10000),new("oled",PartKind.OledI2c,800,320,60,ModelId:"oled-ssd1306-i2c")]);
        var wires=new[]{("pico","4:GP2","r","1"),("r","2","led","A"),("led","K","pico",PicoBoard.Ground),("pico",PicoBoard.V3,"pot","1"),("pot","3","pico","33:AGND"),("pot","W","pico","31:GP26/ADC0"),("pico",PicoBoard.V3,"oled","VDD"),("pico","38:GND","oled","GND"),("pico","6:GP4","oled","SDA"),("pico","7:GP5","oled","SCL")};
        for(var i=0;i<wires.Length;i++){var w=wires[i];project.Wires.Add(new("w"+i,new(w.Item1,w.Item2),new(w.Item3,w.Item4)));}
        return project;
    }
    [Fact]
    public void PhysicalHeadersVoltagesLegacyModelsAndInvalidPowerRemainExplicit()
    {
        var project=Project();var json=project.Serialize();Assert.Equal(json,CircuitProject.Parse(json).Serialize());ElectronicsProjectFormat.Inspect(json);
        Assert.Equal(40,PicoBoard.Pins.Distinct().Count());Assert.Null(PicoBoard.Gpio(25));Assert.Null(PicoBoard.Gpio(29));Assert.Equal("31:GP26/ADC0",PicoBoard.Gpio(26));
        var dc=CircuitSolver.Solve(project);Assert.InRange(dc.Voltages[new("pico",PicoBoard.V3)],3.28,3.31);Assert.InRange(dc.Voltages[new("pot","W")],1.63,1.66);
        Assert.Equal(0,dc.Voltages[new("pico","33:AGND")]);var devices=CircuitPico.Devices(project,dc);var oled=Assert.Single(devices);Assert.True(oled.Powered&&oled.Connected);Assert.Equal((0,4,5),(oled.Controller,oled.Sda,oled.Scl));
        var inputs=CircuitPico.Inputs(project,dc,devices);Assert.Null(inputs.Digital[4]);Assert.Null(inputs.Digital[5]);Assert.InRange(inputs.Analog[0],1.63,1.66);
        foreach(var shorted in new[]{false,true}){var broken=CircuitProject.Parse(json);var clock=broken.Wires.Single(w=>w.To==new Terminal("oled","SCL"));if(shorted)broken.Wires[broken.Wires.IndexOf(clock)]=clock with{From=new("pico","6:GP4")};else{broken.Wires.Remove(clock);var data=broken.Wires.Single(w=>w.To==new Terminal("oled","SDA"));broken.Wires[broken.Wires.IndexOf(data)]=data with{From=new("pico","7:GP5")};}var absent=Assert.Single(CircuitPico.Devices(broken));Assert.False(absent.Connected);Assert.NotEqual(absent.Sda,absent.Scl);}
        var outputs=Enumerable.Range(0,30).Select(_=>new PinDrive(false,0)).ToArray();outputs[2]=new(true,3.3);
        Assert.True(CircuitSolver.Solve(project,outputs).Parts["led"].Lit);outputs[2]=new(true,0);Assert.False(CircuitSolver.Solve(project,outputs).Parts["led"].Lit);
        var legacy=PartCatalog.All.Single(p=>p.ReferenceName=="Raspberry Pi Pico");var old=new CircuitProject();old.Parts.Add(new("old",PartKind.Visual,0,0,ModelId:legacy.Id,CustomPins:legacy.Pins));var before=old.Serialize();Assert.Equal(before,CircuitProject.Parse(before).Serialize());Assert.False(PartCatalog.CanSimulate(old.Parts[0]));
        project.Parts.Add(new("uno",PartKind.Uno,0,0));Assert.Throws<InvalidDataException>(()=>project.Serialize());project.Parts.RemoveAt(project.Parts.Count-1);
        project.Wires.Add(new("run",new("pico","30:RUN"),new("pico",PicoBoard.Ground)));Assert.Throws<InvalidDataException>(()=>PicoBoard.ValidateRun(project));project.Wires.RemoveAt(project.Wires.Count-1);
        project.Parts.Add(new("external",PartKind.Battery,0,0,3.3));project.Wires.Add(new("external-power",new("external","+"),new("pico",PicoBoard.V3)));Assert.Throws<InvalidDataException>(()=>PicoBoard.ValidateRun(project));project.Wires.RemoveAt(project.Wires.Count-1);project.Parts.RemoveAt(project.Parts.Count-1);
        var index=project.Wires.FindIndex(w=>w.To==new Terminal("oled","VDD"));project.Wires[index]=project.Wires[index] with{From=new("pico",PicoBoard.Vbus)};
        var over=CircuitSolver.Solve(project);Assert.Contains("3,3",Assert.Throws<InvalidDataException>(()=>CircuitPico.Inputs(project,over,CircuitPico.Devices(project,over))).Message);
    }
    [Fact]
    public void PicoJournalRetainsPortsAndPhysicalBusMetadataWithoutChangingOldPayloads()
    {
        Assert.Equal("{\"address\":39,\"read\":false,\"data\":[8],\"ack\":true,\"repeatedStart\":false}",Wire.Write(new ElectronicsI2cIo(39,false,[8],true,false)));
        var json=Project().Serialize();var journal=ElectronicsJournal.Start(json,1);
        journal=ElectronicsJournal.Record(journal,json,json,"serial-output",2,force:true,serialIo:new(2,"UART1"));
        journal=ElectronicsJournal.Record(journal,json,json,"i2c-transfer",3,force:true,i2cIo:[new(60,false,[0,0xaf],true,false,1,true,26,27)]);
        Assert.Equal(json,Wire.Read<ElectronicsJournal>(Wire.Write(journal)).Replay());
        Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(journal,json,json,"serial-input",4,force:true,serialIo:new(3,"bad")));
        Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(journal,json,json,"i2c-transfer",4,force:true,i2cIo:[new(60,false,[],true,false,2,true,4,5)]));
        Assert.Throws<InvalidDataException>(()=>ElectronicsJournal.Record(journal,json,json,"i2c-transfer",4,force:true,i2cIo:[new(60,false,[],true,false,0,true)]));
    }
    [Fact]
    public async Task AssignedPicoSubmissionHasItsOwnImmutableReviewAndGrade()
    {
        await using var f=await LiveClassroom.Start();var project=Project();project.Code=CircuitCodeGuide.Build(project).Sketch!;var json=project.Serialize();var id=Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/"+id,new ElectronicsTaskRequest("Pico и OLED","Соберите схему и объясните ADC.",json));
        var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks",new ElectronicsSelectionRequest([id]));
        var assignment=await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/"+id);var client=Guid.NewGuid().ToString("N");var report=Guid.NewGuid().ToString("N");
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,1,json,[new(report,"submitted",json,"",1)],assignment.Id));
        var queue=await f.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics");Assert.Contains(queue.Items,r=>r.ReportId==report&&r.Title=="Pico и OLED");
        var route=$"teacher/reviews/electronics/{f.StudentId}/{report}";var review=await f.Admin.Get<ElectronicsReviewDetail>(route);Assert.Equal(json,review.ProjectJson);CircuitProject.Parse(review.ProjectJson);
        await f.Admin.Put<ElectronicsReviewDecision>(route+"/decisions/"+Guid.NewGuid().ToString("N"),new ElectronicsReviewRequest("accepted","Верно, объяснение принято.",5,null,review.AssignmentVersion));
        project.Code+="\n// Следующая версия черновика\n";
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,2,project.Serialize(),[],assignment.Id));
        var graded=await f.Student.Get<ElectronicsReviewDetail>("electronics/reviews/"+report);Assert.Equal(5,graded.Item.Grade);Assert.Equal(json,graded.ProjectJson);Assert.Single(graded.History);
    }
    [AvaloniaFact]
    public async Task NativePicoRunsAndDeliversItsSnapshotAndHistoryToTeacher()
    {
        await using var f=await LiveClassroom.Start();var directory=Path.Combine(Path.GetTempPath(),"classroom-pico-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var project=Project();var guide=CircuitCodeGuide.Build(project);Assert.NotNull(guide.Sketch);
        project.Code=guide.Sketch!.Replace("delay(500)","delay(10)").Replace("Serial.begin(115200);","Serial.begin(115200);Serial1.begin(115200);Serial2.setTX(8);Serial2.setRX(9);Serial2.begin(115200);Serial1.println(\"UART0 ready\");Serial2.println(\"UART1 ready\");")
            .Replace("  delay(20);","  while(Serial.available())Serial.write(Serial.read());while(Serial1.available())Serial1.write(Serial1.read());while(Serial2.available())Serial2.write(Serial2.read());delay(20);");
        var file=Path.Combine(directory,"student.json");File.WriteAllText(file,project.Serialize());
        var owner=new MainWindow(false,false){Width=1320,Height=900};using var view=new ElectronicsScreen(owner,file,f.Student);owner.Preview(view);owner.Show();owner.UpdateLayout();
        static async Task Run(ElectronicsScreen editor){await (Task)typeof(ElectronicsScreen).GetMethod("Compile",Flags)!.Invoke(editor,[true])!;((DispatcherTimer)typeof(ElectronicsScreen).GetField("timer",Flags)!.GetValue(editor)!).Stop();Assert.True((bool)typeof(ElectronicsScreen).GetField("running",Flags)!.GetValue(editor)!);}
        static async Task Tick(ElectronicsScreen editor)=>await (Task)typeof(ElectronicsScreen).GetMethod("Tick",Flags)!.Invoke(editor,null)!;
        await Run(view);
        for(var i=0;i<60;i++){await Tick(view);if(view.LocalHistory!.Changes.Any(c=>c.Kind=="serial-output"&&c.SerialIo?.Port==0)&&((Dictionary<string,OledFrame>)typeof(ElectronicsScreen).GetField("oledDisplays",Flags)!.GetValue(view)!).Values.Any(o=>o.Display))break;}
        Assert.Contains(view.LocalHistory!.Changes,c=>c.SerialIo?.Port==1&&c.SerialIo.Text.Contains("UART0 ready"));Assert.Contains(view.LocalHistory.Changes,c=>c.SerialIo?.Port==2&&c.SerialIo.Text.Contains("UART1 ready"));Assert.Contains(view.LocalHistory.Changes,c=>c.SerialIo?.Port==0&&c.SerialIo.Text.Contains("204"));
        Assert.Contains(view.LocalHistory.Changes,c=>c.I2cIo?.Any(t=>t.Controller==0&&t.Address==60&&t.Ack)==true);
        for(var port=0;port<3;port++){
            ((ComboBox)typeof(ElectronicsScreen).GetField("serialChannel",Flags)!.GetValue(view)!).SelectedIndex=port;
            ((ComboBox)typeof(ElectronicsScreen).GetField("serialEnding",Flags)!.GetValue(view)!).SelectedIndex=0;
            var text=port==2?"Z":"E"+port+"Ж";
            ((TextBox)typeof(ElectronicsScreen).GetField("serialInput",Flags)!.GetValue(view)!).Text=text;
            typeof(ElectronicsScreen).GetMethod("QueueSerialInput",Flags)!.Invoke(view,null);
            string Received()=>string.Concat(view.LocalHistory.Changes.Where(c=>c.Kind=="serial-output"&&c.SerialIo?.Port==port).Select(c=>c.SerialIo!.Text));
            for(var i=0;i<40&&!Received().Contains(text);i++)await Tick(view);
            Assert.Contains(text,Received());Assert.Contains(view.LocalHistory.Changes,c=>c.Kind=="serial-input"&&c.SerialIo?.Port==port&&c.SerialIo.Text==text);
        }
        Assert.DoesNotContain("Unimplemented",((TextBlock)typeof(ElectronicsScreen).GetField("status",Flags)!.GetValue(view)!).Text??"");
        owner.UpdateLayout();using(var frame=owner.CaptureRenderedFrame()){Assert.NotNull(frame);var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??directory;Directory.CreateDirectory(output);frame!.Save(Path.Combine(output,"pico-native-1320.png"));}
        view.Suspend();Assert.Equal(project.Serialize(),File.ReadAllText(file));
        for(var i=0;i<40;i++){await view.Synchronize();var info=await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}");if(info.Reports.Any(r=>r.Kind=="started"))break;await Task.Delay(50);}
        var detail=await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}");var report=detail.Reports.Single(r=>r.Kind=="started");var snapshot=await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/report/{report.Id}");Assert.Equal(project.Serialize(),snapshot.ProjectJson);
        var teacherFile=Path.Combine(directory,"teacher-copy.json");File.WriteAllText(teacherFile,snapshot.ProjectJson);var teacher=new MainWindow(true,false){Width=900,Height=900};using var copy=new ElectronicsScreen(teacher,teacherFile,teacher:true);teacher.Preview(copy);teacher.Show();teacher.UpdateLayout();await Run(copy);
        for(var i=0;i<40;i++){await Tick(copy);if(((Dictionary<string,OledFrame>)typeof(ElectronicsScreen).GetField("oledDisplays",Flags)!.GetValue(copy)!).Values.Any(o=>o.Display))break;}
        Assert.True(Assert.Single(((Dictionary<string,OledFrame>)typeof(ElectronicsScreen).GetField("oledDisplays",Flags)!.GetValue(copy)!).Values).Display);copy.Suspend();Assert.Equal(project.Serialize(),File.ReadAllText(file));teacher.Close();owner.Close();
    }
}
