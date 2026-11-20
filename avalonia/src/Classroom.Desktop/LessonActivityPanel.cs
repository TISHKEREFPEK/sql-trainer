using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Classroom.Contracts;

namespace Classroom.Desktop;

internal sealed class LessonActivityPanel : UserControl
{
    readonly StackPanel rows = Ui.Stack();
    readonly TextBlock message = Ui.Muted("");
    readonly Func<LessonActivityItem, Task> openWork;
    LessonActivityItem[] items = [];

    public LessonActivityPanel(Func<LessonActivityItem, Task> openWork)
    {
        this.openWork = openWork;
        Content = GraphiteUi.Panel(Ui.Stack(Ui.Text("Последняя активность", 20), message, rows));
        rows.Spacing = 2;
        IsVisible = false;
    }

    internal void SwitchLesson(bool started)
    {
        IsVisible = started;
        items = [];
        rows.Children.Clear();
        Message("Загружаем активность…");
    }

    internal void Show(LessonActivity feed)
    {
        Message(feed.Items.Length == 0 ? "Работы на этом занятии пока не открывали." : null);
        if (items.SequenceEqual(feed.Items)) return;
        items = feed.Items;
        rows.Children.Clear();
        foreach (var item in items)
        {
            if (rows.Children.Count > 0) rows.Children.Add(GraphiteUi.Separator());
            rows.Children.Add(WorkRow(item));
        }
    }

    internal void ShowError() => Message("Не удалось обновить активность занятия.");

    void Message(string? text)
    {
        message.Text = text;
        message.IsVisible = text is not null;
    }

    Button WorkRow(LessonActivityItem item)
    {
        var action = item.Action switch
        {
            "working" => "В работе",
            "solved" => "Решено",
            "opened" => "Схема открыта",
            "saved" => "Схема сохранена",
            "submitted" => "Схема сдана",
            "accepted" => "Принято",
            "changes-requested" => "На доработку",
            "rejected" => "Не принято",
            _ => "Работа обновлена"
        };
        var time = Ui.Time(item.At);
        var who = Ui.Stack(Ui.Text(item.StudentName), Ui.Muted(item.GroupName, 12));
        var what = Ui.Stack(Ui.Text(item.Title), Ui.Muted(item.Subject == "sql" ? "SQL" : "Электроника", 12));
        var when = Ui.Stack(Ui.Text(action, 13), Ui.Muted(time, 12));
        who.Spacing = what.Spacing = when.Spacing = 0;
        var content = new Grid
        {
            ColumnDefinitions = new("*,1.3*,Auto,Auto"),
            RowDefinitions = new("Auto,0"),
            ColumnSpacing = 12
        };
        content.Children.Add(who);
        Grid.SetColumn(what, 1);
        content.Children.Add(what);
        Grid.SetColumn(when, 2);
        content.Children.Add(when);
        var indicator = GraphiteUi.NavigationIcon("M6,3 L12,9 L6,15");
        indicator.Width = 10;
        indicator.Height = 14;
        Grid.SetColumn(indicator, 3);
        content.Children.Add(indicator);
        bool? compact = null;
        content.SizeChanged += (_, _) =>
        {
            var narrow = content.Bounds.Width < 650;
            if (compact == narrow) return;
            compact = narrow;
            content.ColumnDefinitions[1].Width = narrow ? new(0) : new(1.3, GridUnitType.Star);
            content.RowDefinitions[1].Height = narrow ? GridLength.Auto : new(0);
            content.RowSpacing = narrow ? 5 : 0;
            Grid.SetRow(what, narrow ? 1 : 0);
            Grid.SetColumn(what, narrow ? 0 : 1);
            Grid.SetColumnSpan(what, narrow ? 4 : 1);
        };
        var button = Ui.Button("", () => openWork(item));
        button.Content = content;
        button.Classes.Add("lesson-activity-row");
        button.Padding = new Thickness(8, 5);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, $"{item.StudentName}, {item.Title}, {action}, {time}. Открыть работу");
        return button;
    }
}
