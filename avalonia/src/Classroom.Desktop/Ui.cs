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
    public static TextBlock Text(string text, double size = 14)
    {
        var block=new TextBlock {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,LineHeight=size*1.5};
        if(size>=18)block.Classes.Add("heading");
        return block;
    }
    public static T BindBrush<T>(T control,Avalonia.AvaloniaProperty property,string resource) where T: AvaloniaObject
    { control.Bind(property,new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(resource+"Brush"));return control; }
    public static TextBlock Muted(string text,double size=13){var block=Text(text,size);block.Classes.Add("muted");return block;}
    public static Button Primary(Button button){button.Classes.Add("primary");return button;}
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

    public static Button Button(string title, Func<Task> action, Func<bool>? enabledAfter = null)
    {
        var button = new Button
        {
            Content = title,
            MinHeight = 38,
            Padding = new Thickness(14, 9)
        };
        button.Click += async (_, _) =>
        {
            if (!button.IsEffectivelyEnabled) return;
            button.IsEnabled = false;
            button.Classes.Add("busy");
            try
            {
                await action();
            }
            finally
            {
                button.Classes.Remove("busy");
                button.IsEnabled = enabledAfter?.Invoke() ?? true;
            }
        };
        return button;
    }

    public static Border Card(Control child)
    {
        var border = new Border
        {
            Child = child,
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(12),
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
        ["graphite"] = ("Мягкий графит", "#EDEDF0", "#F8F8FA", "#232327", "#FCFCFD"),
        ["light"] = ("Светлая", "#F6F7F9", "#FFFFFF", "#705CC6", "#242938"),
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
        var p = Presets.GetValueOrDefault(theme.Preset, Presets["graphite"]);
        var colors = theme.Colors ?? new();
        string Resolve(string name, string value) => colors.GetValueOrDefault(name, value);
        var surface = Resolve("surface", p.Surface);
        var background = Resolve("background", p.Background);
        var accent = Resolve("accent", p.Accent);
        var editor = Resolve("editor", p.Editor);
        app.RequestedThemeVariant = Ink(surface) == "#FFFFFF" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        var brushes = new Dictionary<string,string>(new[]
        {
            ("Background", background),
            ("BackgroundInk", Ink(background)=="#000000"?"#292B38":Ink(background)),
            ("Surface", surface),
            ("Accent", accent),
            ("Editor", editor),
            ("Ink", Ink(surface)=="#000000"?"#292B38":"#ECEEF5"),
            ("EditorInk", Ink(editor)=="#FFFFFF"?"#E5E7F2":"#292B38"),
            ("AccentInk",Ink(accent)),
            ("Muted", Ink(surface)=="#FFFFFF"?"#A8ADBE":"#6B7080"),
            ("Hover",Ink(surface)=="#FFFFFF"?"#343346":"#F3F1FB"),
            ("Subtle",Ink(surface)=="#FFFFFF"?"#242A38":"#F0F1F5"),
            ("Line", Ink(surface) == "#FFFFFF" ? "#454B53" : "#E3E5EB")
        }.ToDictionary(p=>p.Item1,p=>p.Item2));
        // Keep existing named themes intact. Only the new preset supplies the
        // approved graphite tokens; explicit user colours still take precedence.
        if(theme.Preset=="graphite")
        {
            if(background==p.Background)brushes["BackgroundInk"]="#232327";
            if(surface==p.Surface)
            {
                brushes["Ink"]="#232327";brushes["Muted"]="#63636E";
                brushes["Hover"]="#EAEAF0";brushes["Subtle"]="#ECECF2";brushes["Line"]="#D9D9E1";
            }
            else brushes["Muted"]=brushes["Ink"]=Ink(surface);
            if(editor==p.Editor)brushes["EditorInk"]="#303038";
            else brushes["EditorInk"]=Ink(editor);
            if(background!=p.Background)brushes["BackgroundInk"]=Ink(background);
        }
        var graphite=theme.Preset=="graphite";
        foreach(var (name,value) in new Dictionary<string,string>
        {
            ["SelectedTask"]=graphite&&surface==p.Surface?"#E0E0E7":brushes["Hover"],
            ["LineNumbers"]=graphite&&editor==p.Editor?"#737380":brushes["EditorInk"],
            ["InlineCodeInk"]=graphite&&surface==p.Surface?"#43434E":brushes["Ink"],
            ["InlineCodeBackground"]=brushes["Subtle"],
            ["PrimaryHover"]=graphite&&accent==p.Accent?"#3A3A40":accent,
            ["Connected"]="#55715D",["ProgressTrack"]=graphite&&surface==p.Surface?"#D8D8DF":brushes["Line"],
            ["ProgressFill"]=graphite&&accent==p.Accent?"#55555F":accent,
            ["StaleResultInk"]="#876337",["Focus"]=graphite&&surface==p.Surface?"#797984":brushes["Ink"],
            ["EditorFocus"]=graphite&&editor==p.Editor?"#8A8A98":brushes["EditorInk"],
            ["SuccessInk"]="#405747",["SuccessBackground"]="#E3E9E3",["SuccessLine"]="#D2DFD3",
            ["ErrorInk"]="#85474F",["ErrorBackground"]="#F0E6E7",["ErrorLine"]="#E6D4D6",
            ["InfoInk"]="#555564",["InfoBackground"]="#E7E7ED",["InfoLine"]="#D8D8E1"
        })brushes[name]=value;
        foreach (var(name, value)in brushes)
            app.Resources[name + "Brush"] = new SolidColorBrush(Color.Parse(value));
        var graphiteNavigation=graphite&&surface==p.Surface&&background==p.Background;
        app.Resources["NavigationSelectionBrush"]=graphiteNavigation?new LinearGradientBrush
        {
            StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,1,RelativeUnit.Relative),
            GradientStops=new GradientStops{new(Color.Parse("#F5F5F8"),0),new(Color.Parse("#E2E2E9"),1)}
        }:new SolidColorBrush(Color.Parse(brushes["Hover"]));
        app.Resources["NavigationShadow"]=BoxShadows.Parse(graphiteNavigation?"5 7 18 0 #D3D3DB, -4 -4 13 0 #FFFFFF":"0 3 12 0 #18000000");
        app.Resources["NavigationSelectedShadow"]=BoxShadows.Parse(graphiteNavigation?"3 4 10 0 #D1D1D8, -2 -2 7 0 #FFFFFF":"0 2 6 0 #10000000");
        app.Resources["SystemAccentColor"] = Color.Parse(accent);
        foreach(var key in new[]{"SystemAccentColorLight1","SystemAccentColorLight2","SystemAccentColorDark1","SystemAccentColorDark2"})app.Resources[key]=Color.Parse(accent);
        Changed?.Invoke();
    }
}
