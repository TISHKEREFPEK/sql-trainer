using Avalonia.Controls;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class LessonElectronicsPicker : UserControl
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly StackPanel rows = Ui.Stack();
    readonly Button save;
    readonly TextBlock count = Ui.Text("");
    readonly HashSet<string> selected = [];
    ElectronicsTaskSummary[] catalog = [];
    ElectronicsLessonTask[] issued = [];
    string? lessonId;
    bool saving, dirty;
    public LessonElectronicsPicker(MainWindow owner, ClassroomApi api)
    {
        this.owner = owner; this.api = api;
        save = Ui.Button("Сохранить выдачу схем", () => owner.Safe(async () => { if (lessonId is not null) await Save(lessonId); }));
        Content = Ui.Card(Ui.Stack(Ui.Text("Электронные задания на занятие", 20),
            Ui.Text("Создайте условия и начальные схемы в разделе «Задания → Электроника»."),
            new ScrollViewer { Content = rows, MaxHeight = 220 }, Ui.Row(count, save)));
    }
    public async Task Refresh(string? id)
    {
        if (saving) return;
        var data = await api.Get<ElectronicsTaskSummary[]>("teacher/electronics/tasks");
        var changed = lessonId != id || !data.SequenceEqual(catalog);
        catalog = data;
        if (lessonId != id)
        {
            lessonId = id; issued = id is null ? [] : (await api.Get<ElectronicsSelection>($"teacher/lessons/{id}/electronics-tasks")).Tasks;
            selected.Clear(); foreach (var task in issued) selected.Add(task.Id); dirty = false;
        }
        if (changed || rows.Children.Count == 0) Render();
    }
    public async Task Save(string id)
    {
        if (saving) return;
        saving = true; save.IsEnabled = false;
        try
        {
            var result = await api.Put<ElectronicsSelection>($"teacher/lessons/{id}/electronics-tasks", new ElectronicsSelectionRequest(selected.ToArray()));
            lessonId = id; issued = result.Tasks; selected.Clear(); foreach (var task in issued) selected.Add(task.Id);
            dirty = false; owner.Message("Электронные задания выданы.");
        }
        finally { saving = false; Render(); }
    }
    void Render()
    {
        rows.Children.Clear();
        var choices = catalog.ToDictionary(t => t.Id, t => t.Title); foreach (var task in issued) choices[task.Id] = task.Title;
        foreach (var task in choices)
        {
            var check = new CheckBox { Content = task.Value, IsChecked = selected.Contains(task.Key) };
            check.IsCheckedChanged += (_, _) => { if (check.IsChecked == true) selected.Add(task.Key); else selected.Remove(task.Key); dirty = true; Count(); };
            rows.Children.Add(check);
        }
        if (choices.Count == 0) rows.Children.Add(Ui.Text("Электронных заданий пока нет."));
        save.IsEnabled = lessonId is not null && !saving; Count();
    }
    void Count() => count.Text = "Выбрано: " + selected.Count + (lessonId is null ? " · выдадутся при начале занятия" : dirty ? " · не сохранено" : "");
}
