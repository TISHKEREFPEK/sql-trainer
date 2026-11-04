using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;

namespace Classroom.Desktop;

public sealed class ElectronicsTaskAuthoring : UserControl
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly ListBox list = new();
    readonly TextBox title = Ui.Input();
    readonly TextBox prompt = Ui.Input(null, true);
    readonly TextBlock starterInfo = Ui.Text("Начальная схема не приложена. Ученик начнёт с пустой схемы.");
    readonly TextBlock state = Ui.Text("");
    readonly Button save;
    string? id, starter;
    int revision;
    bool restricted, loading, dirty;

    public ElectronicsTaskAuthoring(MainWindow owner, ClassroomApi api)
    {
        this.owner = owner; this.api = api;
        title.MaxLength = 120; prompt.MaxLength = 32000; prompt.MinHeight = 180;
        void Changed() { if (!loading) { dirty = true; state.Text = "Есть несохранённые изменения"; } }
        title.TextChanged += (_, _) => Changed(); prompt.TextChanged += (_, _) => Changed();
        list.SelectionChanged += async (_, _) =>
        {
            if (loading || list.SelectedItem is not Choice choice || choice.Id == id) return;
            await owner.Safe(async () =>
            {
                if (dirty && !await owner.Confirm("Открыть другое задание и отменить несохранённые изменения?")) return;
                var task = await api.Get<ElectronicsTaskDto>("teacher/electronics/tasks/" + choice.Id);
                loading = true; id = task.Id; revision = task.Revision; restricted = task.Restricted;
                title.Text = task.Title; prompt.Text = task.Prompt; starter = task.StarterJson;
                loading = false; dirty = false; state.Text = "Редакция " + revision; Starter();
            });
        };
        save = Ui.Primary(Ui.Button("Сохранить задание", () => owner.Safe(async () =>
        {
            save!.IsEnabled = false;
            try
            {
                id ??= Guid.NewGuid().ToString("N");
                var task = await api.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + id,
                    new ElectronicsTaskRequest(title.Text ?? "", prompt.Text ?? "", starter, restricted, revision));
                revision = task.Revision; dirty = false; state.Text = "Сохранено · редакция " + revision;
                await Refresh();
            }
            finally { save.IsEnabled = true; }
        })));
        var create = Ui.Button("Новое задание", () => owner.Safe(async () =>
        {
            if (dirty && !await owner.Confirm("Создать новое задание и отменить несохранённые изменения?")) return;
            loading = true; id = Guid.NewGuid().ToString("N"); revision = 0; title.Text = ""; prompt.Text = ""; starter = null;
            restricted = false; list.SelectedItem = null; loading = false; dirty = false; state.Text = "Новое задание"; Starter();
        }));
        var attach = Ui.Button("Приложить схему и код", () => owner.Safe(async () =>
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Начальная схема с кодом", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Схема приложения") { Patterns = ["*.json"] }] });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync(); using var reader = new StreamReader(stream);
            var buffer = new char[2_000_001]; var length = await reader.ReadBlockAsync(buffer);
            if (length > 2_000_000) throw new InvalidDataException("Начальная схема превышает 2 МБ текста.");
            var json = new string(buffer, 0, length); CircuitProject.Parse(json); ElectronicsProjectFormat.Inspect(json);
            starter = json; dirty = true; state.Text = "Есть несохранённые изменения"; Starter();
        }));
        var remove = Ui.Button("Убрать начальную схему", () => { starter = null; dirty = true; state.Text = "Есть несохранённые изменения"; Starter(); return Task.CompletedTask; });
        var delete = Ui.Button("Убрать задание из каталога", () => owner.Safe(async () =>
        {
            if (id is null || revision == 0 || !await owner.Confirm("Убрать это задание из каталога? Прежние выдачи и ученические работы сохранятся.")) return;
            await api.Delete<System.Text.Json.JsonElement>("teacher/electronics/tasks/" + id);
            loading = true; id = null; revision = 0; title.Text = ""; prompt.Text = ""; starter = null; loading = false; dirty = false;
            state.Text = "Задание убрано из каталога"; Starter(); await Refresh();
        }));
        var navigation = Ui.Stack(Ui.Text("Электронные задания", 20), create, Ui.Button("Обновить список", () => owner.Safe(Refresh)), list);
        var form = Ui.Stack(Ui.Field("Название", title), Ui.Field("Условие", prompt), starterInfo, Ui.Row(attach, remove),
            Ui.Text("Изменение задания не заменяет редакцию, уже выданную на занятии."), state, Ui.Row(save, delete));
        var grid = new Grid { ColumnDefinitions = new("230,*"), ColumnSpacing = 12 };
        grid.Children.Add(Ui.Card(navigation)); var card = Ui.Card(Ui.Scroll(form)); Grid.SetColumn(card, 1); grid.Children.Add(card); Content = grid;
    }
    void Starter()
    {
        if (starter is null) { starterInfo.Text = "Начальная схема не приложена. Ученик начнёт с пустой схемы."; return; }
        var project = CircuitProject.Parse(starter);
        starterInfo.Text = $"Начальная схема: {project.Name} · деталей: {project.Parts.Count} · код приложен";
    }
    public async Task Refresh()
    {
        var rows = await api.Get<ElectronicsTaskSummary[]>("teacher/electronics/tasks");
        loading = true;
        list.ItemsSource = rows.Select(t => new Choice(t.Id, t.Title)).ToArray();
        list.SelectedItem = list.Items.OfType<Choice>().FirstOrDefault(t => t.Id == id);
        loading = false;
    }
    sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
}
