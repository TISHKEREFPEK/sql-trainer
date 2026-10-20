using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class LearningStatisticsView : UserControl
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly StackPanel page = Ui.Stack();
    readonly ScrollViewer scroll;
    LearningOverview? data;
    string? groupId, studentId, taskId;
    LearningTaskDetail? detail;
    readonly List<LearningEvent> history = new();
    long generation;
    bool loading;

    public LearningStatisticsView(MainWindow owner, ClassroomApi api)
    {
        this.owner = owner;
        this.api = api;
        scroll = Ui.Scroll(page);
        Content = scroll;
    }

    public async Task Refresh()
    {
        if (loading) return;
        loading = true;
        var stamp = ++generation;
        try
        {
            var update = await api.Get<LearningOverview>("teacher/learning");
            if (stamp != generation) return;
            data = update;
            if (studentId is not null && !data.Students.Any(s => s.Id == studentId))
            { studentId = taskId = null; }
            if (taskId is not null && studentId is not null)
                await OpenTask(taskId);
            else Render();
        }
        finally { loading = false; }
    }

    static string Time(long? at) => at is { } value ? Ui.Time(value) : "Не записывалось";
    static string Duration(int? seconds) => seconds is null ? "Не записывалось" : seconds < 60 ? $"{seconds} с" : seconds < 3600 ? $"{seconds / 60} мин" : $"{seconds / 3600} ч {seconds % 3600 / 60} мин";
    static double Progress(LearningStudent s) => s.Total == 0 ? 0 : 100d * s.Completed / s.Total;
    LearningStudent[] Members() => data!.Students.Where(s => groupId == "*" || (groupId == "" ? s.GroupId is null : s.GroupId == groupId)).OrderBy(s => s.Login).ToArray();
    string GroupName() => groupId == "*" ? "Все ученики" : groupId == "" ? "Без группы" : data!.Groups.FirstOrDefault(g => g.Id == groupId)?.Name ?? "Группа удалена";
    LearningRecord[] Records(IEnumerable<LearningStudent> students)
    {
        var ids = students.Select(s => s.Id).ToHashSet();
        return data!.Records.Where(r => ids.Contains(r.StudentId)).ToArray();
    }

    void Header(string title)
    {
        page.Children.Clear();
        var navigation = Ui.Row(Ui.Button("Все группы", () => { groupId = studentId = taskId = null; generation++; Render(); return Task.CompletedTask; }));
        if (groupId is not null)
            navigation.Children.Add(Ui.Button(GroupName(), () => { studentId = taskId = null; generation++; Render(); return Task.CompletedTask; }));
        if (studentId is not null)
            navigation.Children.Add(Ui.Button(data!.Students.First(s => s.Id == studentId).Login, () => { taskId = null; generation++; Render(); return Task.CompletedTask; }));
        navigation.Children.Add(Ui.Button("Обновить", () => owner.Safe(Refresh)));
        page.Children.Add(navigation);
        page.Children.Add(Ui.Text(title, 24));
        page.Children.Add(Ui.Text($"Обновлено: {Time(data!.GeneratedAt)} · время компьютера преподавателя ({TimeZoneInfo.Local.StandardName})", 12));
    }

    void Render()
    {
        if (data is null) return;
        if (studentId is not null) { RenderStudent(); return; }
        if (groupId is not null) { RenderGroup(); return; }
        Header("Группы");
        var rows = data.Groups.OrderBy(g => g.Name).Select(g => GroupRow(g.Id, g.Name)).ToList();
        if (data.Students.Any(s => s.GroupId is null)) rows.Add(GroupRow("", "Без группы"));
        rows.Insert(0, GroupRow("*", "Все ученики"));
        var grid = Grid(rows, [("Группа", "Name"), ("Учеников", "Count"), ("Средний прогресс, %", "Percent"), ("Решено за 7 дней", "Completed"), ("Последняя активность", "Last")]);
        page.Children.Add(grid);
        void Open() { if (grid.SelectedItem is StatisticsRow r) { groupId = r.Id; generation++; Render(); } }
        grid.DoubleTapped += (_, _) => Open();
        page.Children.Add(Ui.Button("Открыть выбранную группу", () => { Open(); return Task.CompletedTask; }));
        if (data.Students.Length == 0) page.Children.Add(Ui.Text("Пока нет учеников. Создайте их во вкладке «Ученики и группы»."));
        page.Children.Add(Ui.Text("Средний прогресс включает всех учеников группы, в том числе ещё не начавших курс. Решения за 7 дней — количество впервые завершённых заданий.", 12));
    }

    StatisticsRow GroupRow(string id, string name)
    {
        var students = data!.Students.Where(s => id == "*" || (id == "" ? s.GroupId is null : s.GroupId == id)).ToArray();
        var records = Records(students);
        var last = students.Select(s => s.LastActivityAt).DefaultIfEmpty().Max();
        return new() { Id = id, Name = name, Count = students.Length, Percent = students.Length == 0 ? 0 : Math.Round(students.Average(Progress), 1), Completed = records.Count(r => r.CompletedAt >= data.GeneratedAt - 7L * 86400000), Last = Time(last), LastAt = last };
    }

    void RenderGroup()
    {
        Header(GroupName());
        var students = Members();
        var records = Records(students);
        page.Children.Add(Ui.Text($"Учеников: {students.Length} · Средний прогресс: {(students.Length == 0 ? 0 : students.Average(Progress)):F1}% · Решено: {records.Count(r => r.CompletedAt is not null)}"));
        var search = Ui.Input(); search.Watermark = "Поиск ученика";
        var rows = students.Select(s =>
        {
            var own = records.Where(r => r.StudentId == s.Id).ToArray();
            return new StatisticsRow { Id = s.Id, Name = s.Login, Percent = Math.Round(Progress(s), 1), Progress = $"{s.Completed} / {s.Total}", Errors = own.Sum(r => r.Errors), Hints = own.Sum(r => r.Hints), Violations = own.Sum(r => r.Violations), Active = Duration(own.Sum(r => r.ActiveSeconds)), ActiveSeconds = own.Sum(r => r.ActiveSeconds), Last = Time(s.LastActivityAt), LastAt = s.LastActivityAt };
        }).ToArray();
        var grid = Grid(rows, [("Ученик", "Name"), ("Решено", "Progress"), ("Прогресс, %", "Percent"), ("Активное время", "Active"), ("Ошибки", "Errors"), ("Подсказки", "Hints"), ("Нарушения", "Violations"), ("Последняя активность", "Last")]);
        search.TextChanged += (_, _) => grid.ItemsSource = rows.Where(r => r.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        page.Children.Add(search); page.Children.Add(grid);
        void Open() { if (grid.SelectedItem is StatisticsRow row) { studentId = row.Id; generation++; RenderStudent(); } }
        grid.DoubleTapped += (_, _) => Open();
        page.Children.Add(Ui.Row(Ui.Button("Открыть выбранного ученика", () => { Open(); return Task.CompletedTask; }), Ui.Button("Экспорт группы в CSV", () => owner.Safe(() => Export(students)))));
        page.Children.Add(Ui.Text("Задания, вызывающие трудности", 20));
        var hard = records.Where(r => r.Checks > 0).GroupBy(r => r.TaskId).Select(g => new StatisticsRow
        {
            Id = g.Key, Name = g.First().Title, Count = g.Count(), Completed = g.Count(r => r.CompletedAt is not null),
            Errors = g.Sum(r => r.Errors), Checks = g.Sum(r => r.Checks), Hints = g.Count(r => r.Hints > 0),
            Percent = Math.Round(100d * g.Count(r => r.CompletedAt is null) / g.Count(), 1),
            Progress = $"{g.Count(r => r.CompletedAt is null)} из {g.Count()}",
            First = g.Any(r => r.FirstCheckFailed is not null) ? $"{g.Count(r => r.FirstCheckFailed == 1)} из {g.Count(r => r.FirstCheckFailed is not null)}" : "Нет данных",
            State = g.Count() < 5 ? "Мало данных" : ""
        }).OrderByDescending(r => r.Percent).ThenByDescending(r => r.Count).ThenBy(r => r.Name).ToArray();
        page.Children.Add(Grid(hard, [("Задание", "Name"), ("Не решили", "Progress"), ("Не решили, %", "Percent"), ("Первая проверка с ошибкой", "First"), ("Ошибки", "Errors"), ("Использовали подсказки", "Hints"), ("Примечание", "State")]));
        page.Children.Add(Ui.Text("Рейтинг учитывает только учеников, отправивших решение на проверку. «Пока не решили» не означает итоговую неуспеваемость. Нарушения — обнаруженные запрещённые действия, а не доказательство списывания.", 12));
    }

    void RenderStudent()
    {
        var student = data!.Students.First(s => s.Id == studentId);
        Header(student.Login);
        var own = data.Records.Where(r => r.StudentId == studentId).ToDictionary(r => r.TaskId);
        page.Children.Add(Ui.Text($"Решено: {student.Completed} / {student.Total} ({Progress(student):F1}%) · Активное время: {Duration(own.Values.Sum(r => r.ActiveSeconds))} · Последняя активность: {Time(student.LastActivityAt)}"));
        var search = Ui.Input(); search.Watermark = "Поиск задания";
        var filter = new ComboBox { ItemsSource = new[] { "Все задания", "Не начато", "В работе", "Решено", "С ошибками", "С нарушениями" }, SelectedIndex = 0, MinWidth = 180 };
        var rows = student.TaskIds.Select(id =>
        {
            own.TryGetValue(id, out var r);
            return new StatisticsRow { Id = id, Name = r?.Title ?? data.Tasks.FirstOrDefault(t => t.Id == id)?.Title ?? id, State = r?.CompletedAt is not null ? "Решено" : r is not null && (r.StartedAt is not null || r.Checks > 0 || r.LastActivityAt is not null || r.ActiveSeconds > 0) ? "В работе" : "Не начато", Variant = r?.Variant.ToString() ?? "—", Started = Time(r?.StartedAt), StartedAt = r?.StartedAt, Last = Time(r?.CompletedAt), LastAt = r?.CompletedAt, Active = Duration(r?.ActiveSecondsAtCompletion ?? r?.ActiveSeconds ?? 0), ActiveSeconds = r?.ActiveSecondsAtCompletion ?? r?.ActiveSeconds ?? 0, Checks = r?.Checks ?? 0, Errors = r?.Errors ?? 0, Hints = r?.Hints ?? 0, Violations = r?.Violations ?? 0 };
        }).ToArray();
        var grid = Grid(rows, [("Задание", "Name"), ("Вариант", "Variant"), ("Состояние", "State"), ("Первое открытие", "Started"), ("Решено", "Last"), ("Активное время", "Active"), ("Проверки", "Checks"), ("Ошибки", "Errors"), ("Подсказки", "Hints"), ("Нарушения", "Violations")]);
        void Filter() => grid.ItemsSource = rows.Where(r => r.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase) && (filter.SelectedIndex == 0 || filter.SelectedIndex < 4 && r.State == filter.SelectedItem?.ToString() || filter.SelectedIndex == 4 && r.Errors > 0 || filter.SelectedIndex == 5 && r.Violations > 0)).ToArray();
        search.TextChanged += (_, _) => Filter(); filter.SelectionChanged += (_, _) => Filter();
        page.Children.Add(Ui.Row(filter, search)); page.Children.Add(grid);
        async Task Open() { if (grid.SelectedItem is StatisticsRow r) { if (!own.ContainsKey(r.Id)) { owner.Message("Ученик ещё не открывал это задание."); return; } await OpenTask(r.Id); } }
        grid.DoubleTapped += async (_, _) => await owner.Safe(Open);
        page.Children.Add(Ui.Button("Открыть выбранное задание", () => owner.Safe(Open)));
        page.Children.Add(Ui.Text("Время указано по часам компьютера преподавателя. Активное время для решённых заданий фиксируется при первом правильном ответе. Старые даты и ответы, которые не сохранялись, восстановить нельзя.", 12));
    }

    async Task OpenTask(string id)
    {
        var student = studentId;
        var stamp = ++generation;
        var update = await api.Get<LearningTaskDetail>($"teacher/learning/{student}/{id}");
        if (stamp != generation || studentId != student) return;
        taskId = id; detail = update; history.Clear(); history.AddRange(update.Events);
        RenderTask(); scroll.Offset = default;
    }

    void RenderTask()
    {
        var d = detail!; var r = d.Record;
        Header(r.Title);
        page.Children.Add(Ui.Text($"Вариант {r.Variant} · редакция {r.Revision} · {(r.CompletedAt is null ? "В работе" : "Решено")}"));
        page.Children.Add(Ui.Text(d.Prompt, 16));
        page.Children.Add(Ui.Text($"Первое открытие: {Time(r.StartedAt)}\nПоследняя активность: {Time(r.LastActivityAt)}\nПервое решение: {Time(r.CompletedAt)}\nАктивное время до решения: {Duration(r.CompletedAt is null ? r.ActiveSeconds : r.ActiveSecondsAtCompletion)}\nПроверки: {r.Checks} · ошибки: {r.Errors} · подсказки: {r.Hints} / {r.HintTotal} · нарушения: {r.Violations}"));
        page.Children.Add(Ui.Text("Принятый ответ", 20));
        page.Children.Add(Code(d.AcceptedCode ?? (r.CompletedAt is null ? "Ещё не решено" : "Ответ не записывался в прежней версии")));
        page.Children.Add(new Expander { Header = "Текущий сохранённый черновик", Content = Code(d.Draft.Length == 0 ? "Черновик пуст" : d.Draft) });
        page.Children.Add(Ui.Text("История действий · последние сверху", 20));
        foreach (var e in history)
        {
            var panel = Ui.Stack(Ui.Text($"{Time(e.At)} · {Action(e.Action)} · {Result(e.Result)}", 14));
            if (e.Code is not null) panel.Children.Add(Code(e.Code));
            else if (e.Action is "check" or "run") panel.Children.Add(Ui.Text("SQL этой попытки не записывался", 12));
            if (e.Explanation is not null) panel.Children.Add(Ui.Text(e.Explanation));
            page.Children.Add(Ui.Card(panel));
        }
        if (history.Count == 0) page.Children.Add(Ui.Text("Записанных событий нет."));
        if (d.HasMore)
            page.Children.Add(Ui.Button("Показать ещё 100 событий", () => owner.Safe(async () =>
            {
                var stamp = generation; var student = studentId; var task = taskId; var offset = scroll.Offset;
                var next = await api.Get<LearningTaskDetail>($"teacher/learning/{student}/{task}?offset={d.NextOffset}");
                if (stamp != generation) return;
                history.AddRange(next.Events.Where(e => history.All(x => x.Id != e.Id))); detail = next;
                RenderTask(); scroll.Offset = offset;
            })));
        page.Children.Add(Ui.Text("Нарушения показывают только события, обнаруженные приложением. Не все системные попытки снимка доступны для регистрации.", 12));
    }

    static TextBox Code(string text) => new() { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontFamily = new("Cascadia Code, Consolas, monospace"), MinHeight = 70, MaxHeight = 240 };
    static string Action(string action) => action switch { "opened" => "Первое открытие", "check" => "Проверка", "run" => "Запуск SQL", "hint" => "Открытие подсказки", "copy" => "Запрет копирования", "cut" => "Запрет вырезания", "paste" => "Запрет вставки", "capture" => "Попытка снимка окна", "drop" => "Запрет перетаскивания", "clipboard" => "Запрет буфера обмена", _ => action };
    static string Result(string result) => result switch { "correct" => "Верно", "different" => "Результат отличается", "executed" => "Выполнено", "clipboard" or "capture" => "Заблокировано", _ => result };
    static DataGrid Grid(IEnumerable<StatisticsRow> rows, (string Title, string Property)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = rows.ToArray(), MinHeight = 150, MaxHeight = 420, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (title, property) in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), SortMemberPath = property switch { "Last" => nameof(StatisticsRow.LastAt), "Started" => nameof(StatisticsRow.StartedAt), "Active" => nameof(StatisticsRow.ActiveSeconds), _ => property }, CanUserSort = property is not ("Progress" or "First"), MinWidth = 95, Width = new DataGridLength(1, DataGridLengthUnitType.Auto) });
        return grid;
    }

    async Task Export(LearningStudent[] students)
    {
        var records = Records(students).ToDictionary(r => (r.StudentId, r.TaskId));
        var file = await owner.StorageProvider.SaveFilePickerAsync(new() { Title = "Экспорт статистики группы", SuggestedFileName = "sql-group-statistics.csv", DefaultExtension = "csv" });
        if (file is null) return;
        static string Cell(string v) => "\"" + ((v.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' ? "'" : "") + v).Replace("\"", "\"\"") + "\"";
        await using var stream = await file.OpenWriteAsync(); using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        if (stream.CanSeek) stream.SetLength(0);
        await writer.WriteLineAsync("Группа;Ученик;Задание;Вариант;Состояние;Первое открытие;Последняя активность;Первое решение;Активные секунды;Проверки;Ошибки;Подсказки;Нарушения");
        foreach (var student in students)
            foreach (var id in student.TaskIds)
            {
                records.TryGetValue((student.Id, id), out var r);
                var group = data!.Groups.FirstOrDefault(g => g.Id == student.GroupId)?.Name ?? "Без группы";
                await writer.WriteLineAsync(string.Join(';', new[] { group, student.Login, r?.Title ?? data.Tasks.FirstOrDefault(t => t.Id == id)?.Title ?? id, r?.Variant.ToString() ?? "", r?.CompletedAt is not null ? "Решено" : r is not null && (r.StartedAt is not null || r.Checks > 0 || r.LastActivityAt is not null || r.ActiveSeconds > 0) ? "В работе" : "Не начато", Time(r?.StartedAt), Time(r?.LastActivityAt), Time(r?.CompletedAt), (r?.ActiveSecondsAtCompletion ?? r?.ActiveSeconds ?? 0).ToString(), (r?.Checks ?? 0).ToString(), (r?.Errors ?? 0).ToString(), (r?.Hints ?? 0).ToString(), (r?.Violations ?? 0).ToString() }.Select(Cell)));
            }
        owner.Message("Статистика группы экспортирована.");
    }

    internal void Preview(LearningOverview overview, string? group = null, string? student = null, LearningTaskDetail? task = null)
    {
        data = overview; groupId = group; studentId = student; taskId = task?.Record.TaskId; detail = task;
        if (task is null) Render(); else { history.Clear(); history.AddRange(task.Events); RenderTask(); }
    }
}

public sealed class StatisticsRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public int Completed { get; init; }
    public double Percent { get; init; }
    public string Progress { get; init; } = "";
    public int Errors { get; init; }
    public int Checks { get; init; }
    public int Hints { get; init; }
    public int Violations { get; init; }
    public string First { get; init; } = "";
    public string State { get; init; } = "";
    public string Variant { get; init; } = "";
    public string Active { get; init; } = "";
    public string Started { get; init; } = "";
    public string Last { get; init; } = "";
    public long? LastAt { get; init; }
    public long? StartedAt { get; init; }
    public int ActiveSeconds { get; init; }
}
