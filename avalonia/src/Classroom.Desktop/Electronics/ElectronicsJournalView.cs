using Avalonia.Controls;
using Avalonia.Layout;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed class ElectronicsJournalView : UserControl, IDisposable
{
    sealed record StreamChoice(ElectronicsJournalInfo Stream)
    { public override string ToString() => $"{Ui.Time(Stream.StartedAt)} · {Stream.Title}" + (Stream.ForkedFrom is null ? "" : " · восстановленная копия"); }
    sealed record StepChoice(long Sequence, ElectronicsJournalEvent? Event)
    { public override string ToString() => Event is { } e ? $"{Sequence}. {Label(e.Change.Kind)}\n{Ui.Time(e.Change.At)}" : "0. Исходная схема"; }
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly string studentId;
    readonly CancellationTokenSource lifetime = new();
    readonly CancellationToken cancellation;
    readonly ComboBox streams = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ListBox steps = new();
    readonly ContentControl preview = new();
    readonly TextBlock info = Ui.Text("", 12), status = Ui.Text("", 12);
    readonly TextBox serialDetail = new(){IsReadOnly=true,AcceptsReturn=true,IsVisible=false,MinHeight=45,MaxHeight=140};
    readonly Button moreStreams, moreSteps, reportButton;
    readonly List<StreamChoice> choices = new();
    readonly List<StepChoice> events = new();
    readonly Control root;
    ElectronicsJournalInfo? selected;
    string? reportId;
    long generation, selection;
    bool binding, disposed;
    static string Label(string kind) => kind switch {
        "code-edit" => "Правка кода", "code-undo" => "Отмена кода", "code-redo" => "Повтор кода", "rename" => "Название", "scheme-edit" => "Изменение схемы",
        "move-start" => "Начало перемещения", "move" => "Перемещение", "move-end" or "move-capture-lost" => "Конец перемещения", "resize-start" => "Начало изменения размера", "resize" => "Размер", "resize-end" => "Размер изменён",
        "rotate-start" => "Начало поворота", "rotate" => "Поворот", "rotate-end" => "Поворот завершён", "gesture-cancel" => "Отмена жеста", "undo" => "Отмена изменения", "redo" => "Повтор изменения",
        "work-open" => "Открыта работа", "work-leave" => "Переход из работы", "work-close" => "Работа закрыта", "window-focus" => "Возврат в окно", "window-blur" => "Переход в другое окно", "window-minimize" => "Окно свёрнуто", "window-restore" => "Окно восстановлено",
        "serial-input-request"=>"Запрошен ввод в порт","serial-input"=>"Ввод принят моделью порта","serial-output"=>"Вывод последовательного порта","serial-clear"=>"Очищен экран порта","serial-channel"=>"Выбран последовательный порт",
        "i2c-transfer"=>"Обмен по I2C","i2c-address"=>"Адрес I2C",
        "run-request" => "Запрошен запуск", "compile-request" => "Запрошена компиляция", "simulation-stop" => "Симуляция остановлена", "report-submitted" => "Намерение сдачи", "report-received" => "Получен ответ сервера",
        "report-compiled" => "Сообщена компиляция", "report-started" => "Сообщён запуск", "report-compile-error" => "Сообщена ошибка компиляции", "report-run-error" => "Сообщена ошибка запуска", "control-change" => "Регулятор", "part-add" => "Добавлена деталь", "project-replace" => "Замена схемы", "code-template" => "Шаблон кода", _ => kind };
    readonly string? assignmentId, lessonId;
    public ElectronicsJournalView(MainWindow owner, ClassroomApi api, string studentId, string studentName, string? assignmentId = null, string? lessonId = null)
    {
        this.assignmentId = assignmentId; this.lessonId = lessonId;
        this.owner = owner; this.api = api; this.studentId = studentId; cancellation = lifetime.Token;
        moreStreams = Ui.Button("Ещё записи", () => Guard(LoadMoreStreams)); moreSteps = Ui.Button("Ещё шаги", () => Guard(LoadMoreSteps));
        reportButton = Ui.Button("Открыть связанный снимок", () => Guard(OpenReport)); reportButton.IsVisible = false;
        steps.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<StepChoice>((step, _) => new TextBlock { Text = step?.ToString(), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 190, FontSize = 13, Margin = new Avalonia.Thickness(0, 3) });
        streams.SelectionChanged += async (_, _) => { if (!binding && streams.SelectedItem is StreamChoice choice) await Guard(() => Open(choice.Stream)); };
        steps.SelectionChanged += async (_, _) => { if (!binding && steps.SelectedItem is StepChoice step) await Guard(() => Select(step)); };
        var picker = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 }; picker.Children.Add(streams); Grid.SetColumn(moreStreams, 1); picker.Children.Add(moreStreams); var refresh = Ui.Button("Обновить", () => Guard(Load)); Grid.SetColumn(refresh, 2); picker.Children.Add(refresh);
        var left = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 8 }; left.Children.Add(steps); Grid.SetRow(moreSteps, 1); left.Children.Add(moreSteps);
        var right = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 8 }; right.Children.Add(info); Grid.SetRow(serialDetail,1);right.Children.Add(serialDetail);Grid.SetRow(reportButton, 2); right.Children.Add(reportButton); Grid.SetRow(preview, 3); right.Children.Add(preview);
        var split = new Grid { ColumnDefinitions = new("220,8,*") }; split.Children.Add(left); Grid.SetColumn(right, 2); split.Children.Add(right);
        var page = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 10, Margin = new Avalonia.Thickness(8) };
        page.Children.Add(Ui.Text(studentName + " · история электроники", 20)); Grid.SetRow(picker, 1); page.Children.Add(picker); Grid.SetRow(status, 2); page.Children.Add(status); Grid.SetRow(split, 3); page.Children.Add(split); Content = root = page;
    }
    string Prefix => "teacher/electronics-journal/" + Uri.EscapeDataString(studentId);
    string StreamPrefix => Prefix + "/" + Uri.EscapeDataString(selected!.Id);
    async Task Guard(Func<Task> action)
    { if (!disposed) await owner.Safe(async () => { try { await action(); } catch (OperationCanceledException) when (disposed) { } }); }
    public async Task Load()
    {
        if (disposed) return;
        try
        {
            var stamp = ++generation; selection++; selected = null; choices.Clear(); events.Clear(); info.Text = "Загрузка истории…"; preview.Content = null; moreSteps.IsVisible = moreStreams.IsVisible = reportButton.IsVisible = false;
            binding = true; streams.ItemsSource = Array.Empty<StreamChoice>(); steps.ItemsSource = Array.Empty<StepChoice>(); binding = false;
            await LoadMoreStreams();
            if (!disposed && generation == stamp && choices.Count > 0) { binding = true; streams.SelectedIndex = 0; binding = false; await Open(choices[0].Stream); }
        }
        catch (OperationCanceledException) when (disposed) { }
    }
    async Task LoadMoreStreams()
    {
        var stamp = generation; var page = await api.Get<ElectronicsJournalInfo[]>(Prefix + "?offset=" + choices.Count + (assignmentId is null ? "" : "&assignmentId=" + Uri.EscapeDataString(assignmentId)) + (lessonId is null ? "" : "&lessonId=" + Uri.EscapeDataString(lessonId)), cancellation); if (disposed || stamp != generation) return;
        choices.AddRange(page.Select(s => new StreamChoice(s))); binding = true; var previous = streams.SelectedItem; streams.ItemsSource = choices.ToArray(); streams.SelectedItem = previous; binding = false;
        if (choices.Count == 0) info.Text = "";
        moreStreams.IsVisible = page.Length == 30; status.Text = choices.Count == 0 ? "История ещё не получена. Прежние незаписанные шаги не восстанавливаются." : "Время действия — по ПК ученика; получение — по серверу преподавателя. Новые шаги появятся после передачи и обновления.";
    }
    async Task Open(ElectronicsJournalInfo stream)
    {
        selected = stream; var stamp = ++generation; selection++; moreSteps.IsVisible = reportButton.IsVisible = false; events.Clear(); events.Add(new(0, null)); preview.Content = null;
        binding = true; steps.ItemsSource = events.ToArray(); steps.SelectedIndex = -1; binding = false; await LoadMoreSteps();
        if (disposed || stamp != generation) return; binding = true; steps.SelectedIndex = 0; binding = false; await Select(events[0]);
    }
    async Task LoadMoreSteps()
    {
        if (disposed || selected is null) return; var stamp = generation; var page = await api.Get<ElectronicsJournalEvents>(StreamPrefix + "/changes?after=" + (events.LastOrDefault()?.Sequence ?? 0), cancellation);
        if (disposed || stamp != generation) return; events.AddRange(page.Events.Select(e => new StepChoice(e.Change.Sequence, e)));
        binding = true; var previous = steps.SelectedItem; steps.ItemsSource = events.ToArray(); steps.SelectedItem = previous; binding = false; moreSteps.IsVisible = page.HasMore;
    }
    async Task Select(StepChoice step)
    {
        if (disposed || selected is null) return; var stamp = ++selection; var source = selected; var prefix = StreamPrefix; reportId = null; reportButton.IsVisible = false; info.Text = "Загрузка шага…"; preview.Content = null;
        var state = await api.Get<ElectronicsJournalState>(prefix + "/state?sequence=" + step.Sequence, cancellation);
        var link = step.Event?.Change.ReportId is null ? null : await api.Get<ElectronicsJournalReportLink>(prefix + "/changes/" + step.Sequence + "/report", cancellation);
        if (disposed || stamp != selection || selected?.Id != source.Id) return;
        info.Text = step.Event is { } e ? $"Шаг {step.Sequence} · {Label(e.Change.Kind)}\nНа ПК ученика: {Ui.Time(e.Change.At)} · получено: {Ui.Time(e.ReceivedAt)}" : "Исходная схема · начало записи на ПК ученика: " + Ui.Time(source.StartedAt);
        if (step.Event?.Change.Kind.StartsWith("window-", StringComparison.Ordinal) == true) info.Text += "\nСобытие окна не является доказательством списывания.";
        serialDetail.IsVisible=step.Event?.Change.SerialIo is not null||step.Event?.Change.I2cIo is not null;serialDetail.Text=step.Event?.Change.SerialIo?.Text??"";
        if(step.Event?.Change.I2cIo is { } transfers)serialDetail.Text=string.Join("\n",transfers.Select(t=>$"0x{t.Address:X2} · {(t.Read?"чтение":"запись")} · {(t.Ack?"адрес подтверждён":"нет ответа")}{(t.RepeatedStart?" · повторный START":"")}\nДанные (hex): "+string.Join(" ",t.Data.Select(b=>b.ToString("X2")))));
        if(step.Event?.Change.SerialIo is { }io)info.Text+=$"\n{(io.Port==0?"Serial":"Serial"+io.Port)}";
        if (link is not null) info.Text += link.Availability switch { "available" => "\nСвязанный отчёт подтверждён для этой копии задания.", "legacy-context" => "\nСнимок и копия сверены; в старом свободном отчёте занятие не сохранялось.", "mismatch" => "\nСсылка не совпадает с копией или снимком. Другой отчёт не подставлен.", _ => "\nОтчёт пока не найден; намерение само по себе не подтверждает сдачу." };
        reportId = link?.Availability is "available" or "legacy-context" ? link.ReportId : null; reportButton.IsVisible = reportId is not null;
        preview.Content = new ElectronicsSnapshotView(new(state.ProjectJson, "history", step.Event?.ReceivedAt ?? source.ReceivedAt, ""));
    }
    async Task OpenReport()
    {
        if (reportId is not { } id) return; var stamp = selection; var snapshot = await api.Get<ElectronicsSnapshot>($"teacher/electronics/{Uri.EscapeDataString(studentId)}/report/{Uri.EscapeDataString(id)}", cancellation); if (disposed || stamp != selection) return;
        var page = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 10 }; page.Children.Add(Ui.Button("К истории электроники", () => { Content = root; return Task.CompletedTask; }));
        var view = new ElectronicsSnapshotView(snapshot); Grid.SetRow(view, 1); page.Children.Add(view); Content = page;
    }
    public void Dispose() { if (disposed) return; disposed = true; generation++; selection++; lifetime.Cancel(); lifetime.Dispose(); }
}
