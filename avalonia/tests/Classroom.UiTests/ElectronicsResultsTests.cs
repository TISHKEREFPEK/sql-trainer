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
public sealed class ElectronicsResultsTests
{
    static async Task WaitFor(Func<Task<bool>> ready) { for(var i=0;i<80;i++){if(await ready())return;await Task.Delay(100);}Assert.True(await ready()); }
    static void Capture(MainWindow window,string file){window.UpdateLayout();using var frame=window.CaptureRenderedFrame();var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots");Directory.CreateDirectory(output);frame!.Save(Path.Combine(output,file));}
    [AvaloniaFact]
    public async Task StudentResultsReachTeacherAndViewerDoesNotEditTeacherProject()
    {
        await using var f=await LiveClassroom.Start();
        var dir=Path.Combine(Path.GetTempPath(),"classroom-results-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var learner=CircuitProject.Blink(); learner.Code="void setup(){invalid_student_symbol;}void loop(){}";var studentFile=Path.Combine(dir,"student.json");File.WriteAllText(studentFile,learner.Serialize());
        var student=new MainWindow(false,false){Width=1320,Height=900}; using var studentView=new ElectronicsScreen(student,studentFile,f.Student);student.Preview(studentView);student.Show();student.UpdateLayout();
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        async Task Compile(bool run)=>await (Task)typeof(ElectronicsScreen).GetMethod("Compile",flags)!.Invoke(studentView,[run])!;
        await Compile(false);
        var tabs=studentView.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex=1;student.UpdateLayout();
        var code=studentView.GetVisualDescendants().OfType<TextEditor>().Single(); code.Text="void setup(){pinMode(13,OUTPUT);}void loop(){digitalWrite(13,HIGH);}";await Task.Delay(50);await Compile(true);studentView.Suspend();
        studentView.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Отправить преподавателю").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(async()=>{await studentView.Synchronize();var row=(await f.Admin.Get<ElectronicsOverview>("teacher/electronics")).Students.Single();return row.Submitted==1&&row.Compiled==1&&row.Errors==1;});
        var detail=await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}");Assert.Contains(detail.Reports,r=>r.Kind=="started");Assert.Contains(detail.Reports,r=>r.Kind=="compile-error");
        var error=detail.Reports.Single(r=>r.Kind=="compile-error");Assert.Contains("invalid_student_symbol",(await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/report/{error.Id}")).Detail);
        var own=CircuitExamples.Servo();own.Name="Собственная схема преподавателя";var teacherFile=Path.Combine(dir,"teacher.json");File.WriteAllText(teacherFile,own.Serialize());
        var teacher=new MainWindow(true,false){Width=1320,Height=900};using var teacherView=new ElectronicsScreen(teacher,teacherFile,f.Admin,teacher:true);teacher.Preview(teacherView);teacher.Show();teacher.UpdateLayout();
        var teacherTabs=teacherView.GetVisualDescendants().OfType<TabControl>().Single();teacherTabs.SelectedIndex=3;
        await WaitFor(()=>Task.FromResult(teacherView.GetVisualDescendants().OfType<ListBox>().Any(l=>l.Items.Cast<object>().Any(i=>i.ToString()!.Contains("ui-test")))));teacher.UpdateLayout();Capture(teacher,"electronics-results-overview-1320.png");
        var users=teacherView.GetVisualDescendants().OfType<ListBox>().Single();users.SelectedItem=users.Items.Cast<object>().Single(i=>i.ToString()!.Contains("ui-test"));
        await WaitFor(()=>Task.FromResult(teacherView.GetVisualDescendants().OfType<ElectronicsSnapshotView>().Any()));teacher.UpdateLayout();Capture(teacher,"electronics-results-snapshot-1320.png");
        Assert.DoesNotContain(teacherView.GetVisualDescendants().OfType<Button>(),b=>b.Content?.ToString()=="Запустить" && b.IsVisible && b.GetVisualAncestors().OfType<Control>().All(c=>c.IsVisible));
        var snapshot=teacherView.GetVisualDescendants().OfType<ElectronicsSnapshotView>().Single();snapshot.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex=1;teacher.UpdateLayout();
        var displayed=snapshot.GetVisualDescendants().OfType<TextEditor>().Single();Assert.True(displayed.IsReadOnly);Assert.Equal(code.Text,displayed.Text);
        teacher.Width=900;Capture(teacher,"electronics-results-code-900.png");teacherView.Suspend();Assert.Equal(own.Serialize(),File.ReadAllText(teacherFile));
        student.Close();teacher.Close();
    }
}

