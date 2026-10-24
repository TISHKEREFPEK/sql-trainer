using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;
public sealed class EmbeddedPageTests
{
    static readonly System.Reflection.BindingFlags Flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    static void Click(MainWindow window,string name) => window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task WaitFor(Func<bool> ready) { for(var i=0;i<150&&!ready();i++) await Task.Delay(50); Assert.True(ready()); }
    static void Capture(MainWindow window,string name)
    {
        window.UpdateLayout(); using var frame=window.CaptureRenderedFrame(); var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots"); Directory.CreateDirectory(output); frame!.Save(Path.Combine(output,name));
    }
    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task AppearanceUsesMainWindowRollsBackOnBackAndSavesWithoutLosingCircuit(int width)
    {
        await using var fixture=await LiveClassroom.Start();
        var login=await fixture.Student.Login(new("student","ui-test","synthetic-student-password")); ThemeManager.Apply(login.Theme);
        var window=new MainWindow(false,false){Width=width,Height=900};
        typeof(MainWindow).GetField("api",Flags)!.SetValue(window,fixture.Student); typeof(MainWindow).GetField("login",Flags)!.SetValue(window,login);
        var dir=Path.Combine(Path.GetTempPath(),"classroom-page-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var file=Path.Combine(dir,"circuit.json");
        var project=CircuitProject.Blink(); project.Code="// мой проект\nvoid setup(){} void loop(){}"; File.WriteAllText(file,project.Serialize());
        using var circuit=new ElectronicsScreen(window,file); typeof(MainWindow).GetField("electronics",Flags)!.SetValue(window,circuit); typeof(MainWindow).GetField("screen",Flags)!.SetValue(window,circuit);
        window.Preview(circuit); window.Show(); window.UpdateLayout();
        try
        {
            var original=((SolidColorBrush)Application.Current!.Resources["BackgroundBrush"]!).Color;
            await window.ShowAppearance(); window.UpdateLayout(); Assert.Empty(window.OwnedWindows);
            var page=window.GetVisualDescendants().OfType<AppearancePage>().Single();
            page.GetVisualDescendants().OfType<TextBox>().First(t=>t.Watermark?.StartsWith("#RRGGBB")==true).Text="#123456";
            await WaitFor(()=>((SolidColorBrush)Application.Current.Resources["BackgroundBrush"]!).Color==Color.Parse("#123456")); Capture(window,$"appearance-page-{width}.png");
            Click(window,"Назад"); Assert.Contains(window.GetVisualDescendants(),v=>ReferenceEquals(v,circuit)); Assert.Equal(original,((SolidColorBrush)Application.Current.Resources["BackgroundBrush"]!).Color);
            Assert.Equal(project.Code,CircuitProject.Parse(File.ReadAllText(file)).Code);
            await window.ShowAppearance(); window.UpdateLayout();
            window.GetVisualDescendants().OfType<AppearancePage>().Single().GetVisualDescendants().OfType<TextBox>().First(t=>t.Watermark?.StartsWith("#RRGGBB")==true).Text="#345678";
            await Task.Delay(50); Click(window,"Сохранить"); await WaitFor(()=>!window.GetVisualDescendants().OfType<AppearancePage>().Any());
            Assert.Contains(window.GetVisualDescendants(),v=>ReferenceEquals(v,circuit));
            var stored=await fixture.Student.Login(new("student","ui-test","synthetic-student-password")); Assert.Equal("#345678",stored.Theme.Colors!["background"]);
            Assert.Equal(project.Code,CircuitProject.Parse(File.ReadAllText(file)).Code);
        }
        finally { window.Close(); ThemeManager.Apply(new()); }
    }
    [AvaloniaFact]
    public async Task GlossaryOpensAsSearchablePageAndReturnsToSameSqlDraft()
    {
        await using var fixture=await LiveClassroom.Start();
        var window=new MainWindow(false,false){Width=900,Height=900}; using var view=new StudentScreen(window,fixture.Student); window.Preview(view); window.Show(); await view.Load(); window.UpdateLayout();
        var editor=view.GetVisualDescendants().OfType<TextEditor>().Single(); editor.Text="SELECT 17 AS page_preserved;";
        Click(window,"Справочник"); await WaitFor(()=>window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Справочник SQL"));
        Assert.Empty(window.OwnedWindows);
        Assert.False(window.IsLessonVisible);
        typeof(MainWindow).GetField("student",Flags)!.SetValue(window,view);
        var shortcut = new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent,Key=Key.Enter,KeyModifiers=KeyModifiers.Control };
        window.RaiseEvent(shortcut); Assert.False(shortcut.Handled);
        var term=(await fixture.Student.Get<Dictionary<string,string>>("glossary")).Keys.First();
        var filter=window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark=="Поиск термина"); filter.Text=term;
        await Task.Delay(50); Assert.All(window.GetVisualDescendants().OfType<Expander>(),e=>Assert.Contains(term,e.Header!.ToString()!,StringComparison.OrdinalIgnoreCase)); Capture(window,"glossary-page-900.png");
        Click(window,"Назад"); Assert.True(window.IsLessonVisible); Assert.Contains(window.GetVisualDescendants(),v=>ReferenceEquals(v,view)); Assert.Equal("SELECT 17 AS page_preserved;",editor.Text); Assert.Empty(window.OwnedWindows);
        typeof(MainWindow).GetField("student",Flags)!.SetValue(window,null);
        window.Close();
    }
}
