using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Classroom.Contracts;

namespace Classroom.Desktop;
public static class Ui
{
    public static TextBlock Text(string text, double size = 14) => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap
    };
    public static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel
        {
            Spacing = 12
        };
        foreach (var c in children)
            panel.Children.Add(c);
        return panel;
    }

    public static StackPanel Row(params Control[] children)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        foreach (var c in children)
            panel.Children.Add(c);
        return panel;
    }

    public static Button Button(string title, Func<Task> action)
    {
        var button = new Button
        {
            Content = title,
            MinHeight = 36,
            Padding = new Thickness(14, 7)
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await action();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    public static Border Card(Control child)
    {
        var border = new Border
        {
            Child = child,
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        };
        border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SurfaceBrush"));
        border.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("LineBrush"));
        return border;
    }

    public static ScrollViewer Scroll(Control child) => new()
    {
        Content = child,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    public static Control Field(string label, Control input) => Stack(Text(label), input);
    public static TextBox Input(string? text = null, bool multiline = false) => new()
    {
        Text = text,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MinHeight = multiline ? 100 : 36
    };
    public static Control Table(SqlResult result)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            MaxHeight = 340,
            MinHeight = 80
        };
        for (var i = 0; i < result.Columns.Length; i++)
            grid.Columns.Add(new DataGridTextColumn { Header = result.Columns[i], Binding = new Binding($"[{i}]"), MinWidth = 80, Width = new DataGridLength(1, DataGridLengthUnitType.Auto) });
        grid.ItemsSource = result.Values.Select(row => row.Select(Display).ToArray()).ToArray();
        return grid;
    }

    public static string Display(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null => "NULL",
        System.Text.Json.JsonValueKind.String => value.GetString()!,
        _ => value.ToString()};
    public static string Time(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
}

public static class ThemeManager
{
    public static event Action? Changed;
    public static readonly Dictionary<string, (string Name, string Background, string Surface, string Accent, string Editor)> Presets = new()
    {
        ["light"] = ("Светлая", "#EDF2F4", "#FFFFFF", "#137C80", "#20343D"),
        ["mist"] = ("Туман", "#E9EDF5", "#F7F9FF", "#5267AA", "#28334F"),
        ["paper"] = ("Бумага", "#EFECE5", "#FFFCF5", "#71613D", "#36362F"),
        ["dark"] = ("Тёмная", "#20262C", "#2A323A", "#6BC9BF", "#1A2026"),
        ["onyx"] = ("Оникс", "#101010", "#191919", "#B9A6EB", "#121212"),
        ["midnight"] = ("Полночь", "#141D30", "#1D2A42", "#89AEFA", "#10192A")
    };
    public static string Ink(string color)
    {
        var rgb = Color.Parse(color);
        var l = new[]
        {
            rgb.R / 255d,
            rgb.G / 255d,
            rgb.B / 255d
        }.Select(v => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4)).ToArray();
        var lum = l[0] * .2126 + l[1] * .7152 + l[2] * .0722;
        return (lum + .05) / .05 >= 1.05 / (lum + .05) ? "#000000" : "#FFFFFF";
    }

    public static void Apply(ThemePreference theme)
    {
        var app = Application.Current!;
        var p = Presets.GetValueOrDefault(theme.Preset, Presets["light"]);
        var colors = theme.Colors ?? new();
        string Resolve(string name, string value) => colors.GetValueOrDefault(name, value);
        var surface = Resolve("surface", p.Surface);
        var background = Resolve("background", p.Background);
        var accent = Resolve("accent", p.Accent);
        var editor = Resolve("editor", p.Editor);
        app.RequestedThemeVariant = Ink(surface) == "#FFFFFF" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        foreach (var(name, value)in new[]
        {
            ("Background", background),
            ("Surface", surface),
            ("Accent", accent),
            ("Editor", editor),
            ("Ink", Ink(surface)),
            ("EditorInk", Ink(editor)),
            ("Line", Ink(surface) == "#FFFFFF" ? "#454B53" : "#D5E0E4")
        }

        )
            app.Resources[name + "Brush"] = new SolidColorBrush(Color.Parse(value));
        app.Resources["SystemAccentColor"] = Color.Parse(accent);
        Changed?.Invoke();
    }
}
