using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Domain;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlWorkspaceUiTests
{
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static async Task Until(Func<bool> ready){for(var i=0;i<200&&!ready();i++)await Task.Delay(50);Assert.True(ready());}
    static Button FindButton(Control root,string name)=>root.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()==name||AutomationProperties.GetName(b)==name);
    static void Click(Control root,string name)=>FindButton(root,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task<(MainWindow Window,StudentScreen View)> Open(LiveClassroom f,int width,int height)
    {
        var login=await f.Student.Login(new("student","ui-test","synthetic-student-password"));
        var window=new MainWindow(false,false){Width=width,Height=height};
        typeof(MainWindow).GetField("api",Hidden)!.SetValue(window,f.Student);typeof(MainWindow).GetField("login",Hidden)!.SetValue(window,login);
        typeof(MainWindow).GetMethod("StartStudentNavigation",Hidden)!.Invoke(window,null);
        window.Show();await window.ShowStudentHome();await window.EnterLesson(new("sql"),"database");
        window.UpdateLayout();await Until(()=>window.Bounds.Width==width&&window.Bounds.Height==height);
        return(window,window.GetVisualDescendants().OfType<StudentScreen>().Single());
    }
    static void Capture(MainWindow window,string name)
    {
        window.UpdateLayout();var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;Directory.CreateDirectory(output);
        using var frame=window.CaptureRenderedFrame();frame!.Save(Path.Combine(output,name));
    }
    static async Task Close(MainWindow window){window.Close();await Until(()=>!window.IsVisible);}

    [AvaloniaTheory]
    [InlineData(1008,567,true)]
    [InlineData(1366,768,false)]
    [InlineData(1920,1080,false)]
    [InlineData(704,396,true)]
    public async Task LongMaterialsAndSqlKeepCommandsInsideTheirPanel(int width,int height,bool restricted)
    {
        await using var f=await LiveClassroom.Start();
        var dto=await f.Admin.Get<TeacherTaskDto>("teacher/tasks/database");
        var definition=Catalog.Read(dto.TaskJson) with
        {
            Title="Проверка длинного названия задания: выбор данных из учебной базы",
            Prompt=string.Join("\n\n",Enumerable.Repeat("Выведите все данные учебной таблицы. Это длинное условие для проверки прокрутки и доступности команд.",18)),
            Concept=string.Join("\n\n",Enumerable.Repeat("Объяснение на русском языке. Работа в редакторе должна сохраняться при переходе между материалами.",18))
        };
        await f.Admin.Put<System.Text.Json.JsonElement>("teacher/tasks/database",new SaveTaskRequest(Catalog.Write(definition),restricted));
        var (window,view)=await Open(f,width,height);
        try
        {
            var editor=view.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.Text=string.Join('\n',Enumerable.Range(1,100).Select(i=>$"-- строка {i}: длинный SQL не растягивает рабочую область"));
            editor.Document.UndoStack.ClearAll();
            editor.Document.Insert(editor.Document.TextLength, "\n-- правка для проверки отмены");
            editor.Select(3,5);var text=editor.Text;var document=editor.Document;var caret=editor.CaretOffset;
            var tabs=view.GetVisualDescendants().OfType<TabControl>().Single();Assert.Equal(0,tabs.SelectedIndex);
            tabs.SelectedIndex=1;window.UpdateLayout();Assert.Same(document,editor.Document);Assert.Equal(text,editor.Text);Assert.Equal(caret,editor.CaretOffset);Assert.Equal(5,editor.SelectionLength);
            tabs.SelectedIndex=0;window.UpdateLayout();
            Click(view, "Отменить");
            Assert.NotEqual(text, editor.Text);
            Click(view, "Повторить");
            Assert.Equal(text, editor.Text);
            var editorPanel=editor.FindAncestorOfType<Border>()!;
            foreach(var label in new[]{"Запустить","Проверить"})
            {
                var button=FindButton(view,label);var at=button.TranslatePoint(new Point(0,0),editorPanel)!.Value;
                Assert.InRange(at.X,0,editorPanel.Bounds.Width-button.Bounds.Width+1);
                Assert.InRange(at.Y,0,editorPanel.Bounds.Height-button.Bounds.Height+1);
            }
            Assert.True(editor.Bounds.Height>0);Assert.True(editor.Bounds.Width>100);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Результат появится после запуска запроса");
            var hint = FindButton(view, "Открыть подсказку");
            Click(view, "Открыть подсказку");
            hint.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !hint.IsVisible || hint.IsEnabled);
            Assert.Single((await f.Student.Get<StudentTask>("tasks/database")).OpenedHints);
            if (hint.IsVisible)
            {
                Click(view, "Открыть подсказку");
                await Until(() => !hint.IsVisible || hint.IsEnabled);
                Assert.Equal(2, (await f.Student.Get<StudentTask>("tasks/database")).OpenedHints.Length);
            }
            Capture(window,$"sql-workspace-materials-{width}x{height}.png");
            var events=window.SqlStore(f.Student).Find(f.Student.LessonId!,"course","database")!.Journal!.Changes;
            Assert.Contains(events,e=>e.Kind=="materials-explanation");Assert.Contains(events,e=>e.Kind=="materials-condition");
        }
        finally{await Close(window);}
    }

    [AvaloniaFact]
    public async Task RetryingPendingRunKeepsOriginalOperationAndShowsItsResultAsStale()
    {
        await using var fixture = await LiveClassroom.Start();
        var (window, view) = await Open(fixture, 1008, 567);
        try
        {
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            var task = await fixture.Student.Get<StudentTask>("tasks/database");
            var lesson = fixture.Student.LessonId!;
            var store = window.SqlStore(fixture.Student);
            const string previous = "SELECT 81 AS previous_answer";
            var pending = new PendingExecution(task.Id, previous, false, lesson, task.WorkScope);
            store.Remember(pending);
            editor.Text = "SELECT 92 AS edited_answer";
            var status = view.GetVisualDescendants().OfType<Button>().Single(button =>
                AutomationProperties.GetName(button)?.Contains("следующий запуск повторит прежний SQL", StringComparison.Ordinal) == true);
            status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.IsEffectivelyVisible && text.Text?.Contains("следующий запуск повторит прежний SQL", StringComparison.Ordinal) == true);
            status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Click(view, "Проверить");
            await Until(() => FindButton(view, "Проверить").IsEnabled &&
                view.GetVisualDescendants().OfType<DataGrid>().Any(grid => grid.Classes.Contains("sql-result-table")));
            var table = view.GetVisualDescendants().OfType<DataGrid>().Single(grid => grid.Classes.Contains("sql-result-table"));
            Assert.Equal("previous_answer", Assert.Single(table.Columns).Header);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible &&
                text.Text == "Запрос изменён. Здесь результат предыдущего запуска.");
            Assert.Null(store.Pending(lesson, task.WorkScope, task.Id));
            var history = store.Find(lesson, task.WorkScope, task.Id)!.Journal!.Changes;
            Assert.Contains(history, item => item.Kind == "run-result" && item.Reference?.OperationId == pending.Request.OperationId);
            Assert.DoesNotContain(history, item => item.Kind == "check");
            Assert.Equal("SELECT 92 AS edited_answer", editor.Text);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task RealEmptyRunErrorStaleResultAndCheckRemainDistinct()
    {
        await using var f=await LiveClassroom.Start();var (window,view)=await Open(f,1366,768);
        try
        {
            var editor=view.GetVisualDescendants().OfType<TextEditor>().Single();
            var initialSources = view.GetVisualDescendants().OfType<TabControl>().Single().GetVisualDescendants()
                .OfType<Expander>().Select(expander => expander.Header?.ToString()).ToArray();
            Assert.NotEmpty(initialSources);
            editor.Text="SELECT 1 AS empty_value WHERE 0";Click(view,"Запустить");
            await Until(()=>view.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Строк: 0")&&FindButton(view,"Запустить").IsEnabled);
            var grid=view.GetVisualDescendants().OfType<DataGrid>().Single(g=>g.Classes.Contains("sql-result-table"));
            Assert.Single(grid.Columns);Assert.Empty(grid.ItemsSource.Cast<object>());
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Результат появится после запуска запроса"||t.Text=="Задание выполнено");
            editor.Text="SELECT FROM";
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.IsVisible&&t.Text=="Запрос изменён. Здесь результат предыдущего запуска.");
            Click(view,"Запустить");await Until(()=>view.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Ошибка")&&FindButton(view,"Запустить").IsEnabled);
            var detail=await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database");
            var error=detail.Events.First(e=>e.Code=="SELECT FROM"&&e.Explanation is not null);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text==error.Result);
            Assert.Equal(initialSources, view.GetVisualDescendants().OfType<TabControl>().Single().GetVisualDescendants()
                .OfType<Expander>().Select(expander => expander.Header?.ToString()).ToArray());
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.IsVisible&&t.Text=="Запрос изменён. Здесь результат предыдущего запуска.");
            Capture(window,"sql-workspace-error-1366.png");
            var task=await f.Student.Get<StudentTask>("tasks/database");var original=Catalog.Data.Tasks.Single(t=>t.Id=="database");
            var solution=task.VariantIndex==1?original.Solution:Variants.Create(original)[task.VariantIndex-2].Solution;
            editor.Text="SELECT 1 AS unexpected";Click(view,"Проверить");
            await Until(()=>view.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Проверьте состав, названия и порядок столбцов.")&&FindButton(view,"Проверить").IsEnabled);
            editor.Text=solution;Click(view,"Запустить");await Until(()=>FindButton(view,"Запустить").IsEnabled&&view.GetVisualDescendants().OfType<DataGrid>().Any(g=>g.Classes.Contains("sql-result-table")));
            Assert.Null((await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Record.CompletedAt);
            Click(view,"Проверить");await Until(()=>view.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Задание выполнено")&&FindButton(view,"Проверить").IsEnabled);
            Assert.NotNull((await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Record.CompletedAt);
            editor.Text+="\n-- после зачёта";Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.IsVisible&&t.Text=="Запрос изменён. Здесь результат предыдущего запуска.");
            Assert.NotNull((await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Record.CompletedAt);
            Capture(window,"sql-workspace-success-stale-1366.png");
        }
        finally{await Close(window);}
    }
}
