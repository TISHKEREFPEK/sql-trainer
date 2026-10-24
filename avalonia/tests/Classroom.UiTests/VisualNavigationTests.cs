using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;
public sealed class VisualNavigationTests
{
    [AvaloniaFact]
    public async Task CategoryTabsKeepDraftAndLiveThemeUpdatesBothNativeEditors()
    {
        await using var fixture=await LiveClassroom.Start();ThemeManager.Apply(new());
        var window=new MainWindow(false,false){Width=1320,Height=900};using var student=new StudentScreen(window,fixture.Student);window.Preview(student);window.Show();await student.Load();window.UpdateLayout();
        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        StudentTask Current()=>(StudentTask)typeof(StudentScreen).GetField("current",flags)!.GetValue(student)!;
        var original=Current();var editor=student.GetVisualDescendants().OfType<TextEditor>().Single();const string draft="SELECT 23 AS style_keeps_draft;";editor.Text=draft;
        student.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Практика").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var tasks=(ListBox)typeof(StudentScreen).GetField("tasks",flags)!.GetValue(student)!;Assert.InRange(tasks.Items.Count,1,8);tasks.SelectedIndex=0;
        for(var i=0;i<100&&Current().Id==original.Id;i++)await Task.Delay(50);Assert.NotEqual(original.Id,Current().Id);
        Assert.Equal(draft,(await fixture.Student.Get<StudentTask>("tasks/"+original.Id)).Draft);
        var newText=editor.Text;ThemeManager.Apply(new("onyx"));Assert.Equal(newText,editor.Text);Assert.NotEqual(((SolidColorBrush)editor.Background!).Color,((SolidColorBrush)editor.Foreground!).Color);
        var file=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"classroom-native-style-"+Guid.NewGuid().ToString("N"),"project.json");using var circuit=new ElectronicsScreen(window,file);window.Preview(circuit);window.UpdateLayout();
        var tabs=circuit.GetVisualDescendants().OfType<TabControl>().Single();tabs.SelectedIndex=1;window.UpdateLayout();var code=circuit.GetVisualDescendants().OfType<TextEditor>().Single();var unchanged=code.Text;
        ThemeManager.Apply(new("light",new(){["editor"]="#FFFFFF"}));Assert.Equal(Color.Parse("#FFFFFF"),((SolidColorBrush)code.Background!).Color);Assert.Equal(unchanged,code.Text);
        Assert.NotEqual(Color.Parse("#FFFFFF"),code.SyntaxHighlighting.GetNamedColor("Keyword").Foreground!.GetColor(null));
        ThemeManager.Apply(new("onyx"));tabs.SelectedIndex=0;window.UpdateLayout();
        using(var frame=window.CaptureRenderedFrame()){var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(output);frame!.Save(System.IO.Path.Combine(output,"electronics-onyx-1320.png"));}
        window.Close();ThemeManager.Apply(new());
    }
}
