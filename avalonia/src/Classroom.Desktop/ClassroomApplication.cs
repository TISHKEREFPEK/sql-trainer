using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Classroom.Desktop;
public sealed class ClassroomApplication : Application
{
    public static bool Teacher { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Classroom.Desktop/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://Classroom.Desktop/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        var style = new Style(x => x.OfType<Window>());
        style.Setters.Add(new Setter(Window.FontFamilyProperty, new Avalonia.Media.FontFamily("Segoe UI, Arial")));
        style.Setters.Add(new Setter(Window.FontSizeProperty, 14d));
        style.Setters.Add(new Setter(Window.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BackgroundBrush")));
        style.Setters.Add(new Setter(Window.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("InkBrush")));
        Styles.Add(style);
        ThemeManager.Apply(new());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Teacher);
        base.OnFrameworkInitializationCompleted();
    }

    public static int Run(string[] args, bool teacher)
    {
        Teacher = teacher;
        return AppBuilder.Configure<ClassroomApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
