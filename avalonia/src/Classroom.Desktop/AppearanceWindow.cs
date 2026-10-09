using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;
public sealed class AppearanceWindow : Window
{
    readonly ThemePreference original;
    readonly ClassroomApi api;
    readonly ComboBox preset = new();
    readonly ComboBox personal = new();
    readonly TextBlock message = Ui.Text("");
    readonly Dictionary<string, TextBox> colors = new();
    bool committed;
    public AppearanceWindow(ClassroomApi api, ThemePreference original)
    {
        this.api = api;
        this.original = original;
        Title = "Оформление";
        Width = 580;
        Height = 750;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        preset.ItemsSource = ThemeManager.Presets.Select(p => new Choice(p.Key, p.Value.Name)).ToArray();
        preset.SelectedItem = (preset.ItemsSource as Choice[])!.First(p => p.Id == original.Preset);
        var panel = Ui.Stack(Ui.Text("Оформление", 24), Ui.Field("Тема", preset));
        foreach (var(key, label)in new[]
        {
            ("background", "Фон"),
            ("surface", "Панели"),
            ("accent", "Акцент"),
            ("editor", "SQL-редактор")
        }

        )
        {
            var box = Ui.Input(original.Colors?.GetValueOrDefault(key));
            box.Watermark = "#RRGGBB — оставьте пустым для цвета темы";
            colors[key] = box;
            box.TextChanged += (_, _) => Preview();
            panel.Children.Add(Ui.Field(label, box));
        }

        preset.SelectionChanged += (_, _) => Preview();
        panel.Children.Add(Ui.Row(Ui.Button("Сохранить", () => Safe(async () =>
        {
            var theme = Preference();
            await api.Put<JsonElement>("theme", theme);
            committed = true;
            Close(theme);
        })), Ui.Button("Отменить", () =>
        {
            Close(null);
            return Task.CompletedTask;
        })));
        var name = Ui.Input();
        panel.Children.Add(Ui.Text("Мои темы", 20));
        panel.Children.Add(personal);
        panel.Children.Add(Ui.Button("Применить выбранную", () =>
        {
            if (personal.SelectedItem is Saved selected)
            {
                var t = selected.Preset.Theme;
                preset.SelectedItem = (preset.ItemsSource as Choice[])!.First(p => p.Id == t.Preset);
                foreach (var(key, input)in colors)
                    input.Text = t.Colors?.GetValueOrDefault(key);
                Preview();
            }

            return Task.CompletedTask;
        }));
        panel.Children.Add(Ui.Field("Название новой темы", name));
        panel.Children.Add(Ui.Button("Сохранить пресет", () => Safe(async () =>
        {
            await api.Post<ThemePreset[]>("theme-presets", new ThemePresetRequest(name.Text ?? "", Preference()));
            await Refresh();
            message.Text = "Пресет сохранён.";
        })));
        panel.Children.Add(Ui.Button("Удалить пресет", () => Safe(async () =>
        {
            if (personal.SelectedItem is Saved selected)
            {
                await api.Delete<ThemePreset[]>("theme-presets/" + selected.Preset.Id);
                await Refresh();
            }
        })));
        panel.Children.Add(message);
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Scroll(panel)
        };
        Opened += async (_, _) => await Safe(Refresh);
        Closed += (_, _) =>
        {
            if (!committed)
                ThemeManager.Apply(original);
        };
    }

    ThemePreference Preference()
    {
        var entries = colors.Where(p => !string.IsNullOrWhiteSpace(p.Value.Text)).ToDictionary(p => p.Key, p => p.Value.Text!.Trim().ToUpperInvariant());
        if (entries.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Value, "^#[0-9A-F]{6}$")))
            throw new InvalidDataException("Цвет задаётся в формате #RRGGBB.");
        return new((preset.SelectedItem as Choice)?.Id ?? "light", entries);
    }

    void Preview()
    {
        try
        {
            ThemeManager.Apply(Preference());
            message.Text = "";
        }
        catch (InvalidDataException e)
        {
            message.Text = e.Message;
        }
    }

    async Task Refresh()
    {
        personal.ItemsSource = (await api.Get<ThemePreset[]>("theme-presets")).Select(p => new Saved(p)).ToArray();
    }

    async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)when (e is InvalidDataException or ApiException or HttpRequestException or TaskCanceledException)
        {
            message.Text = e.Message;
        }
    }

    sealed record Choice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    sealed record Saved(ThemePreset Preset)
    {
        public override string ToString() => Preset.Name;
    }
}
