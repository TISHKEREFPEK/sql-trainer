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

    static void Capture(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width >= 850);
        var dir = Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, name));
    }
}
