using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class SqlJournalView : UserControl, IDisposable
{
    sealed record StreamChoice(SqlJournalStreamInfo Stream)
    {
        public override string ToString() => $"{Ui.Time(Stream.StartedAt)} · {(Stream.WorkScope == "course" ? "Практика курса" : "Работа занятия")}" +
            (Stream.ForkedFrom is null ? "" : " · восстановленная копия");
    }
    sealed record StepChoice(long Sequence, SqlJournalEvent? Event)
    {
        public override string ToString() => Event is { } e ? $"{Sequence}. {Kind(e.Change.Kind)}\n{Ui.Time(e.Change.At)}" : "0. Исходный текст";
    }
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly string studentId, taskId;
    readonly CancellationTokenSource lifetime = new();
    readonly CancellationToken cancellation;
    readonly ComboBox streams = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ListBox steps = new() { MinHeight = 120 };
    readonly List<StreamChoice> choices = new();
    readonly List<StepChoice> events = new();
    readonly TextEditor editor = new() { IsReadOnly = true, ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 15 };
    readonly TextBlock info = Ui.Text("Выберите запись истории.", 12), status = Ui.Text("", 12);
    readonly TextBox removed = Code(), inserted = Code();
    readonly Expander difference = new() { Header = "Изменение текста", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly Button moreStreams, moreSteps;
    SqlJournalStreamInfo? selected;
    long generation, selection;
    bool binding, disposed;
    static TextBox Code() => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 45, MaxHeight = 110, FontFamily = new("Cascadia Code, Consolas") };
    static string Kind(string value) => value switch { "edit" => "Правка", "observe" => "Обновление из сервера", "conflict-local" => "Выбран локальный текст", "conflict-server" => "Выбран серверный текст", "backup-restore" => "Восстановление копии", _ => value };

    public SqlJournalView(MainWindow owner, ClassroomApi api, string studentId, string taskId, string title)
    {
        this.owner = owner; this.api = api; this.studentId = studentId; this.taskId = taskId; cancellation = lifetime.Token;
        moreStreams = Ui.Button("Ещё записи", () => Guard(LoadMoreStreams)); moreStreams.IsVisible = false;
        moreSteps = Ui.Button("Ещё шаги", () => Guard(LoadMoreSteps)); moreSteps.IsVisible = false;
        streams.SelectionChanged += async (_, _) => { if (!binding && streams.SelectedItem is StreamChoice choice) await Guard(() => Open(choice.Stream)); };
        steps.SelectionChanged += async (_, _) => { if (!binding && steps.SelectedItem is StepChoice step) await Guard(() => Select(step)); };
        Ui.BindBrush(editor, TextEditor.BackgroundProperty, "Editor"); Ui.BindBrush(editor, TextEditor.ForegroundProperty, "EditorInk"); editor.Padding = new Avalonia.Thickness(12);
        AttachedToVisualTree += (_, _) => { ThemeManager.Changed += ApplyTheme; ApplyTheme(); };
        DetachedFromVisualTree += (_, _) => ThemeManager.Changed -= ApplyTheme;
        var comparison = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        comparison.Children.Add(Ui.Field("Удалено", removed)); var added = Ui.Field("Добавлено", inserted); Grid.SetColumn(added, 1); comparison.Children.Add(added); difference.Content = comparison;
        var left = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 8 }; left.Children.Add(steps); Grid.SetRow(moreSteps, 1); left.Children.Add(moreSteps);
        var right = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8 }; right.Children.Add(info); Grid.SetRow(difference, 1); right.Children.Add(difference); Grid.SetRow(editor, 2); right.Children.Add(editor);
        var split = new Grid { ColumnDefinitions = new("220,8,*") }; split.Children.Add(left); Grid.SetColumn(right, 2); split.Children.Add(right);
        var picker = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 }; picker.Children.Add(streams); Grid.SetColumn(moreStreams, 1); picker.Children.Add(moreStreams);
        var refresh = Ui.Button("Обновить", () => Guard(Load)); Grid.SetColumn(refresh, 2); picker.Children.Add(refresh);
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 10, Margin = new Avalonia.Thickness(8) };
        root.Children.Add(Ui.Stack(Ui.Text(title + " · история SQL", 20), Ui.Muted("Выберите запись и шаг. Даты действий записаны на ПК ученика; время получения — на сервере преподавателя.")));
        Grid.SetRow(picker, 1); root.Children.Add(picker); Grid.SetRow(status, 2); root.Children.Add(status); Grid.SetRow(split, 3); root.Children.Add(split); Content = root;
    }
    string Prefix => $"teacher/sql-journal/{Uri.EscapeDataString(studentId)}";
    string StreamPrefix => Prefix + "/" + Uri.EscapeDataString(selected!.Id);
    async Task Guard(Func<Task> action)
    {
        if (disposed) return;
        await owner.Safe(async () => { try { await action(); } catch (OperationCanceledException) when (disposed) { } });
    }
    public async Task Load()
    {
        if (disposed) return;
        try { await LoadCore(); } catch (OperationCanceledException) when (disposed) { }
    }
    async Task LoadCore()
    {
        var stamp = ++generation; selection++; selected = null; choices.Clear(); events.Clear(); editor.Text = ""; difference.IsVisible = false; info.Text = "Загрузка истории…";
        moreSteps.IsVisible = moreStreams.IsVisible = false;
        binding = true; streams.ItemsSource = Array.Empty<StreamChoice>(); steps.ItemsSource = Array.Empty<StepChoice>(); binding = false;
        await LoadMoreStreams();
        if (!disposed && generation == stamp && choices.Count > 0)
        {
            binding = true; streams.SelectedIndex = 0; binding = false;
            await Open(choices[0].Stream);
        }
    }
    async Task LoadMoreStreams()
    {
        var stamp = generation;
        var page = await api.Get<SqlJournalStreamInfo[]>(Prefix + $"?taskId={Uri.EscapeDataString(taskId)}&offset={choices.Count}", cancellation);
        if (disposed || stamp != generation) return;
        choices.AddRange(page.Select(s => new StreamChoice(s)));
        binding = true; var previous = streams.SelectedItem; streams.ItemsSource = choices.ToArray(); streams.SelectedItem = previous; binding = false;
        moreStreams.IsVisible = page.Length == 30;
        status.Text = choices.Count == 0 ? "Пошаговая история ещё не получена. Старые правки, которые не записывались, восстановить нельзя." : "Отображаются полученные сервером шаги. Несинхронизированные правки появятся после подключения ученика.";
    }
    async Task Open(SqlJournalStreamInfo stream)
    {
        selected = stream; var stamp = ++generation; selection++; moreSteps.IsVisible = false;
        events.Clear(); events.Add(new(0, null)); editor.Text = ""; info.Text = "Загрузка истории…"; difference.IsVisible = false;
        binding = true; steps.ItemsSource = events.ToArray(); steps.SelectedIndex = -1; binding = false;
        await LoadMoreSteps();
        if (disposed || stamp != generation || selected?.Id != stream.Id) return;
        binding = true; steps.SelectedIndex = 0; binding = false; await Select(events[0]);
    }
    async Task LoadMoreSteps()
    {
        if (disposed || selected is null) return;
        var stamp = generation; var after = events.LastOrDefault()?.Sequence ?? 0;
        var page = await api.Get<SqlJournalEvents>(StreamPrefix + "/changes?after=" + after, cancellation);
        if (disposed || stamp != generation) return;
        events.AddRange(page.Events.Select(e => new StepChoice(e.Change.Sequence, e)));
        binding = true; var previous = steps.SelectedItem; steps.ItemsSource = events.ToArray(); steps.SelectedItem = previous; binding = false;
        moreSteps.IsVisible = page.HasMore;
    }
    async Task Select(StepChoice step)
    {
        if (disposed || selected is null) return;
        var stamp = ++selection; var source = selected!; var prefix = StreamPrefix;
        editor.Text = ""; difference.IsVisible = false; info.Text = $"Загрузка шага {step.Sequence}…";
        var state = await api.Get<SqlJournalText>(prefix + "/state?sequence=" + step.Sequence, cancellation);
        SqlJournalText? before = step.Event is null ? null : await api.Get<SqlJournalText>(prefix + "/state?sequence=" + (step.Sequence - 1), cancellation);
        if (disposed || stamp != selection || selected?.Id != source.Id) return;
        editor.Text = state.Code; editor.CaretOffset = 0; difference.IsVisible = step.Event is not null;
        info.Text = step.Event is { } item ? $"Шаг {step.Sequence} · {Kind(item.Change.Kind)}\nНа ПК ученика: {Ui.Time(item.Change.At)} · получено: {Ui.Time(item.ReceivedAt)}" :
            $"Исходный текст · начало записи на ПК ученика: {Ui.Time(source.StartedAt)}\nПолучено: {Ui.Time(source.ReceivedAt)}";
        if (step.Event is { } e)
        {
            removed.Text = before!.Code.Substring(e.Change.Offset, e.Change.Removed); inserted.Text = e.Change.Inserted;
        }
    }
    void ApplyTheme()
    { if (editor.Background is SolidColorBrush brush) editor.SyntaxHighlighting = SqlHighlighting.Create(ThemeManager.Ink(brush.Color.ToString())); }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); lifetime.Dispose(); ThemeManager.Changed -= ApplyTheme; }
}
