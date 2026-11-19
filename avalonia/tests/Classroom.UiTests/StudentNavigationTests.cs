using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Client;
using Classroom.Desktop;
using Classroom.Domain;
using Xunit;

namespace Classroom.UiTests;

public sealed class StudentNavigationTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompactOverlayKeepsClipboardPolicyOnlyForMarkedTask(bool restricted)
    {
        var window=new MainWindow(false,false);
        using var api=new ClassroomApi(new("https://127.0.0.1:1",new string('0',64)));
        using var sql=new StudentScreen(window,api);
        var source=Catalog.Data.Tasks.Single(t=>t.Id=="database");
        var task=new StudentTask(source.Id,source.Title,source.Module,source.Prompt,source.Concept,source.Example,source.Starter,source.Terms,new(),null,1,1,restricted,[],[],source.Hints.Length,source.Solution,false,0);
        sql.Preview(new([new(source.Id,source.Title,source.Module,null)],[],0),task,new(false,new([],[]),[],null,null));
        window.Preview(sql);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(MainWindow).GetField("student",flags)!.SetValue(window,sql);
        var configure=(Action<Control>)typeof(MainWindow).GetMethod("ConfigureStudentPopup",flags)!.CreateDelegate(typeof(Action<Control>),window);
        var nav=new StudentNavigation(_=>Task.CompletedTask,configure){Width=52,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Left};
        var parts=sql.TakeNavigation();nav.SetWork(parts.Body,parts.Summary);
        var root=(Grid)window.Content!;Grid.SetColumn(nav,1);Grid.SetRow(nav,1);root.Children.Add(nav);
        window.Show();window.UpdateLayout();
        try
        {
            nav.CataloguePopup.IsOpen=true;window.UpdateLayout();Assert.True(nav.CataloguePopup.IsUsingOverlayLayer);
            var search=((Control)nav.CataloguePopup.Child!).GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark=="Поиск задания");
            foreach(var operation in new[]{TextBox.CopyingToClipboardEvent,TextBox.CuttingToClipboardEvent,TextBox.PastingFromClipboardEvent})
            {
                var args=new RoutedEventArgs(operation);search.RaiseEvent(args);Assert.Equal(restricted,args.Handled);
            }
            if(restricted)
            {
                var key=new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.S,KeyModifiers=KeyModifiers.Meta|KeyModifiers.Shift};
                search.RaiseEvent(key);Assert.True(key.Handled);
            }
            Assert.Equal(source.Solution,sql.GetVisualDescendants().OfType<TextEditor>().Single().Text);
        }
        finally
        {
            nav.CataloguePopup.IsOpen=false;nav.SetWork(null,null);sql.RestoreNavigation();
            typeof(MainWindow).GetField("student",flags)!.SetValue(window,null);window.Close();
        }
    }

    static async Task WaitFor(Func<bool> ready){for(var i=0;i<200&&!ready();i++)await Task.Delay(50);Assert.True(ready());}
    static void Click(Control root,string name)
    {
        root.UpdateLayout();var buttons=root.GetVisualDescendants().OfType<Button>().ToArray();
        var matches=buttons.Where(b=>AutomationProperties.GetName(b)==name||b.Content?.ToString()==name).ToArray();
        Assert.True(matches.Length==1,$"Команда {name}: найдено {matches.Length}; доступны "+string.Join(", ",buttons.Select(b=>AutomationProperties.GetName(b)??b.Content?.ToString())));
        matches[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    static void Capture(MainWindow window,string file)
    {
        window.UpdateLayout();var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(output);
        using var frame=window.CaptureRenderedFrame();frame!.Save(Path.Combine(output,file));
    }

    [AvaloniaFact]
    public async Task AuthenticatedSidebarKeepsEditorAcrossRoutesAndCompactCatalogueUsesTheSameControls()
    {
        await using var f=await LiveClassroom.Start();
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var window=new MainWindow(false,false){Width=1320,Height=900};
        typeof(MainWindow).GetField("api",flags)!.SetValue(window,f.Student);
        await (Task)typeof(MainWindow).GetMethod("ShowLogin",flags)!.Invoke(window,null)!;
        window.Show();window.UpdateLayout();
        try
        {
            window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.PasswordChar=='●'&&t.IsEffectivelyVisible).Text="synthetic-student-password";
            window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Items.OfType<StudentChoice>().Any()).SelectedIndex=0;
            Click(window,"Войти");
            await WaitFor(()=>window.GetVisualDescendants().OfType<StudentHomeControl>().Any());
            var nav=window.GetVisualDescendants().OfType<StudentNavigation>().Single();
            await WaitFor(()=>!nav.IsBusy&&nav.Entry("glossary").IsEnabled);
            // Opening the dictionary before the first SQL visit must not leave
            // a constructed-but-never-loaded editor on the next transition.
            Click(window,"Справочник");await WaitFor(()=>nav.IsBusy&&window.IsGlossaryOpen);
            Capture(window,"student-shell-dictionary-before-back.png");
            Click(window,"Закрыть");await WaitFor(()=>!nav.IsBusy);Click(window,"SQL");
            await WaitFor(()=>!nav.IsBusy&&window.GetVisualDescendants().OfType<StudentScreen>().Any());
            var sql=window.GetVisualDescendants().OfType<StudentScreen>().Single();
            var editor=sql.GetVisualDescendants().OfType<TextEditor>().Single();Assert.True(sql.HasLoaded);
            editor.Text="SELECT 31 AS navigation_draft;";editor.Document.UndoStack.ClearAll();editor.Document.Insert(editor.Document.TextLength,"\n-- правка");editor.Select(7,2);
            var document=editor.Document;var caret=editor.CaretOffset;var selection=editor.SelectionLength;var text=editor.Text;
            var search=window.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark=="Поиск задания");
            Capture(window,"student-shell-sql-1320.png");
            Click(window,"Моё занятие");await WaitFor(()=>!nav.IsBusy&&window.GetVisualDescendants().OfType<StudentHomeControl>().Any());
            Click(window,"SQL");await WaitFor(()=>!nav.IsBusy&&window.GetVisualDescendants().OfType<StudentScreen>().Any());
            Assert.Same(editor,sql.GetVisualDescendants().OfType<TextEditor>().Single());Assert.Same(document,editor.Document);
            Assert.Equal(text,editor.Text);Assert.Equal(caret,editor.CaretOffset);Assert.Equal(selection,editor.SelectionLength);Assert.True(document.UndoStack.CanUndo);
            window.Width=704;window.Height=396;window.UpdateLayout();
            await WaitFor(()=>window.Bounds.Width<850&&nav.Bounds.Width==52);window.UpdateLayout();
            Assert.Equal(52,nav.Bounds.Width);Assert.True(nav.Entry("tasks").IsVisible);
            var profile=nav.GetVisualDescendants().OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="Меню профиля");
            var profileAt=profile.TranslatePoint(new Point(0,0),nav)!.Value;
            Assert.InRange(profileAt.Y,0,nav.Bounds.Height-profile.Bounds.Height);
            var choose=nav.Entry("tasks");choose.BringIntoView();window.UpdateLayout();
            var at=choose.TranslatePoint(new Point(choose.Bounds.Width/2,choose.Bounds.Height/2),window)!.Value;
            window.MouseDown(at,MouseButton.Left);window.MouseUp(at,MouseButton.Left);
            var popup=nav.CataloguePopup;await WaitFor(()=>popup.IsOpen);
            Assert.True(popup.IsUsingOverlayLayer);
            var catalogue=(Control)popup.Child!;
            Assert.Same(search,catalogue.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark=="Поиск задания"));
            var category=catalogue.GetVisualDescendants().OfType<Expander>().Single(e=>AutomationProperties.GetName(e)=="Раздел SQL");
            category.IsExpanded=true;window.UpdateLayout();
            var choices=catalogue.GetVisualDescendants().OfType<Button>().Where(b=>b.Classes.Contains("nav-tab")).ToArray();
            await WaitFor(()=>choices.All(b=>b.Bounds.Width>=100));
            // Layout can be ready before the Fluent expansion has produced a
            // rendered frame; capture the opened choices, not its previous frame.
            await Task.Delay(250);
            Capture(window,"student-shell-category-popup.png");
            Assert.Equal(3,choices.Length);Assert.All(choices,b=>Assert.True(b.Bounds.Width>=100));
            choices.Single(b=>b.Content?.ToString()=="Практика").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Практика",category.Header);Assert.False(category.IsExpanded);
            Assert.Equal(text,editor.Text);Assert.Equal(selection,editor.SelectionLength);
            popup.IsOpen=false;
            Click(window,"Моё занятие");await WaitFor(()=>!nav.IsBusy&&window.GetVisualDescendants().OfType<StudentHomeControl>().Any());
            Capture(window,"student-shell-home-704x396.png");
            window.Width=640;window.UpdateLayout();await WaitFor(()=>window.Bounds.Width==640);window.UpdateLayout();
            var compactRows=window.GetVisualDescendants().OfType<Grid>().Where(g=>g.Classes.Contains("graphite-work-row")&&g.Bounds.Width<480).ToArray();
            Assert.NotEmpty(compactRows);
            foreach(var row in compactRows)
            {
                var status=row.Children.OfType<TextBlock>().Last();var action=row.Children.OfType<Button>().Single();
                Assert.True(action.Bounds.Top>=status.Bounds.Bottom);
            }
            Capture(window,"student-shell-home-640x396.png");
            window.Width=1008;window.Height=567;window.UpdateLayout();await WaitFor(()=>nav.Bounds.Width==168);window.UpdateLayout();
            Assert.Equal(168,nav.Bounds.Width);Capture(window,"student-shell-home-1008x567.png");
            var lesson=(await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
            await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause");
            await WaitFor(()=>!nav.Entry("sql").IsEnabled);
            Assert.False(nav.Entry("electronics").IsEnabled);Assert.True(nav.Entry("home").IsEnabled);
            Assert.True(nav.Entry("review").IsEnabled);
            await f.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/resume");await WaitFor(()=>nav.Entry("sql").IsEnabled);
            Click(window,"SQL");await WaitFor(()=>!nav.IsBusy&&window.GetVisualDescendants().OfType<StudentScreen>().Any());
            await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy",new LessonPolicyRequest(null,AllowSql:false,AllowElectronics:true,OpenToAllGroups:true,FreePractice:true));
            await WaitFor(()=>!nav.Entry("sql").IsVisible);
            Assert.False(nav.Entry("glossary").IsVisible);Assert.False(search.IsEffectivelyEnabled);Assert.True(nav.Entry("electronics").IsEnabled);
            await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy",new LessonPolicyRequest(null,OpenToAllGroups:true,FreePractice:true));
            await WaitFor(()=>nav.Entry("sql").IsVisible&&search.IsEffectivelyEnabled);
            Click(window,"Выйти");
            await WaitFor(()=>!window.GetVisualDescendants().OfType<StudentNavigation>().Any()&&window.GetVisualDescendants().OfType<Button>().Any(b=>b.Content?.ToString()=="Войти"));
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(),b=>b.Content?.ToString()=="Войти");
        }
        finally{window.Close();await WaitFor(()=>!window.IsVisible);}
    }
}
