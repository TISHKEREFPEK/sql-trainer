using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Classroom.Desktop;

public sealed partial class TeacherScreen
{
    enum Section { Lesson, Accounts, Assignments, Results, Settings }
    readonly Dictionary<Section, (Control Page, Button Button)> sections = new();
    readonly StackPanel routeList = new() { Spacing = 6 };
    readonly ContentControl toolbar = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock reviewCount = Ui.Text("—", 12);
    readonly Button reviewButton;
    readonly Button standButton;
    Section selectedSection = Section.Lesson;
    Func<Task>? openStand;
    bool changingResultPage;

    internal ContentControl Presentation { get; } = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };

    void AddSection(Section key, string label, string icon, Control page)
    {
        var button = GraphiteUi.NavigationButton(label, icon, () => owner.Safe(async () =>
        {
            ShowSection(key);
            if (key == Section.Results) await SelectResultPage(0);
        }), out _);
        sections.Add(key, (page, button));
        routeList.Children.Add(button);
    }

    Grid CreateTeacherLayout()
    {
        var canvas = new Grid
        {
            ColumnDefinitions = new("188,*"),
            RowDefinitions = new("Auto,*"),
            ColumnSpacing = 18,
            Margin = new Thickness(14)
        };
        var rail = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 16 };
        rail.Children.Add(GraphiteUi.ClassroomBrand(out _));
        var shortcuts = Ui.Stack(routeList, GraphiteUi.Separator(), reviewButton, standButton);
        shortcuts.Spacing = 8;
        var scroll = Ui.Scroll(shortcuts);
        Grid.SetRow(scroll, 1);
        rail.Children.Add(scroll);
        var role = Ui.Muted("Преподаватель", 13);
        Grid.SetRow(role, 2);
        rail.Children.Add(role);
        var railPanel = new Border { Child = rail };
        railPanel.Classes.Add("classroom-navigation");
        Grid.SetRowSpan(railPanel, 2);
        canvas.Children.Add(railPanel);
        Grid.SetColumn(toolbar, 1);
        canvas.Children.Add(toolbar);
        Grid.SetColumn(Presentation, 1);
        Grid.SetRow(Presentation, 1);
        canvas.Children.Add(Presentation);
        return canvas;
    }

    Button CreateReviewShortcut()
    {
        var button = GraphiteUi.NavigationButton("Проверка схем",
            "M4,2 H14 V16 H4 Z M7,6 H11 M7,10 L9,12 L13,8",
            () => owner.Safe(OpenReviewQueue), out _);
        var content = (Grid)button.Content!;
        content.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var badge = new Border
        {
            Child = reviewCount,
            Padding = new Thickness(6, 2),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center
        };
        Ui.BindBrush(badge, Border.BackgroundProperty, "SelectedTask");
        Grid.SetColumn(badge, 2);
        content.Children.Add(badge);
        return button;
    }

    void SetReviewCount(int count)
    {
        reviewCount.Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        AutomationProperties.SetName(reviewButton, $"Проверка схем. Ожидают: {count}");
        ToolTip.SetTip(reviewButton, $"Работ ожидают проверки: {count}");
    }

    void ShowSection(Section section)
    {
        owner.ClosePage(false);
        selectedSection = section;
        ShowSurface(sections[section].Page);
    }

    void ShowSurface(Control content)
    {
        if (!ReferenceEquals(Presentation.Content, content))
        {
            if (Presentation.Content is Electronics.ElectronicsScreen editor) editor.Suspend();
            Presentation.Content = content;
        }
        UpdateNavigationSelection();
    }

    void UpdateNavigationSelection()
    {
        foreach (var entry in sections.Values)
            entry.Button.Classes.Set("active", ReferenceEquals(Presentation.Content, entry.Page));
        reviewButton.Classes.Set("active", ReferenceEquals(Presentation.Content, reviewQueue));
        standButton.Classes.Set("active", Presentation.Content is Electronics.ElectronicsScreen);
    }

    async Task SelectResultPage(int index)
    {
        changingResultPage = true;
        try { resultPages.SelectedIndex = index; }
        finally { changingResultPage = false; }
        UpdateNavigationSelection();
        await RefreshResultPage(index);
    }

    Task RefreshResultPage(int index) => index switch
    {
        0 => learning.Refresh(),
        2 => electronicsResults.Load(),
        _ => Task.CompletedTask
    };

    internal void SetStandShortcut(Func<Task> action)
    {
        openStand = action;
        standButton.IsVisible = true;
    }

    internal void SetToolbar(Control? actions)
    {
        toolbar.Content = actions;
        toolbar.IsVisible = actions is not null;
        toolbar.Margin = actions is null ? default : new Thickness(0, 0, 0, 12);
    }

    internal void ShowStand(Electronics.ElectronicsScreen editor) => ShowSurface(editor);
    internal void ShowConnectionCode(Control content) => ShowSurface(content);
}
