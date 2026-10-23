using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Domain;
using Classroom.Worker;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Classroom.UiTests.TestApplication))]
namespace Classroom.UiTests;
public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ClassroomApplication>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task ConnectionCodeFindsTheRightTeacherAndRetainsPinnedTlsAndLogin()
    {
        await using var first = await LiveClassroom.Start();
        await using var second = await LiveClassroom.Start();
        var firstCode = PairingCode.Create(first.Pairing);
        var secondCode = PairingCode.Create(second.Pairing);
        Assert.NotEqual(firstCode, secondCode);
        using var found = new ClassroomApi(await TeacherDiscovery.Find(PairingCode.Format(secondCode).ToLowerInvariant()));
        Assert.Equal(second.Admin.Connection.Fingerprint, found.Connection.Fingerprint);
        Assert.Equal(second.Admin.Connection.Address, found.Connection.Address);
        Assert.False((await found.Get<StatusDto>("status")).NeedsSetup);
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => found.Login(new("student", "ui-test", "wrong-password")))).Status);
        var login = await found.Login(new("student", "ui-test", "synthetic-student-password"));
        Assert.Equal(second.StudentId, login.Id);
        Assert.Equal("sql", login.Destination!.StandId);
        using var wrongCertificate = new ClassroomApi(found.Connection with { Fingerprint = new string('0', 64) });
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongCertificate.Get<StatusDto>("status"));
        await Assert.ThrowsAsync<IOException>(() => TeacherDiscovery.Find("2222-2222-2222"));
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1320)]
    public async Task CodeConnectionScreensHaveOneStudentFieldAndReadableTeacherCode(int width)
    {
        var window = new MainWindow(false, false) { Width = width, Height = 700 };
        typeof(MainWindow).GetMethod("ShowConnect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
        window.Show();
        Assert.Single(window.GetVisualDescendants().OfType<TextBox>());
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Код преподавателя");
        window.UpdateLayout();
        Capture(window, $"student-connect-{width}.png");
        window.Close();
        var teacher = new MainWindow(true, false) { Width = width, Height = 700 };
        var host = new HostManager();
        typeof(HostManager).GetProperty(nameof(HostManager.ConnectionCode))!.SetValue(host, "ABCD-EFGH-JKLM");
        using var api = new ClassroomApi(new("https://127.0.0.1:1", new string('0', 64)));
        using var view = new TeacherScreen(teacher, api, host);
        teacher.Preview(view); teacher.Show();
        await view.ShowConnectionCode();
        teacher.UpdateLayout();
        Assert.Contains(teacher.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "ABCD-EFGH-JKLM" && t.FontSize >= 40);
        Capture(teacher, $"teacher-connection-code-{width}.png");
        teacher.Close();
    }

    [AvaloniaFact]
    public async Task TeacherCodeIsHiddenUntilLoginAndRemovedOnLogout()
    {
        await using var fixture = await LiveClassroom.Start();
        var teacher = new MainWindow(true, false);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(MainWindow).GetField("api", flags)!.SetValue(teacher, fixture.Admin);
        var host = (HostManager)typeof(MainWindow).GetField("host", flags)!.GetValue(teacher)!;
        var code = PairingCode.Format(PairingCode.Create(fixture.Pairing));
        typeof(HostManager).GetProperty(nameof(HostManager.ConnectionCode))!.SetValue(host, code);
        await (Task)typeof(MainWindow).GetMethod("ShowLogin", flags)!.Invoke(teacher, null)!;
        teacher.Show();
        Assert.DoesNotContain(teacher.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "Код подключения");
        Assert.DoesNotContain(teacher.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == code);
        teacher.GetVisualDescendants().OfType<TextBox>().Single().Text = "synthetic-teacher-password";
        teacher.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Войти").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 150 && !teacher.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Код подключения"); i++) await Task.Delay(50);
        Assert.DoesNotContain(teacher.GetVisualDescendants().OfType<TabItem>(), t => t.Header?.ToString() == "Подключение");
        Assert.DoesNotContain(teacher.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == code);
        Assert.Contains(teacher.GetVisualDescendants().OfType<ComboBox>(), c => c.Items.OfType<string>().SequenceEqual(new[] { "SQL", "Электроника" }));
        teacher.Width = 900; teacher.UpdateLayout();
        Capture(teacher, "teacher-stand-selector-900.png");
        teacher.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Код подключения").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        teacher.UpdateLayout();
        Assert.Contains(teacher.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == code);
        teacher.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Выйти").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 150 && !teacher.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == "Войти"); i++) await Task.Delay(50);
        Assert.Contains(teacher.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "Войти");
        Assert.DoesNotContain(teacher.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == code);
        Assert.DoesNotContain(teacher.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "Код подключения");
        teacher.Close();
    }

    [AvaloniaFact]
    public async Task PausedLoginRecoversAndRejectedSqlDoesNotBecomeAPermanentRetry()
    {
        await using var fixture = await LiveClassroom.Start();
        var definition = Catalog.Data.Tasks.Single(t => t.Id == "database");
        await fixture.Admin.Put<System.Text.Json.JsonElement>("teacher/tasks/database", new SaveTaskRequest(Catalog.Write(definition), true));
        await fixture.Student.Post<System.Text.Json.JsonElement>("tasks/database/violation");
        var window = new MainWindow(false, false);
        using var view = new StudentScreen(window, fixture.Student);
        window.Preview(view); window.Show();
        // Previously Load threw 423 before starting its polling timer.
        await view.Load();
        var run = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Запустить");
        Assert.False(run.IsEnabled);
        await fixture.Admin.Post<System.Text.Json.JsonElement>($"teacher/students/{fixture.StudentId}/unlock");
        for (var i = 0; i < 60 && !run.IsEnabled; i++) await Task.Delay(100);
        Assert.True(run.IsEnabled);
        view.Dispose(); window.Close();

        var task = await fixture.Student.Get<StudentTask>("tasks/database");
        var catalogue = await fixture.Student.Get<CatalogueDto>("catalog");
        var staleWindow = new MainWindow(false, false);
        using var staleView = new StudentScreen(staleWindow, fixture.Student);
        staleView.Preview(catalogue, task, new(false, new([], []), [], null, null));
        staleWindow.Preview(staleView); staleWindow.Show();
        var editor = staleView.GetVisualDescendants().OfType<TextEditor>().Single();
        await fixture.Student.Post<System.Text.Json.JsonElement>("tasks/database/violation");
        editor.Text = "SELECT 7;";
        var rejected = await Assert.ThrowsAsync<ApiException>(staleView.CheckShortcut);
        Assert.Equal(423, rejected.Status);
        Assert.Null(staleWindow.PendingFor("database"));
        await fixture.Admin.Post<System.Text.Json.JsonElement>($"teacher/students/{fixture.StudentId}/unlock");
        editor.Text = definition.Solution;
        await staleView.CheckShortcut();
        Assert.True((await fixture.Student.Get<StudentTask>("tasks/database")).Completed);
        var details = await fixture.Admin.Get<LearningTaskDetail>($"teacher/learning/{fixture.StudentId}/database");
        Assert.Equal(definition.Solution, details.AcceptedCode);
        Assert.Single(details.Events, e => e.Action == "check");

        var projectSource = Catalog.Data.Tasks.First(t => t.Project == "library");
        await (Task)typeof(StudentScreen).GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(staleView, [projectSource.Id])!;
        var assigned = await fixture.Student.Get<StudentTask>($"tasks/{projectSource.Id}");
        editor.Text = assigned.VariantIndex == 1 ? projectSource.Solution : Variants.Create(projectSource)[assigned.VariantIndex - 2].Solution;
        var check = staleView.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Проверить");
        check.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && !(await fixture.Student.Get<StudentTask>($"tasks/{projectSource.Id}")).Completed; i++) await Task.Delay(100);
        await Task.Delay(100);
        Assert.True((await fixture.Student.Get<StudentTask>($"tasks/{projectSource.Id}")).Completed);
        Assert.False(check.IsEnabled); // Button's async-finally must preserve the completed-project policy.
        staleWindow.Close();
    }

    [AvaloniaTheory]
    [InlineData(0, 1320)]
    [InlineData(1, 1320)]
    [InlineData(2, 1320)]
    [InlineData(3, 1320)]
    [InlineData(0, 900)]
    [InlineData(1, 900)]
    [InlineData(2, 900)]
    [InlineData(3, 900)]
    public void LearningStatisticsRendersEveryLevelAndIncludesUnstartedStudents(int level, int width)
    {
        ThemeManager.Apply(new());
        var window = new MainWindow(true, false) { Width = width, Height = 900 };
        using var api = new ClassroomApi(new("https://127.0.0.1:1", new string('0', 64)));
        var view = new LearningStatisticsView(window, api);
        var at = DateTimeOffset.Parse("2026-10-19T09:00:00+03:00").ToUnixTimeMilliseconds();
        var record = new LearningRecord("s1", "database", "База, таблица, строка", "01 · Старт", 1, 1, at, at + 600000, at + 600000, 120, 120, 3, 2, 1, 2, 2, 1);
        var overview = new LearningOverview([new("g", "Демо · ИСП-31"), new("empty", "Пустая группа")], [new("s1", "demo-01", "g", 1, 2, at + 600000, ["database", "columns"]), new("s2", "demo-02", "g", 0, 2, null, ["database", "columns"])], [new("database", record.Title, record.Module, null), new("columns", "Выбираем столбцы", record.Module, null)], [record], at + 600000);
        var detail = new LearningTaskDetail(record, "Покажи все строки таблицы students.", "SELECT 7; -- текущий черновик", "SELECT * FROM students;", [new("c", at + 600000, "check", "correct", "SELECT * FROM students;", null), new("e", at + 30000, "check", "near \"SELEC\": syntax error", "SELEC 1;", "Проверьте написание SELECT."), new("h", at + 20000, "hint", "Подсказка №1", null, null)], false, 3);
        view.Preview(overview, level > 0 ? "g" : null, level > 1 ? "s1" : null, level == 3 ? detail : null);
        window.Preview(view); window.Show(); window.UpdateLayout();
        var labels = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        if (level == 0)
        {
            var grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            var row = grid.ItemsSource.Cast<StatisticsRow>().Single(r => r.Id == "g");
            Assert.Equal(25, row.Percent); Assert.Equal(2, row.Count);
            Assert.Contains(grid.ItemsSource.Cast<StatisticsRow>(), r => r.Id == "empty" && r.Count == 0);
        }
        if (level == 2) Assert.Contains(view.GetVisualDescendants().OfType<DataGrid>().Single().ItemsSource.Cast<StatisticsRow>(), r => r.Id == "columns" && r.State == "Не начато");
        if (level == 3)
        {
            Assert.Contains("Принятый ответ", labels);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(), t => t.IsReadOnly && t.Text == detail.AcceptedCode);
            Assert.Contains(labels, t => t.Contains(Ui.Time(at)));
        }
        Capture(window, $"statistics-{level}-{width}.png"); window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void EditorCutCommandFollowsTaskRestrictionWithoutAKeyEvent(bool restricted)
    {
        var window = new MainWindow(false, false);
        using var api = new ClassroomApi(new("https://127.0.0.1:1", new string('0', 64)));
        using var view = new StudentScreen(window, api);
        var source = Catalog.Data.Tasks.Single(t => t.Id == "database");
        var task = new StudentTask(source.Id, source.Title, source.Module, source.Prompt, source.Concept, source.Example, source.Starter, source.Terms, new(), null, 1, 1, restricted, [], [], source.Hints.Length, source.Solution, false, 0);
        view.Preview(new([new(source.Id, source.Title, source.Module, null)], [], 0), task, new(false, new([], []), [], null, null));
        window.Preview(view);
        window.Show();
        window.UpdateLayout();
        var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
        var labels = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Assert.Contains(labels, t => restricted ? t.Contains("Запрещено: Ctrl+C") && t.Contains("снимки этого окна") : t.Contains("Копирование и вставка разрешены"));
        editor.TextArea.Selection = Selection.Create(editor.TextArea, 0, editor.Text.Length);
        ApplicationCommands.Cut.Execute(null, editor.TextArea);
        Assert.Equal(restricted ? source.Solution : "", editor.Text);
        if (restricted)
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == "Копирование, вырезание и вставка запрещены преподавателем.");
        Capture(window, restricted ? "student-restricted-feedback.png" : "student-unrestricted.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(Key.C, KeyModifiers.Control)]
    [InlineData(Key.X, KeyModifiers.Control)]
    [InlineData(Key.V, KeyModifiers.Control)]
    [InlineData(Key.Insert, KeyModifiers.Control)]
    [InlineData(Key.Insert, KeyModifiers.Shift)]
    [InlineData(Key.Delete, KeyModifiers.Shift)]
    [InlineData(Key.Snapshot, KeyModifiers.None)]
    [InlineData(Key.S, KeyModifiers.Meta | KeyModifiers.Shift)]
    public void RestrictedTaskConsumesClipboardShortcuts(Key key, KeyModifiers modifiers)
    {
        var window = new MainWindow(false, false);
        using var api = new ClassroomApi(new("https://127.0.0.1:1", new string('0', 64)));
        using var view = new StudentScreen(window, api);
        var source = Catalog.Data.Tasks.Single(t => t.Id == "database");
        var task = new StudentTask(source.Id, source.Title, source.Module, source.Prompt, source.Concept, source.Example, source.Starter, source.Terms, new(), null, 1, 1, true, [], [], source.Hints.Length, source.Solution, false, 0);
        view.Preview(new([new(source.Id, source.Title, source.Module, null)], [], 0), task, new(false, new([], []), [], null, null));
        window.Preview(view);
        typeof(MainWindow).GetField("student", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, view);
        window.Show();
        window.UpdateLayout();
        var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        editor.TextArea.RaiseEvent(args);
        Assert.True(args.Handled);
        Assert.Equal(source.Solution, editor.Text);
        typeof(MainWindow).GetField("student", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, null);
        window.Close();
    }

    [AvaloniaFact]
    public void TeacherGroupListReceivesGroupsDuringPollingAndKeepsSelection()
    {
        var window = new MainWindow(true, false);
        using var api = new ClassroomApi(new("https://127.0.0.1:1", new string('0', 64)));
        using var view = new TeacherScreen(window, api, new HostManager());
        var source = Catalog.Data.Tasks.Single(t => t.Id == "database");
        var initial = new TeacherOverview([], [new("a", "Первая")], [new(source.Id, source.Title, source.Module, null)], [], [], []);
        view.Preview(initial, new(Catalog.Write(source), [], false, 1));
        window.Preview(view);
        window.Show();
        window.UpdateLayout();
        var groups = (ComboBox)typeof(TeacherScreen).GetField("groups", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        groups.SelectedIndex = 1;
        typeof(TeacherScreen).GetMethod("RenderOverview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(view, [initial with { Groups = [..initial.Groups, new("b", "Вторая")] }, false]);
        Assert.Equal(3, groups.ItemCount);
        Assert.Equal("Первая", groups.SelectedItem!.ToString());
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("copy")]
    [InlineData("cut")]
    [InlineData("paste")]
    public void RestrictedTaskBlocksTextBoxClipboardEvents(string operation)
    {
        var window = new MainWindow(false, false);
        using var api = new ClassroomApi(new("https://127.0.0.1:1", new string('0', 64)));
        using var view = new StudentScreen(window, api);
        var source = Catalog.Data.Tasks.Single(t => t.Id == "database");
        var task = new StudentTask(source.Id, source.Title, source.Module, source.Prompt, source.Concept, source.Example, source.Starter, source.Terms, new(), null, 1, 1, true, [], [], source.Hints.Length, source.Solution, false, 0);
        view.Preview(new([new(source.Id, source.Title, source.Module, null)], [], 0), task, new(false, new([], []), [], null, null));
        window.Preview(view);
        typeof(MainWindow).GetField("student", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, view);
        window.Show();
        window.UpdateLayout();
        var search = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "Поиск задания");
        var routed = operation switch
        {
            "copy" => TextBox.CopyingToClipboardEvent,
            "cut" => TextBox.CuttingToClipboardEvent,
            _ => TextBox.PastingFromClipboardEvent
        };
        var args = new Avalonia.Interactivity.RoutedEventArgs(routed);
        search.RaiseEvent(args);
        Assert.True(args.Handled);
        typeof(MainWindow).GetField("student", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, null);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("light", 1320)]
    [InlineData("onyx", 1320)]
    [InlineData("light", 900)]
    public void StudentScreensRender(string theme, int width)
    {
        ThemeManager.Apply(new(theme));
        var window = new MainWindow(false, false)
        {
            Width = width,
            Height = 900
        };
        using var api = new ClassroomApi(new("https://127.0.0.1:47831", new string ('0', 64)));
        using var view = new StudentScreen(window, api);
        var definition = Variants.Create(Catalog.Data.Tasks.Single(t => t.Id == "where"))[0];
        var output = SqlEngine.Evaluate(new(definition, null, definition.Solution, true));
        var task = new StudentTask(definition.Id, definition.Title, definition.Module, definition.Prompt, definition.Concept, definition.Example, definition.Starter, definition.Terms, definition.Terms.ToDictionary(t => t, t => Catalog.Data.Glossary.GetValueOrDefault(t, "")), null, 2, 1, false, definition.Hints.Take(1).ToArray(), [12345], 2, definition.Solution, false, 0);
        var catalog = new CatalogueDto(Catalog.Data.Tasks.Select(t => new CatalogueItem(t.Id, t.Title, t.Module, t.Project)).ToArray(), ["database", "columns"], 0);
        view.Preview(catalog, task, output.Result);
        window.Preview(view);
        window.Show();
        window.UpdateLayout();
        Capture(window, $"student-{theme}-{width}.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TeacherScreenRenders(bool reports)
    {
        ThemeManager.Apply(new());
        var window = new MainWindow(true, false);
        using var api = new ClassroomApi(new("https://127.0.0.1:47831", new string ('0', 64)));
        using var view = new TeacherScreen(window, api, new HostManager());
        var items = Catalog.Data.Tasks.Select(t => new CatalogueItem(t.Id, t.Title, t.Module, t.Project)).ToArray();
        var task = Catalog.Data.Tasks.Single(t => t.Id == "where");
        view.Preview(new(Enumerable.Range(0, 35).Select(i => new StudentSummary(i.ToString(), $"Ученик {i + 1}", null, i, 0)).ToArray(), [], items, [], [], []), new TeacherTaskDto(Catalog.Write(task), Variants.Create(task).Select(Catalog.Write).ToArray(), false, 1), reports);
        window.Preview(view);
        window.Show();
        window.UpdateLayout();
        Capture(window, reports ? "teacher-analytics.png" : "teacher-light.png");
        window.Close();
    }

    static void Capture(Window window, string name, int minimumWidth = 850)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width >= minimumWidth);
        var dir = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, name));
    }
}
