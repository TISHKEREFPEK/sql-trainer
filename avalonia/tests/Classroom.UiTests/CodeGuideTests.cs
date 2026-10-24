using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;
using Path = System.IO.Path;
namespace Classroom.UiTests;
public sealed class CodeGuideTests
{
    [Fact]
    public async Task TemplatesUseActualWiresAndCompileWithLocalLibraries()
    {
        var empty = new CircuitProject(); Assert.Contains("void setup()",empty.Code); Assert.DoesNotContain("pinMode",empty.Code);
        Assert.StartsWith(":",await FirmwareRunner.Compile(empty.Code,CancellationToken.None));
        Assert.Contains("digitalWrite(13",CircuitProject.Blink().Code); Assert.Equal(CircuitProject.Blink().Code,CircuitExamples.Breadboard().Code);
        foreach(var project in new[]{CircuitProject.Blink(),CircuitExamples.Breadboard(),CircuitExamples.Analog(),CircuitExamples.Servo()})
        {
            var guide=CircuitCodeGuide.Build(project); Assert.NotNull(guide.Sketch);
            Assert.StartsWith(":",await FirmwareRunner.Compile(guide.Sketch!,CancellationToken.None));
            Assert.Contains("void setup()",guide.Sketch); Assert.Contains("void loop()",guide.Sketch);
        }
        var servo=CircuitExamples.Servo();
        var signal=servo.Wires.FindIndex(w=>w.From.Pin=="D9"||w.To.Pin=="D9"); Assert.True(signal>=0);
        var wire=servo.Wires[signal]; servo.Wires[signal]=wire.From.Pin=="D9"?wire with {From=wire.From with {Pin="D6"}}:wire with {To=wire.To with {Pin="D6"}};
        var changed=CircuitCodeGuide.Build(servo); Assert.Contains(".attach(6);",changed.Sketch!); Assert.Contains("#include <Servo.h>",changed.Sketch!);
        Assert.StartsWith(":",await FirmwareRunner.Compile(changed.Sketch!,CancellationToken.None));
    }
    [Fact]
    public void MissingAmbiguousAndUnsupportedConnectionsDoNotProduceMisleadingTemplates()
    {
        var project=CircuitProject.Blink(); project.Wires.RemoveAt(0); Assert.Null(CircuitCodeGuide.Build(project).Sketch);
        project=CircuitProject.Blink(); project.Wires.Add(new("conflict",new("uno","D12"),new("r","1"))); Assert.Null(CircuitCodeGuide.Build(project).Sketch);
        var pico=PartCatalog.All.Single(d=>d.ReferenceName=="Raspberry Pi Pico"); project.Parts.Add(new("pico",PartKind.Visual,100,100,ModelId:pico.Id));
        var guide=CircuitCodeGuide.Build(project); Assert.Null(guide.Sketch); Assert.Contains("Только для схемы",guide.Notes);
        project=new(); Assert.Null(CircuitCodeGuide.Build(project).Sketch);
    }
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public void CodePageShowsComponentInstructionsWithoutRewritingTheProgram(int width)
    {
        var project=CircuitExamples.Servo(); project.Code="// мой код\nvoid setup(){} void loop(){}";
        var dir=Path.Combine(Path.GetTempPath(),"classroom-code-guide-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var file=Path.Combine(dir,"project.json"); File.WriteAllText(file,project.Serialize());
        var window=new MainWindow(true,false){Width=width,Height=900}; using var view=new ElectronicsScreen(window,file); window.Preview(view); window.Show(); window.UpdateLayout();
        view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex=1; window.UpdateLayout();
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("Servo.h")==true);
        Assert.True(view.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Создать шаблон по схеме").IsEnabled);
        view.Suspend(); Assert.Equal(project.Code,CircuitProject.Parse(File.ReadAllText(file)).Code);
        using var frame=window.CaptureRenderedFrame(); var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots"); Directory.CreateDirectory(output); frame!.Save(Path.Combine(output,$"code-guide-{width}.png")); window.Close();
    }
}

