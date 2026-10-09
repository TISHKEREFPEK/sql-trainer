using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
