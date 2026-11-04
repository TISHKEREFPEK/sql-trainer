using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;
public sealed class LessonRoutingTests
{
    static void Click(MainWindow window,string text) => window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task WaitFor(Func<bool> ready) { for(var i=0;i<150&&!ready();i++) await Task.Delay(50); Assert.True(ready()); }
    static async Task ShowLogin(MainWindow window,ClassroomApi api)
    {
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(MainWindow).GetField("api",flags)!.SetValue(window,api);
        await (Task)typeof(MainWindow).GetMethod("ShowLogin",flags)!.Invoke(window,null)!;
        window.Show(); window.UpdateLayout();
    }
    [AvaloniaFact]
    public async Task TeacherWorkspaceIsIndependentAndStudentSwitchesAllowedSubjectsWithPause()
    {
        await using var fixture=await LiveClassroom.Start();
        using var anonymous=new ClassroomApi(fixture.Admin.Connection);
        Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>anonymous.Put<LessonDestination>("teacher/lesson",new LessonDestination("electronics")))).Status);
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>fixture.Student.Put<LessonDestination>("teacher/lesson",new LessonDestination("electronics")))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(()=>fixture.Admin.Put<LessonDestination>("teacher/lesson",new LessonDestination("unknown")))).Status);
        var previousData=Environment.GetEnvironmentVariable("CLASSROOM_DATA"); var previousStudent=Environment.GetEnvironmentVariable("CLASSROOM_STUDENT_DATA");
        var dir=Path.Combine(Path.GetTempPath(),"classroom-routing-"+Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLASSROOM_DATA",dir); Environment.SetEnvironmentVariable("CLASSROOM_STUDENT_DATA",dir);
        var teacher=new MainWindow(true,false){Width=1320,Height=900}; var student=new MainWindow(false,false){Width=1320,Height=900};
        try
        {
            await ShowLogin(teacher,fixture.Admin);
            teacher.GetVisualDescendants().OfType<TextBox>().Single().Text="synthetic-teacher-password"; Click(teacher,"Войти");
            await WaitFor(()=>teacher.GetVisualDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Код подключения"));
            var selector=teacher.GetVisualDescendants().OfType<ComboBox>().Single(c=>Avalonia.Automation.AutomationProperties.GetName(c)=="Стенд"); selector.SelectedIndex=1;
            await WaitFor(()=>selector.IsEnabled&&teacher.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            Click(teacher,"Код подключения"); teacher.UpdateLayout();
            Assert.Equal(1,selector.SelectedIndex);
            Assert.Contains(teacher.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Стенд: Электроника");
            Click(teacher,"Назад к стенду"); Assert.Contains(teacher.GetVisualDescendants(),v=>v is ElectronicsScreen);
            await ShowLogin(student,fixture.Student);
            student.GetVisualDescendants().OfType<TextBox>().Single(t=>t.PasswordChar=='●'&&t.IsEffectivelyVisible).Text="synthetic-student-password";
            student.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Items.OfType<StudentChoice>().Any()).SelectedIndex=0;
            Click(student,"Войти"); await WaitFor(()=>student.GetVisualDescendants().OfType<StudentScreen>().Any());
            Click(student,"Электроника"); await WaitFor(()=>student.GetVisualDescendants().OfType<ElectronicsScreen>().Any());
            Assert.DoesNotContain(student.GetVisualDescendants(),v=>v is StudentScreen);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var poll=(Avalonia.Threading.DispatcherTimer)typeof(MainWindow).GetField("lessonPoll",flags)!.GetValue(student)!;
            await WaitFor(()=>poll.IsEnabled);
            var lesson=(await fixture.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
            await fixture.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause");
            Assert.False((await fixture.Student.Get<StudentLessonDto>("student/lesson")).CanWork);
            var body=(ContentControl)typeof(MainWindow).GetField("body",flags)!.GetValue(student)!;
            await WaitFor(()=>!body.IsEnabled);
            Assert.Contains(student.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Contains("Занятие на паузе") == true);
            await fixture.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/resume");
            await WaitFor(()=>body.IsEnabled);
            selector.SelectedIndex=0; await WaitFor(()=>selector.IsEnabled&&teacher.GetVisualDescendants().OfType<TeacherScreen>().Any());
            teacher.UpdateLayout();
            var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory; Directory.CreateDirectory(output);
            using(var frame=teacher.CaptureRenderedFrame()) frame!.Save(Path.Combine(output,"teacher-lesson-1320.png"));
            teacher.Width=900;teacher.UpdateLayout();
            using(var frame=teacher.CaptureRenderedFrame()) frame!.Save(Path.Combine(output,"teacher-lesson-900.png"));
            var login=await fixture.Student.Login(new("student","ui-test","synthetic-student-password")); Assert.Equal("sql",login.Destination!.StandId);
            await fixture.Admin.Put<LessonDestination>("teacher/lesson",new LessonDestination("sql"));
            Assert.Equal("sql",(await fixture.Student.Login(new("student","ui-test","synthetic-student-password"))).Destination!.StandId);
            student.GetVisualDescendants().OfType<ElectronicsScreen>().Single().Suspend(); Assert.Equal(2,Directory.GetFiles(Path.Combine(dir,"electronics"),"*.json").Count(path=>!path.EndsWith(".sync.json",StringComparison.Ordinal)));
        }
        finally { teacher.Close(); student.Close(); Environment.SetEnvironmentVariable("CLASSROOM_DATA",previousData); Environment.SetEnvironmentVariable("CLASSROOM_STUDENT_DATA",previousStudent); }
    }
}


