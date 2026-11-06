using Avalonia.Controls;
using Avalonia.Layout;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class LessonTaskPicker : UserControl
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly TextBox search = Ui.Input();
    readonly StackPanel rows = Ui.Stack();
    readonly TextBlock count = Ui.Text("");
    readonly Button save;
    readonly HashSet<string> selected = [];
    readonly HashSet<string> requireNew = [];
    CatalogueItem[] catalog = [];
    LessonSqlTaskInfo[] issued = [];
    string? lessonId;
    bool saving;
    bool dirty;

    public LessonTaskPicker(MainWindow owner, ClassroomApi api)
    {
        this.owner = owner; this.api = api;
        search.Watermark = "Поиск SQL-задания";
        search.TextChanged += (_, _) => Render();
        save = Ui.Button("Сохранить выдачу", () => owner.Safe(async () =>
        {
            if (lessonId is not null) await Save(lessonId);
        }));
        Content = Ui.Card(Ui.Stack(Ui.Text("SQL-задания на занятие", 20),
            Ui.Text("Проекты выдаются целой цепочкой. Начатые работы сохраняют прежнюю редакцию."),
            search, new ScrollViewer { Content = rows, MaxHeight = 250, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            Ui.Row(count, save)));
    }
    public async Task Refresh(string? currentLessonId, CatalogueItem[]? tasks)
    {
        if (saving) return;
        var changed = lessonId != currentLessonId;
        var catalogueChanged = tasks is not null && !catalog.SequenceEqual(tasks);
        if (tasks is not null) catalog = tasks;
        if (changed)
        {
            lessonId = currentLessonId;
            issued = lessonId is null ? [] : (await api.Get<LessonSqlSelection>($"teacher/lessons/{lessonId}/sql-tasks")).Tasks;
            selected.Clear(); foreach (var task in issued) selected.Add(task.Id);
            requireNew.Clear(); foreach (var task in issued.Where(t => t.RequireNewSolution)) requireNew.Add(task.Id);
            dirty = false;
        }
        if (changed || catalogueChanged) Render();
    }
    public async Task Save(string id)
    {
        if (saving) return;
        saving = true; save.IsEnabled = false;
        try
        {
            var result = await api.Put<LessonSqlSelection>($"teacher/lessons/{id}/sql-tasks", new LessonSqlSelectionRequest(selected.ToArray(), requireNew.Where(selected.Contains).ToArray()));
            lessonId = id; issued = result.Tasks;
            selected.Clear(); foreach (var task in issued) selected.Add(task.Id);
            requireNew.Clear(); foreach (var task in issued.Where(t => t.RequireNewSolution)) requireNew.Add(task.Id);
            dirty = false;
            owner.Message("Выдача заданий сохранена.");
        }
        finally { saving = false; Render(); }
    }
    void Render()
    {
        rows.Children.Clear(); var query = search.Text?.Trim() ?? "";
        var choices = catalog.ToDictionary(t => t.Id);
        foreach (var task in issued) choices[task.Id] = new(task.Id, task.Title, task.Module, task.Project);
        foreach (var task in choices.Values.Where(t => (t.Title + " " + t.Module).Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var mode = new ComboBox { ItemsSource = new[] { "Зачесть прежнее", "Новое решение" },
                SelectedIndex = requireNew.Contains(task.Id) ? 1 : 0, MinWidth = 180, IsEnabled = selected.Contains(task.Id) };
            ToolTip.SetTip(mode, "Режим можно менять до первого открытия работы учениками. Для проекта применяется ко всей цепочке.");
            var check = new CheckBox { Content = new TextBlock { Text = task.Title + " · " + task.Module,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsChecked = selected.Contains(task.Id), HorizontalAlignment = HorizontalAlignment.Stretch };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) selected.Add(task.Id); else selected.Remove(task.Id);
                mode.IsEnabled = check.IsChecked == true; dirty = true;
                Count();
            };
            mode.SelectionChanged += (_, _) =>
            {
                var ids = task.Project is null ? new[] { task.Id } : choices.Values.Where(t => t.Project == task.Project).Select(t => t.Id).ToArray();
                foreach (var id in ids) { if (mode.SelectedIndex == 1) requireNew.Add(id); else requireNew.Remove(id); }
                dirty = true; Render();
            };
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(check); Grid.SetColumn(mode, 1); row.Children.Add(mode); rows.Children.Add(row);
        }
        if (rows.Children.Count == 0) rows.Children.Add(Ui.Text("Задания не найдены."));
        save.IsEnabled = lessonId is not null && !saving; Count();
    }
    void Count() => count.Text = "Выбрано: " + selected.Count + (lessonId is null ? " · выдадутся при начале занятия" : dirty ? " · не сохранено" : "");
}
