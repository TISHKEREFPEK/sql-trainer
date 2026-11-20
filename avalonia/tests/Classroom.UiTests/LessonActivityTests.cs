using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Classroom.Domain;
using Xunit;

namespace Classroom.UiTests;

public sealed class LessonActivityTests
{
    static string Id() => Guid.NewGuid().ToString("N");
    static Task<LessonActivity> Feed(LiveClassroom fixture, LessonView lesson) =>
        fixture.Admin.Get<LessonActivity>($"teacher/lessons/{lesson.Id}/activity");
    static async Task<LessonView> Prepare(LiveClassroom fixture, string? group = null)
    {
        var previous = (await fixture.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await fixture.Admin.Post<LessonView>($"teacher/lessons/{previous.Id}/finish");
        var lesson = await fixture.Admin.Post<LessonView>("teacher/lessons",
            new CreateLessonRequest(Id(), new(group, OpenToAllGroups: true, FreePractice: true)));
        await fixture.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest(["database", "columns"]));
        await fixture.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/start");
        await fixture.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        return lesson;
    }

    static async Task<string> Solve(LiveClassroom fixture)
    {
        var work = await fixture.Student.Get<StudentTask>("tasks/database");
        var task = Catalog.Data.Tasks.Single(t => t.Id == work.Id);
        var code = (work.VariantIndex == 1 ? task : Variants.Create(task)[work.VariantIndex - 2]).Solution;
        var execution = await fixture.Student.Post<ExecutionDto>("tasks/database/execute", new ExecuteRequest(Id(), code, true));
        Assert.True(execution.Correct);
        return code;
    }

    [Fact]
    public async Task FeedUsesRecordedLessonEvidenceAndKeepsHistoricIdentity()
    {
        await using var f = await LiveClassroom.Start();
        var previous = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await Solve(f);
        Assert.Contains((await Feed(f, previous)).Items, item => item.TaskId == "database" && item.Action == "solved" && item.WorkScope == "course");
        var group = await f.Admin.Post<GroupDto>("teacher/groups", new GroupRequest("ИСП-191"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group", new GroupMembershipRequest(group.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name", new StudentNameRequest("Анна Ковалёва"));
        var lesson = await Prepare(f, group.Id);
        Assert.Empty((await Feed(f, lesson)).Items);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() =>
            f.Student.Get<LessonActivity>($"teacher/lessons/{lesson.Id}/activity"))).Status);

        await f.Student.Get<StudentTask>("tasks/columns");
        var inProgress = Assert.Single((await Feed(f, lesson)).Items);
        Assert.Equal(("sql", "columns", "working", "Анна Ковалёва", "ИСП-191"),
            (inProgress.Subject, inProgress.TaskId, inProgress.Action, inProgress.StudentName, inProgress.GroupName));

        var project = CircuitProject.Blink();
        project.Name = "Чертёж ученика";
        var projectJson = project.Serialize();
        var taskId = Id();
        await f.Admin.Put<ElectronicsTaskDto>($"teacher/electronics/tasks/{taskId}",
            new ElectronicsTaskRequest("Светодиод с кнопкой", "Проверьте работу схемы", projectJson));
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks", new ElectronicsSelectionRequest([taskId]));
        var assignment = await f.Student.Get<ElectronicsAssignmentDto>($"electronics/tasks/{taskId}");
        var report = Id();
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Id(), 1, assignment.ProjectJson,
            [new(report, "submitted", assignment.ProjectJson, "", assignment.StateVersion)], assignment.Id));
        var queued = (await Feed(f, lesson)).Items;
        Assert.Contains(queued, item => item.TaskId == taskId && item.Title == "Светодиод с кнопкой" && item.Action == "submitted");
        var route = $"teacher/reviews/electronics/{f.StudentId}/{report}";
        var detail = await f.Admin.Get<ElectronicsReviewDetail>(route);
        await f.Admin.Put<ElectronicsReviewDecision>($"{route}/decisions/{Id()}",
            new ElectronicsReviewRequest("accepted", "Проверено", 5, detail.History.LastOrDefault()?.Id, detail.AssignmentVersion));

        var free = Id();
        project.Name = "Свободный проект";
        var freeJson = project.Serialize();
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/" + free, new ElectronicsProjectCreate(freeJson));
        await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{free}/open", new ElectronicsProjectOpen(lesson.Id));
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work", new ElectronicsSyncRequest(Id(), 1, freeJson,
            [new(Id(), "submitted", freeJson, "", 1)], FreeContext: new(free, lesson.Id, 0)));
        var another = Id();
        await f.Student.Put<ElectronicsProjectDto>("electronics/projects/" + another, new ElectronicsProjectCreate(freeJson));
        await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{another}/open", new ElectronicsProjectOpen(lesson.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name", new StudentNameRequest("Имя после урока"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group", new GroupMembershipRequest(null));

        var latest = (await Feed(f, lesson)).Items;
        Assert.Equal(6, latest.Length);
        Assert.Equal(latest.OrderByDescending(item => item.At), latest);
        Assert.All(latest, item => { Assert.Equal("Анна Ковалёва", item.StudentName); Assert.Equal(group.Name, item.GroupName); });
        Assert.Contains(latest, item => item.TaskId == taskId && item.Action == "accepted");
        Assert.Contains(latest, item => item.TaskId == free && item.Action == "submitted" && item.WorkScope == "free-project");
        Assert.DoesNotContain(latest, item => item.TaskId == "database");
        Assert.DoesNotContain(latest, item => item.TaskId == "columns");
        Assert.DoesNotContain((await Feed(f, previous)).Items, item => item.TaskId == taskId || item.TaskId == free);
    }

    [AvaloniaFact]
    public async Task ActivityRefreshesAndOpensTheExactWorkFromTheTeacherScreen()
    {
        await using var f = await LiveClassroom.Start();
        var group = await f.Admin.Post<GroupDto>("teacher/groups", new GroupRequest("ИСП-191"));
        await f.Admin.Put<JsonElement>($"teacher/students/{f.StudentId}/group", new GroupMembershipRequest(group.Id));
        await f.Admin.Put<StudentChoice>($"teacher/students/{f.StudentId}/name", new StudentNameRequest("Анна Ковалёва"));
        var lesson = await Prepare(f, group.Id);
        ThemeManager.Apply(new());
        var window = new MainWindow(true, false) { Width = 1320, Height = 950 };
        using var teacher = new TeacherScreen(window, f.Admin, new HostManager());
        window.Preview(teacher);
        window.Show();
        try
        {
            await teacher.Load();
            var control = teacher.GetVisualDescendants().OfType<LessonControl>().Single();
            var panel = control.GetVisualDescendants().OfType<LessonActivityPanel>().Single();
            Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Работы на этом занятии пока не открывали.");
            await f.Student.Get<StudentTask>("tasks/columns");
            var project = CircuitProject.Blink();
            project.Name = "Светодиод на Arduino";
            var projectId = Id();
            await f.Student.Put<ElectronicsProjectDto>("electronics/projects/" + projectId, new ElectronicsProjectCreate(project.Serialize()));
            await f.Student.Post<ElectronicsProjectDto>($"electronics/projects/{projectId}/open", new ElectronicsProjectOpen(lesson.Id));
            var solution = await Solve(f);
            await control.Refresh(true);
            var activity = await Feed(f, lesson);
            var solved = activity.Items.Single(item => item.Action == "solved");
            Button Row() => panel.GetVisualDescendants().OfType<Button>().Single(b =>
                AutomationProperties.GetName(b)?.Contains(solved.Title + ", Решено", StringComparison.Ordinal) == true);
            var button = Row();
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Ui.Time(solved.At));
            Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == project.Name);
            await control.Refresh(true);
            Assert.Same(button, Row());

            var output = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(output);
            foreach (var width in new[] { 1320, 900 })
            {
                window.Width = width;
                window.Height = width == 900 ? 660 : 950;
                window.UpdateLayout();
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.True(point.X + button.Bounds.Width <= window.Bounds.Width + 1);
                Assert.True(point.Y + button.Bounds.Height <= window.Bounds.Height + 1);
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, $"teacher-lesson-activity-{width}.png"));
            }

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 120 && !HasAcceptedCode(); i++) await Task.Delay(50);
            Assert.True(HasAcceptedCode());
            Assert.Contains(teacher.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == solved.StudentName);
            teacher.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Занятие")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.GetVisualDescendants().OfType<Button>().Single(b =>
                AutomationProperties.GetName(b)?.Contains(project.Name + ", Схема открыта", StringComparison.Ordinal) == true)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 120 && !HasProjectSnapshot(); i++) await Task.Delay(50);
            Assert.True(HasProjectSnapshot());
            Assert.Contains(teacher.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == project.Name);

            bool HasAcceptedCode() => teacher.GetVisualDescendants().OfType<ClassroomResultsView>().Any(view =>
                view.GetVisualDescendants().OfType<TextBox>().Any(code => code.IsReadOnly && code.Text == solution));
            bool HasProjectSnapshot() => teacher.GetVisualDescendants().OfType<ClassroomResultsView>().Any(view =>
                view.GetVisualDescendants().OfType<Button>().Any(action => action.Content as string == "Открыть сохранённую схему"));
        }
        finally { window.Close(); }
    }
}
