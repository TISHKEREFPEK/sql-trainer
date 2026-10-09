using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;
public sealed class TeacherScreen : UserControl, IDisposable
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly HostManager host;
    readonly DispatcherTimer poll = new()
    {
        Interval = TimeSpan.FromSeconds(5)
    };
    readonly ListBox students = new();
    readonly ComboBox groups = new();
    readonly ListBox taskList = new();
    readonly TextBox taskSearch = Ui.Input();
    readonly ComboBox version = new();
    readonly CheckBox restricted = new()
    {
        Content = "Запрет копирования и вставки; пауза 30 секунд"
    };
    readonly StackPanel editFields = Ui.Stack();
    readonly StackPanel journal = Ui.Stack();
    readonly StackPanel stats = Ui.Stack();
    readonly StackPanel hints = Ui.Stack();
    readonly Dictionary<string, TextBox> fields = new();
    readonly CheckBox ordered = new()
    {
        Content = "Учитывать порядок строк"
    };
    readonly CheckBox eligible = new()
    {
        Content = "Четыре варианта"
    };
    readonly ComboBox mode = new()
    {
        ItemsSource = new[]
        {
            "query",
            "state"
        }
    };
    TeacherOverview? overview;
    TeacherTaskDto? authoring;
    JsonObject? document;
    string? selectedId;
    bool loading, refreshing, disposed;
    long generation;
    readonly TabControl tabs;
    readonly TabControl reportTabs = new();
    readonly StackPanel analytics = Ui.Stack();
    readonly ComboBox reportGroup = new();
    public TeacherScreen(MainWindow owner, ClassroomApi api, HostManager host)
    {
        this.owner = owner;
        this.api = api;
        this.host = host;
        students.SelectionChanged += (_, _) =>
        {
        };
        taskList.SelectionChanged += async (_, _) =>
        {
            if (taskList.SelectedItem is Choice task)
                await owner.Safe(() => SelectTask(task.Id));
        };
        taskSearch.Watermark = "Поиск задания";
        taskSearch.TextChanged += (_, _) => UpdateTaskList();
        version.SelectionChanged += (_, _) =>
        {
            if (!loading)
                RenderDocument();
        };
        tabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem
                {
                    Header = "Ученики и группы",
                    Content = Accounts()
                },
                new TabItem
                {
                    Header = "Задания",
                    Content = Authoring()
                },
                new TabItem
                {
                    Header = "Результаты",
                    Content = Reports()
                },
                new TabItem
                {
                    Header = "Настройки",
                    Content = Settings()
                }
            },
            Margin = new Thickness(12)
        };
        Content = tabs;
        poll.Tick += async (_, _) =>
        {
            if (refreshing || disposed)
                return;
            refreshing = true;
            try
            {
                await Refresh(false);
            }
            catch (Exception e)when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
                owner.Message("Не удалось обновить отчёт.");
            }
            finally
            {
                refreshing = false;
            }
        };
    }

    public async Task Load()
    {
        await Refresh(true);
        poll.Start();
    }

    public void ApplyTheme()
    {
    }

    Control Accounts()
    {
        var login = Ui.Input();
        var password = Ui.Input();
        password.PasswordChar = '●';
        var groupName = Ui.Input();
        var controls = Ui.Stack(Ui.Text("Новый ученик", 20), Ui.Field("Логин", login), Ui.Field("Пароль", password), Ui.Field("Группа", groups), Ui.Button("Добавить ученика", () => owner.Safe(async () =>
        {
            await api.Post<JsonElement>("teacher/students", new CreateStudentRequest(login.Text ?? "", password.Text ?? "", (groups.SelectedItem as Choice)?.Id is { Length: > 0 } id ? id : null));
            login.Text = "";
            password.Text = "";
            await Refresh(true);
        })), Ui.Text("Группы", 20), Ui.Field("Название новой группы", groupName), Ui.Button("Создать группу", () => owner.Safe(async () =>
        {
            await api.Post<GroupDto>("teacher/groups", new GroupRequest(groupName.Text ?? ""));
            groupName.Text = "";
            await Refresh(true);
        })), Ui.Button("Удалить выбранную группу", () => owner.Safe(async () =>
        {
            if (groups.SelectedItem is Choice { Id.Length: > 0 } group && await owner.Confirm($"Удалить группу «{group.Label}»? Профили и результаты учеников сохранятся."))
            {
                await api.Delete<JsonElement>($"teacher/groups/{group.Id}");
                await Refresh(true);
            }
        })), Ui.Text("Выбранный ученик", 20), Ui.Button("Назначить выбранную группу", () => owner.Safe(async () =>
        {
            if (students.SelectedItem is Choice student)
            {
                await api.Put<JsonElement>($"teacher/students/{student.Id}/group", new GroupMembershipRequest((groups.SelectedItem as Choice)?.Id is { Length: > 0 } groupId ? groupId : null));
                await Refresh(false);
            }
        })), Ui.Button("Разблокировать", () => owner.Safe(async () =>
        {
            if (students.SelectedItem is Choice student)
            {
                await api.Post<JsonElement>($"teacher/students/{student.Id}/unlock");
                await Refresh(false);
            }
        })), Ui.Button("Установить новый пароль", () => owner.Safe(async () =>
        {
            if (students.SelectedItem is Choice student)
            {
                await api.Put<JsonElement>($"teacher/students/{student.Id}/password", new PasswordRequest(password.Text ?? ""));
                password.Text = "";
                owner.Message("Пароль изменён.");
            }
        })), Ui.Button("Удалить ученика", () => owner.Safe(async () =>
        {
            if (students.SelectedItem is Choice student && await owner.Confirm($"Удалить ученика «{student.Label}» и его результаты?"))
            {
                await api.Delete<JsonElement>($"teacher/students/{student.Id}");
                await Refresh(false);
            }
        })));
        var grid = new Grid
        {
            ColumnDefinitions = new("*,420"),
            ColumnSpacing = 12
        };
        grid.Children.Add(Ui.Card(students));
        var form = Ui.Card(Ui.Scroll(controls));
        Grid.SetColumn(form, 1);
        grid.Children.Add(form);
        return grid;
    }

    Control Authoring()
    {
        var navigator = Ui.Stack(taskSearch, Ui.Button("Новое задание", () =>
        {
            selectedId = "custom-" + Guid.NewGuid().ToString("N")[..8];
            var json = new JsonObject
            {
                ["id"] = selectedId,
                ["module"] = "Практика · Свои задания",
                ["title"] = "Новое задание",
                ["prompt"] = "Выведи число 1 в столбце value.",
                ["concept"] = "SELECT может вычислять выражения.",
                ["example"] = "SELECT 2 + 2;",
                ["starter"] = "-- Напиши SQL-запрос",
                ["solution"] = "SELECT 1 AS value;",
                ["seed"] = "",
                ["terms"] = new JsonArray(),
                ["hints"] = new JsonArray("Используй SELECT.", "Укажи имя столбца через AS."),
                ["mode"] = "query"
            };
            authoring = new(json.ToJsonString(), [], false, 1);
            loading = true;
            version.ItemsSource = new[]
            {
                "Основное"
            };
            version.SelectedIndex = 0;
            loading = false;
            RenderDocument();
            return Task.CompletedTask;
        }), Ui.Button("Удалить задание", () => owner.Safe(async () =>
        {
            if (selectedId is not null && await owner.Confirm("Удалить задание из каталога новых назначений?"))
            {
                await api.Delete<JsonElement>($"teacher/tasks/{selectedId}");
                await Refresh(true);
            }
        })));
        foreach (var(key, label, multiline)in new[]
        {
            ("title", "Название", false),
            ("minutes", "Ориентир времени для преподавателя, мин", false),
            ("module", "Тема", false),
            ("prompt", "Условие", true),
            ("concept", "Объяснение", true),
            ("example", "Пример", true),
            ("starter", "Начальный текст SQL", true),
            ("project", "Проект (пусто — самостоятельное)", false),
            ("terms", "Термины через запятую", false),
            ("hints", "Подсказки — по одной на строку", true),
            ("seed", "Исходная база (SQL)", true),
            ("solution", "Эталонное решение", true)
        }

        )
        {
            var input = Ui.Input(null, multiline);
            fields[key] = input;
            editFields.Children.Add(Ui.Field(label, input));
        }

        editFields.Children.Add(Ui.Field("Режим проверки", mode));
        editFields.Children.Add(ordered);
        editFields.Children.Add(eligible);
        editFields.Children.Add(restricted);
        editFields.Children.Add(Ui.Button("Сохранить", () => owner.Safe(SaveTask)));
        var editor = Ui.Stack(Ui.Text("Редактор задания", 24), Ui.Text("Изменения получают новые назначения. Уже начатые работы сохраняют свою редакцию."), Ui.Field("Версия", version), editFields);
        var navLayout = new Grid
        {
            RowDefinitions = new("Auto,*"),
            RowSpacing = 12
        };
        navLayout.Children.Add(navigator);
        Grid.SetRow(taskList, 1);
        navLayout.Children.Add(taskList);
        taskList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Choice>((item, _) => new TextBlock { Text = item?.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 250, Margin = new Thickness(0, 4) }, true);
        var grid = new Grid
        {
            ColumnDefinitions = new("300,*"),
            ColumnSpacing = 12
        };
        grid.Children.Add(Ui.Card(navLayout));
        var form = Ui.Card(Ui.Scroll(editor));
        Grid.SetColumn(form, 1);
        grid.Children.Add(form);
        return grid;
    }

    Control Reports()
    {
        reportGroup.SelectionChanged += (_, _) => RenderAnalytics();
        reportTabs.ItemsSource = new[]
        {
            new TabItem
            {
                Header = "Проверки",
                Content = Ui.Scroll(journal)
            },
            new TabItem
            {
                Header = "Подсказки",
                Content = Ui.Scroll(hints)
            },
            new TabItem
            {
                Header = "Прогресс и активность",
                Content = Ui.Scroll(stats)
            },
            new TabItem
            {
                Header = "Аналитика",
                Content = AnalyticsLayout()
            }
        };
        return Ui.Card(reportTabs);
    }

    Control AnalyticsLayout()
    {
        var layout = new Grid
        {
            RowDefinitions = new("Auto,*"),
            RowSpacing = 12
        };
        layout.Children.Add(Ui.Field("Группа", reportGroup));
        var scroll = Ui.Scroll(analytics);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        return layout;
    }

    void RenderAnalytics()
    {
        if (overview is not { } data)
            return;
        analytics.Children.Clear();
        var groupId = (reportGroup.SelectedItem as Choice)?.Id ?? "";
        var students = data.Students.Where(s => groupId.Length == 0 || s.GroupId == groupId).ToArray();
        var logins = students.Select(s => s.Login).ToHashSet();
        var records = data.Statistics.Where(s => logins.Contains(s.Student)).ToArray();
        var titles = data.Tasks.ToDictionary(t => t.Id);
        string Title(string id) => titles.GetValueOrDefault(id)?.Title ?? id;
        analytics.Children.Add(Ui.Text($"Начато заданий: {records.Length} · Выполнено: {records.Count(r => r.CompletedAt is not null)} · Ошибок: {records.Sum(r => r.Errors)} · Среднее время: {(records.Length == 0 ? 0 : records.Sum(r => r.ActiveSeconds) / records.Length)} с", 18));
        analytics.Children.Add(Ui.Text("Прогресс группы", 20));
        var denominator = Math.Max(1, data.Tasks.Length);
        analytics.Children.Add(Ui.Text($"Учеников: {students.Length} · Средний прогресс: {(students.Length == 0 ? 0 : students.Average(s => s.Completed) * 100 / denominator):F1}% · Начали: {students.Count(s => s.Completed > 0)} · Завершили курс: {students.Count(s => s.Completed >= denominator)}"));
        analytics.Children.Add(Ui.Text("Задания с наибольшим числом ошибок", 20));
        var hard = records.GroupBy(r => r.TaskId).Select(g => new { Id = g.Key, Errors = g.Sum(r => r.Errors), Checks = g.Sum(r => r.Checks), Seconds = g.Sum(r => r.ActiveSeconds) / g.Count(), Starts = g.Count() }).OrderByDescending(g => g.Errors).ThenByDescending(g => g.Checks).Take(15);
        var hardPanel = Ui.Stack();
        RenderReport(hardPanel, ["Задание", "Начали", "Ошибки", "Проверки", "Среднее время, с"], hard.Select(g => new[] { Title(g.Id), g.Starts.ToString(), g.Errors.ToString(), g.Checks.ToString(), g.Seconds.ToString() }));
        analytics.Children.Add(hardPanel);
        var stops = students.Select(s => data.Tasks.FirstOrDefault(t => !data.Statistics.Any(r => r.Student == s.Login && r.TaskId == t.Id && r.CompletedAt is not null))).Where(t => t is not null).Cast<CatalogueItem>().ToArray();
        analytics.Children.Add(Ui.Text("Места остановки", 20));
        analytics.Children.Add(Ui.Text("Первое незавершённое задание по порядку каталога."));
        var stopPanel = Ui.Stack();
        RenderReport(stopPanel, ["Задание", "Ученики"], stops.GroupBy(t => t.Id).OrderByDescending(g => g.Count()).Select(g => new[] { Title(g.Key), g.Count().ToString() }));
        analytics.Children.Add(stopPanel);
        analytics.Children.Add(Ui.Text("Темы", 20));
        var topicPanel = Ui.Stack();
        RenderReport(topicPanel, ["Тема", "Ошибки", "Остановки"], data.Tasks.GroupBy(t => t.Module).Select(g => new[] { g.Key, records.Where(r => g.Any(t => t.Id == r.TaskId)).Sum(r => r.Errors).ToString(), stops.Count(t => t.Module == g.Key).ToString() }));
        analytics.Children.Add(topicPanel);
        analytics.Children.Add(Ui.Text("История завершений", 20));
        var historyPanel = Ui.Stack();
        RenderReport(historyPanel, ["Ученик", "Задание", "Завершено", "Активное время, с"], records.Where(r => r.CompletedAt is not null).OrderByDescending(r => r.CompletedAt).Take(50).Select(r => new[] { r.Student, Title(r.TaskId), Ui.Time(r.CompletedAt!.Value), r.ActiveSeconds.ToString() }));
        analytics.Children.Add(historyPanel);
    }

    Control Settings()
    {
        var connection = host.Connection;
        var address = Ui.Input(host.Settings.Address);
        var port = Ui.Input(host.Settings.Port.ToString());
        var info = Ui.Input(connection is null ? "" : connection.Address + "\nSHA-256: " + connection.Fingerprint, true);
        info.IsReadOnly = true;
        var backupList = new ComboBox();
        var panel = Ui.Stack(Ui.Text("Подключение учеников", 24), Ui.Text("Передайте ученикам адрес и отпечаток сертификата. Изменение сетевого интерфейса применяется после перезапуска преподавателя."), info, Ui.Field("IP-адрес интерфейса", address), Ui.Field("Порт", port), Ui.Button("Сохранить параметры сети", () => owner.Safe(() =>
        {
            if (!int.TryParse(port.Text, out var value))
                throw new InvalidDataException("Некорректный порт.");
            host.SaveSettings(new(address.Text ?? "", value));
            owner.Message("Параметры сохранены. Перезапустите приложение для применения.");
            return Task.CompletedTask;
        })), Ui.Text("Перенос из Electron", 20), Ui.Button("Выбрать classroom.sqlite", () => owner.Safe(Import)), Ui.Text("Резервные копии", 20), Ui.Button("Создать резервную копию", () => owner.Safe(async () =>
        {
            await api.Post<JsonElement>("teacher/backups");
            backupList.ItemsSource = (await api.Get<BackupDto[]>("teacher/backups")).Select(b => new Choice(b.Name, b.Name)).ToArray();
            owner.Message("Резервная копия создана.");
        })), Ui.Button("Обновить список копий", () => owner.Safe(async () =>
        {
            backupList.ItemsSource = (await api.Get<BackupDto[]>("teacher/backups")).Select(b => new Choice(b.Name, b.Name)).ToArray();
        })), backupList, Ui.Button("Восстановить выбранную копию", () => owner.Safe(async () =>
        {
            if (backupList.SelectedItem is Choice backup && await owner.Confirm("Заменить текущую базу выбранной резервной копией? Текущее состояние будет скопировано перед восстановлением. После восстановления нужен повторный вход."))
            {
                await api.Post<JsonElement>($"teacher/backups/{Uri.EscapeDataString(backup.Id)}/restore");
                owner.Message("База восстановлена. Выйдите и войдите снова.");
            }
        })), Ui.Text("Экспорт статистики", 20), Ui.Button("Сохранить CSV", () => owner.Safe(Export)), Ui.Text("Данные преподавателя: " + host.DataDirectory));
        return Ui.Card(Ui.Scroll(panel));
    }

    async Task Refresh(bool updateEditorList)
    {
        var data = await api.Get<TeacherOverview>("teacher/overview");
        if (disposed)
            return;
        RenderOverview(data, updateEditorList);
    }

    void RenderOverview(TeacherOverview data, bool updateEditorList)
    {
        overview = data;
        var reportSelection = (reportGroup.SelectedItem as Choice)?.Id;
        reportGroup.ItemsSource = new[]
        {
            new Choice("", "Все группы")
        }.Concat(data.Groups.Select(g => new Choice(g.Id, g.Name))).ToArray();
        reportGroup.SelectedItem = (reportGroup.ItemsSource as Choice[])!.FirstOrDefault(g => g.Id == reportSelection) ?? (reportGroup.ItemsSource as Choice[])![0];
        RenderAnalytics();
        var selected = (students.SelectedItem as Choice)?.Id;
        students.ItemsSource = data.Students.Select(s => new Choice(s.Id, $"{s.Login} · {s.Completed} / {data.Tasks.Length}" + (s.LockedUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? " · Пауза" : ""))).ToArray();
        students.SelectedItem = (students.ItemsSource as Choice[])?.FirstOrDefault(s => s.Id == selected);
        if (updateEditorList)
        {
            var group = (groups.SelectedItem as Choice)?.Id;
            groups.ItemsSource = new[]
            {
                new Choice("", "Без группы")
            }.Concat(data.Groups.Select(g => new Choice(g.Id, g.Name))).ToArray();
            groups.SelectedItem = (groups.ItemsSource as Choice[])?.FirstOrDefault(g => g.Id == group) ?? (groups.ItemsSource as Choice[])?.FirstOrDefault();
            UpdateTaskList();
        }

        string Title(string id) => data.Tasks.FirstOrDefault(t => t.Id == id)?.Title ?? id;
        RenderReport(journal, ["Ученик", "Задание", "Результат", "Время"], data.Attempts.Select(a => new[] { a.Student, Title(a.TaskId), a.Kind == "correct" ? "Верно" : a.Kind == "different" ? "Результат отличается" : a.Kind == "clipboard" ? "Копирование / вставка" : a.Kind, Ui.Time(a.At) }));
        RenderReport(hints, ["Ученик", "Задание", "Вариант", "Подсказки"], data.Hints.Select(h => new[] { h.Student, Title(h.TaskId), h.VariantIndex.ToString(), string.Join("; ", h.OpenedAt.Select((t, i) => $"№{i + 1} — {Ui.Time(t)}")) }));
        RenderReport(stats, ["Ученик", "Задание", "Активные секунды", "Проверки", "Ошибки", "Завершение"], data.Statistics.Select(s => new[] { s.Student, Title(s.TaskId), s.ActiveSeconds.ToString(), s.Checks.ToString(), s.Errors.ToString(), s.CompletedAt is { } at ? Ui.Time(at) : "" }));
    }

    static void RenderReport(StackPanel panel, string[] columns, IEnumerable<string[]> rows)
    {
        panel.Children.Clear();
        panel.Children.Add(Ui.Table(new(columns, rows.Select(r => r.Select(v => JsonSerializer.SerializeToElement(v)).ToArray()).ToArray())));
    }

    void UpdateTaskList()
    {
        if (overview is null)
            return;
        var text = taskSearch.Text ?? "";
        taskList.ItemsSource = overview.Tasks.Where(t => t.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || t.Module.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(t => new Choice(t.Id, t.Module + " · " + t.Title)).ToArray();
    }

    async Task SelectTask(string id)
    {
        var stamp = ++generation;
        var task = await api.Get<TeacherTaskDto>($"teacher/tasks/{id}");
        if (stamp != generation || disposed)
            return;
        selectedId = id;
        authoring = task;
        loading = true;
        version.ItemsSource = Enumerable.Range(0, 1 + task.AlternativeJson.Length).Select(i => i == 0 ? "Основное" : $"Вариант {i + 1}").ToArray();
        version.SelectedIndex = 0;
        loading = false;
        RenderDocument();
    }

    void RenderDocument()
    {
        if (authoring is null)
            return;
        document = JsonNode.Parse(version.SelectedIndex <= 0 ? authoring.TaskJson : authoring.AlternativeJson[version.SelectedIndex - 1])!.AsObject();
        foreach (var(key, input)in fields)
            input.Text = key == "minutes" ? (document[key]?.GetValue<int>() ?? 0).ToString() : key is "terms" or "hints" ? string.Join(key == "terms" ? ", " : "\n", document[key]?.AsArray().Select(v => v!.GetValue<string>()) ?? []) : document[key]?.GetValue<string>() ?? "";
        mode.SelectedIndex = document["mode"]?.GetValue<string>() == "state" ? 1 : 0;
        ordered.IsChecked = document["ordered"]?.GetValue<bool>() ?? false;
        eligible.IsChecked = document["variantEligible"]?.GetValue<bool>() ?? true;
        restricted.IsChecked = authoring.Restricted;
    }

    async Task SaveTask()
    {
        if (document is null || selectedId is null)
            return;
        foreach (var(key, input)in fields)
            document[key] = key == "minutes" ? JsonValue.Create(int.TryParse(input.Text, out var minutes) && minutes >= 0 ? minutes : throw new InvalidDataException("Введите неотрицательное время в минутах.")) : key is "terms" or "hints" ? JsonSerializer.SerializeToNode((input.Text ?? "").Split(key == "terms" ? ',' : '\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) : (JsonNode? )JsonValue.Create(input.Text ?? "");
        if (string.IsNullOrWhiteSpace(fields["project"].Text))
            document["project"] = null;
        document["mode"] = mode.SelectedIndex == 1 ? "state" : "query";
        document["ordered"] = ordered.IsChecked == true;
        document["variantEligible"] = eligible.IsChecked == true;
        if (version.SelectedIndex <= 0)
            await api.Put<JsonElement>($"teacher/tasks/{selectedId}", new SaveTaskRequest(document.ToJsonString(), restricted.IsChecked == true));
        else
            await api.Put<JsonElement>($"teacher/tasks/{selectedId}/variants/{version.SelectedIndex + 1}", new SaveVariantRequest(document.ToJsonString()));
        var id = selectedId;
        await Refresh(true);
        await SelectTask(id);
        owner.Message("Сохранено. Начатые назначения сохраняют прежние условия.");
    }

    async Task Import()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = "База Electron classroom.sqlite", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("SQLite") { Patterns = ["*.sqlite"] }] });
        if (files.Count == 0)
            return;
        await using var stream = await files[0].OpenReadAsync();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new("application/octet-stream");
        var preview = await api.Post<ImportPreview>("teacher/import/preview", content);
        if (!await owner.Confirm($"Перенести из выбранной базы: учеников — {preview.Students}, назначений — {preview.Assignments}, проектных баз — {preview.Projects}, попыток — {preview.Attempts}, изменённых заданий — {preview.Overrides}?"))
            return;
        await api.Post<JsonElement>("teacher/import/commit", new ImportCommitRequest(preview.Id));
        await Refresh(true);
        owner.Message("Импорт завершён. Исходная база сохранена.");
    }

    async Task Export()
    {
        if (overview is null)
            return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new() { Title = "Экспорт статистики", SuggestedFileName = "sql-results.csv", DefaultExtension = "csv" });
        if (file is null)
            return;
        static string Cell(string value)
        {
            if (value.TrimStart().FirstOrDefault()is '=' or '+' or '-' or '@')
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        var rows = overview.Statistics.Select(s => string.Join(";", new[] { s.Student, s.TaskId, s.ActiveSeconds.ToString(), s.Checks.ToString(), s.Errors.ToString(), s.CompletedAt is { } at ? Ui.Time(at) : "" }.Select(Cell)));
        await using var stream = await file.OpenWriteAsync();
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true));
        await writer.WriteLineAsync("Ученик;Задание;Активные секунды;Проверки;Ошибки;Завершение");
        foreach (var row in rows)
            await writer.WriteLineAsync(row);
        owner.Message("Статистика экспортирована.");
    }

    internal void Preview(TeacherOverview data, TeacherTaskDto task, bool reports = false)
    {
        RenderOverview(data, true);
        authoring = task;
        selectedId = JsonNode.Parse(task.TaskJson)!["id"]!.GetValue<string>();
        loading = true;
        version.ItemsSource = new[]
        {
            "Основное"
        };
        version.SelectedIndex = 0;
        loading = false;
        RenderDocument();
        tabs.SelectedIndex = reports ? 2 : 1;
        if (reports)
            reportTabs.SelectedIndex = 3;
    }

    public void Dispose()
    {
        disposed = true;
        generation++;
        poll.Stop();
    }

    sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
