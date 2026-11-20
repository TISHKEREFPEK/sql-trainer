using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Domain;
using System.Text.Json;
using Xunit;

namespace Classroom.UiTests;

public sealed class LessonWorkspaceUiTests
{
    static async Task WaitFor(Func<bool> predicate)
    {
        for (var i = 0; i < 150 && !predicate(); i++) await Task.Delay(50);
        Assert.True(predicate());
    }

    [AvaloniaFact]
    public async Task TeacherSeesAppliedAccessAndOpensTheExactCurrentAndPastLessonResults()
    {
        await using var f = await LiveClassroom.Start();
        var past = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await f.Admin.Post<LessonView>($"teacher/lessons/{past.Id}/finish");
        var group = await f.Admin.Post<GroupDto>("teacher/groups", new GroupRequest("ИСП-191"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group", new GroupMembershipRequest(group.Id));
        var active = await f.Admin.Post<LessonView>("teacher/lessons",
            new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(group.Id, RegistrationOpen: true)));
        var assigned = await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{active.Id}/sql-tasks",
            new LessonSqlSelectionRequest(["database"]));
        var assignmentTitle = Assert.Single(assigned.Tasks).Title;
        await f.Admin.Post<LessonView>($"teacher/lessons/{active.Id}/start");
        await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        await f.Admin.Put<ResultsPreference>("teacher/results/preferences", new ResultsPreference("course"));
        ThemeManager.Apply(new());
        var window = new MainWindow(true, false) { Width = 1320, Height = 950 };
        using var teacher = new TeacherScreen(window, f.Admin, new HostManager());
        window.Preview(teacher);
        window.Show();
        try
        {
            await teacher.Load();
            var lesson = teacher.GetVisualDescendants().OfType<LessonControl>().Single();
            bool HasText(string value) => lesson.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == value && t.IsEffectivelyVisible);
            Button Action(string value) => lesson.GetVisualDescendants().OfType<Button>().Single(b =>
                b.Content as string == value && b.IsEffectivelyVisible);
            void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = lesson.GetVisualDescendants().OfType<Expander>().Single(e => e.Header as string == "Настройки занятия");
            var assignments = lesson.GetVisualDescendants().OfType<Expander>().Single(e => AutomationProperties.GetName(e) == "Задания занятия");
            Assert.False(settings.IsExpanded);
            Assert.False(assignments.IsExpanded);
            Assert.True(HasText("Занятие идёт"));
            Assert.True(HasText("ИСП-191 · SQL и электроника"));
            Assert.True(HasText("Выбранная группа"));
            Assert.True(HasText("Открыта"));
            Assert.True(HasText("1"));
            Assert.True(HasText("SQL · " + assignmentTitle));

            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(output);
            foreach (var width in new[] { 1320, 1008 })
            {
                window.Width = width;
                window.Height = width == 1008 ? 660 : 950;
                window.UpdateLayout();
                foreach (var label in new[] { "Пауза для всех", "Выбрать задания", "Результаты занятия" })
                {
                    var button = Action(label);
                    var at = button.TranslatePoint(default, window)!.Value;
                    Assert.True(at.X + button.Bounds.Width <= window.Bounds.Width + 1);
                    Assert.True(at.Y + button.Bounds.Height <= window.Bounds.Height + 1);
                }
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, $"teacher-lesson-dashboard-{width}.png"));
            }
            settings.IsExpanded = true;
            await WaitFor(() => settings.GetVisualDescendants().OfType<CheckBox>().Any());
            var entry = settings.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Разрешить вход учеников");
            entry.IsChecked = false;
            Assert.Equal("Настройки занятия · есть изменения", settings.Header);
            Assert.True(HasText("Выбранная группа"));
            Click(Action("Пауза для всех"));
            await WaitFor(() => HasText("Занятие на паузе") && settings.IsEnabled);
            Assert.False(entry.IsChecked);
            Assert.True(HasText("Закрыт"));
            Assert.True(HasText("Закрыта"));
            Click(Action("Продолжить занятие"));
            await WaitFor(() => HasText("Занятие идёт") && settings.IsEnabled);
            Assert.False(entry.IsChecked);
            Click(Action("Применить настройки"));
            await WaitFor(() => Equals(settings.Header, "Настройки занятия") && settings.IsEnabled);
            Assert.True(HasText("Закрыт"));
            Assert.True(HasText("Открыта")); // self-registration has its own gate
            var saved = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
            Assert.False(saved.AdmissionOpen);
            Assert.True(saved.RegistrationOpen);

            Click(Action("Выбрать задания"));
            await WaitFor(() => assignments.GetVisualDescendants().OfType<LessonTaskPicker>().Any());
            Assert.True(assignments.IsExpanded);
            Assert.Single(assignments.GetVisualDescendants().OfType<LessonElectronicsPicker>());
            var search = assignments.GetVisualDescendants().OfType<TextBox>().Single(c => c.Watermark == "Поиск SQL-задания");
            search.Text = "Выбор данных";
            assignments.IsExpanded = false;
            Click(Action("Выбрать задания"));
            await WaitFor(() => search.IsEffectivelyVisible);
            Assert.Equal("Выбор данных", search.Text);

            async Task CheckReport(string name)
            {
                await WaitFor(() => teacher.GetVisualDescendants().OfType<ClassroomResultsView>().Any(v =>
                    v.GetVisualDescendants().OfType<DataGrid>().Any() &&
                    v.GetVisualDescendants().OfType<ComboBox>().Any(c => c.SelectedItem?.ToString()?.Contains(name) == true)));
            }
            Click(Action("Результаты занятия"));
            await CheckReport(group.Name);
            Click(teacher.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Занятие"));
            var history = lesson.GetVisualDescendants().OfType<Expander>().Single(e => e.Header as string == "История занятий");
            history.IsExpanded = true;
            await WaitFor(() => history.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Результаты"));
            Click(history.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Результаты"));
            await CheckReport(past.GroupName);
            Assert.Equal("course", (await f.Admin.Get<ResultsPreference>("teacher/results/preferences")).Mode);

            await f.Admin.Post<LessonView>($"teacher/lessons/{active.Id}/finish");
            Click(teacher.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Занятие"));
            await lesson.Refresh(force: true);
            Assert.True(settings.IsExpanded);
            Assert.True(HasText("Занятие ещё не начато"));
            Assert.DoesNotContain(lesson.GetVisualDescendants().OfType<Button>(),
                b => b.Content as string == "Завершить занятие" && b.IsEffectivelyVisible);
            var groupChoice = settings.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => AutomationProperties.GetName(c) == "Группа занятия");
            groupChoice.SelectedItem = groupChoice.Items.Cast<object>().Single(item => item.ToString() == group.Name);
            await WaitFor(() => !search.IsEffectivelyVisible);
            window.UpdateLayout();
            using (var frame = window.CaptureRenderedFrame())
                frame!.Save(Path.Combine(output, "teacher-lesson-prepare-1008.png"));
            Click(Action("Выбрать задания"));
            await WaitFor(() => search.IsEffectivelyVisible);
            var task = (await f.Admin.Get<TeacherOverview>("teacher/overview")).Tasks.Single(t => t.Id == "database");
            var newSearch = assignments.GetVisualDescendants().OfType<TextBox>().Single(c => c.Watermark == "Поиск SQL-задания");
            newSearch.Text = task.Title;
            CheckBox? IssuedRow() => assignments.GetVisualDescendants().OfType<CheckBox>().SingleOrDefault(c =>
                c.Content is TextBlock label && label.Text!.StartsWith(task.Title + " · ", StringComparison.Ordinal));
            await WaitFor(() => IssuedRow() is not null);
            IssuedRow()!.IsChecked = true;
            window.UpdateLayout();
            var begin = Action("Начать занятие");
            Assert.True(begin.TranslatePoint(default, window)!.Value.Y + begin.Bounds.Height <= window.Bounds.Height);
            Click(begin);
            await WaitFor(() => HasText("Занятие идёт") && !settings.IsExpanded && !assignments.IsExpanded);
            var newLesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
            Assert.NotEqual(active.Id, newLesson.Id);
            Assert.Equal(group.Id, newLesson.GroupId);
            var issued = await f.Admin.Get<LessonSqlSelection>($"teacher/lessons/{newLesson.Id}/sql-tasks");
            Assert.Contains(issued.Tasks, work => work.Id == task.Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TeacherCanSelectNewSolutionAndProjectModeAppliesToTheWholeChain()
    {
        await using var f = await LiveClassroom.Start();
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var window = new MainWindow(false, false) { Width = 900, Height = 900 };
        var picker = new LessonTaskPicker(window, f.Admin);
        window.Preview(picker); window.Show();
        try
        {
            await picker.Refresh(lesson.Id, (await f.Admin.Get<TeacherOverview>("teacher/overview")).Tasks);
            var first = Catalog.Data.Tasks.First(t => t.Project == "library");
            var check = picker.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is TextBlock text && text.Text!.StartsWith(first.Title + " · ", StringComparison.Ordinal));
            check.IsChecked = true;
            check.FindAncestorOfType<Grid>()!.Children.OfType<ComboBox>().Single().SelectedIndex = 1;
            picker.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Сохранить выдачу").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LessonSqlSelection? result = null;
            for (var i = 0; i < 100 && result?.Tasks.Length is not > 0; i++)
            {
                result = await f.Admin.Get<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks");
                if (result.Tasks.Length == 0) await Task.Delay(50);
            }
            Assert.Equal(Catalog.Data.Tasks.Count(t => t.Project == "library"), result!.Tasks.Length);
            Assert.All(result.Tasks, task => Assert.True(task.RequireNewSolution));
            await WaitFor(() => !picker.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("не сохранено") == true));
            picker.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "Поиск SQL-задания").Text = first.Title;
            window.UpdateLayout();
            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(output);
            using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "teacher-sql-modes-900.png"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReissuingOpenPracticeReturnsHomeAndKeepsBothEditorsDraftsSeparate()
    {
        await using var f = await LiveClassroom.Start();
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var login = await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var window = new MainWindow(false, false) { Width = 1320, Height = 900 };
        using var view = new StudentScreen(window, f.Student);
        typeof(MainWindow).GetField("api", flags)!.SetValue(window, f.Student);
        typeof(MainWindow).GetField("login", flags)!.SetValue(window, login);
        typeof(MainWindow).GetField("student", flags)!.SetValue(window, view);
        typeof(MainWindow).GetField("screen", flags)!.SetValue(window, view);
        window.Preview(view); window.Show();
        try
        {
            await view.Load("database");
            var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
            const string oldCode = "SELECT 13 AS prior_practice_draft;";
            editor.Text = oldCode;
            await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest(["database"], ["database"]));
            await WaitFor(() => window.GetVisualDescendants().OfType<StudentHomeControl>().Any());
            Assert.Equal(oldCode, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Draft);
            Assert.Empty((await f.Student.Get<StudentTask>("tasks/database")).Draft);
            var home = window.GetVisualDescendants().OfType<StudentHomeControl>().Single();
            await WaitFor(() => home.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Открыть"));
            home.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Открыть").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.GetVisualDescendants().OfType<StudentScreen>().Any() && view.CurrentWorkScope == "lesson:" + lesson.Id);
            Assert.NotEqual(oldCode, editor.Text);
            const string newCode = "SELECT 27 AS new_lesson_draft;";
            editor.Text = newCode; await view.SaveDraft();
            Assert.Equal(newCode, (await f.Student.Get<StudentTask>("tasks/database")).Draft);
            Assert.Equal(oldCode, (await f.Admin.Get<LearningTaskDetail>($"teacher/learning/{f.StudentId}/database")).Draft);
        }
        finally { window.Close(); }
    }
}
