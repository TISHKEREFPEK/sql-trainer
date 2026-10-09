using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;
public sealed class StudentScreen : UserControl, IDisposable
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly ComboBox category = new()
    {
        ItemsSource = new[]
        {
            "Курс",
            "Практика",
            "Проекты"
        },
        SelectedIndex = 0
    };
    readonly ComboBox module = new();
    readonly TextBox search = Ui.Input();
    readonly ListBox tasks = new();
    readonly TextBlock progress = Ui.Text("");
    readonly TextBlock heading = Ui.Text("", 24);
    readonly TextBlock prompt = Ui.Text("", 16);
    readonly StackPanel hints = Ui.Stack();
    readonly StackPanel explanation = Ui.Stack();
    readonly StackPanel tables = Ui.Stack();
    readonly StackPanel result = Ui.Stack();
    readonly TextEditor editor = new()
    {
        ShowLineNumbers = true,
        FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo"),
        FontSize = 14,
        MinHeight = 230,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };
    readonly TextBlock saved = Ui.Text("");
    readonly TextBlock pause = Ui.Text("");
    readonly TextBlock pageText = Ui.Text("");
    readonly StackPanel pagination;
    readonly Button nextHint;
    readonly Button run;
    readonly Button check;
    readonly DispatcherTimer poll = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    readonly SemaphoreSlim draftGate = new(1, 1);
    CatalogueDto? catalogue;
    StudentTask? current;
    PendingExecution? pending;
    int page;
    long generation;
    long editVersion;
    bool loading, syncing, disposed;
    string? savedCode;
    CancellationTokenSource? debounce;
    public bool Restricted => current?.Restricted == true;

    public StudentScreen(MainWindow owner, ClassroomApi api)
    {
        this.owner = owner;
        this.api = api;
        search.Watermark = "Поиск задания";
        module.HorizontalAlignment = HorizontalAlignment.Stretch;
        category.SelectionChanged += (_, _) => UpdateModules();
        module.SelectionChanged += (_, _) =>
        {
            page = 0;
            UpdateList();
        };
        search.TextChanged += (_, _) =>
        {
            page = 0;
            UpdateList();
        };
        tasks.SelectionChanged += async (_, _) =>
        {
            if (tasks.SelectedItem is TaskChoice choice)
                await owner.Safe(() => Select(choice.Id));
        };
        tasks.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TaskChoice>((item, _) => new TextBlock { Text = item?.Title, TextWrapping = TextWrapping.Wrap, MaxWidth = 190, Margin = new Thickness(0, 4) }, true);
        pagination = Ui.Row(Ui.Button("←", () =>
        {
            if (page > 0)
            {
                page--;
                UpdateList();
            }

            return Task.CompletedTask;
        }), pageText, Ui.Button("→", () =>
        {
            page++;
            UpdateList();
            return Task.CompletedTask;
        }));
        var nav = Ui.Stack(progress, category, module, search, tasks, pagination, Ui.Button("Справочник", () => owner.Safe(Glossary)));
        nextHint = Ui.Button("Открыть подсказку", () => owner.Safe(async () =>
        {
            if (current is null)
                return;
            var id = current.Id;
            var update = await api.Post<StudentTask>($"tasks/{id}/hint", new HintRequest(current.OpenedHints.Length));
            if (current?.Id == id)
            {
                current = update;
                RenderHints();
            }
        }));
        var learning = Ui.Stack(heading, prompt, hints, nextHint, explanation, new Expander { Header = "Таблицы базы", Content = tables });
        run = Ui.Button("Запустить", () => owner.Safe(() => Execute(false)));
        check = Ui.Button("Проверить", () => owner.Safe(() => Execute(true)));
        editor.TextChanged += (_, _) =>
        {
            if (loading || current is null)
                return;
            editVersion++;
            owner.RememberDraft(current.Id, editor.Text);
            saved.Text = "Изменения не сохранены";
            debounce?.Cancel();
            debounce = new();
            _ = SaveLater(debounce.Token);
        };
        editor.ContextMenu = null;
        var work = new Grid
        {
            RowDefinitions = new("Auto,*,Auto,Auto,*")
        };
        work.Children.Add(Ui.Text("SQL-запрос", 20));
        Grid.SetRow(editor, 1);
        work.Children.Add(editor);
        var buttons = Ui.Row(run, check);
        Grid.SetRow(buttons, 2);
        work.Children.Add(buttons);
        var reset = Ui.Button("Сбросить запрос", () =>
        {
            if (current is not null)
                editor.Text = current.Starter;
            return Task.CompletedTask;
        });
        var statePanel = Ui.Stack(saved, pause, reset);
        Grid.SetRow(statePanel, 3);
        work.Children.Add(statePanel);
        var output = Ui.Scroll(result);
        Grid.SetRow(output, 4);
        work.Children.Add(output);
        var grid = new Grid
        {
            ColumnDefinitions = new("232,*,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(12)
        };
        grid.Children.Add(Ui.Card(Ui.Scroll(nav)));
        var card = Ui.Card(Ui.Scroll(learning));
        Grid.SetColumn(card, 1);
        grid.Children.Add(card);
        var workstation = Ui.Card(work);
        Grid.SetColumn(workstation, 2);
        grid.Children.Add(workstation);
        Content = grid;
        SizeChanged += (_, _) =>
        {
            grid.ColumnDefinitions = Bounds.Width < 1100 ? new("200,*,*") : new("232,*,*");
        };
        poll.Tick += async (_, _) => await Synchronize();
        ThemeManager.Changed += ApplyTheme;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        editor.Background = Application.Current!.Resources["EditorBrush"] as IBrush;
        editor.Foreground = Application.Current.Resources["EditorInkBrush"] as IBrush;
        editor.SyntaxHighlighting = SqlHighlighting.Create(ThemeManager.Ink(((SolidColorBrush)editor.Background!).Color.ToString()));
    }

    public async Task Load()
    {
        catalogue = await api.Get<CatalogueDto>("catalog");
        UpdateModules();
        if (catalogue.Tasks.Length > 0)
            await Select(catalogue.Tasks.Any(t => t.Id == owner.LastTaskId) ? owner.LastTaskId! : catalogue.Tasks[0].Id);
        poll.Start();
    }

    static string Category(CatalogueItem item) => item.Project is not null ? "Проекты" : item.Module.StartsWith("Практика") || item.Module.StartsWith("Парковка") ? "Практика" : "Курс";
    void UpdateModules()
    {
        if (catalogue is null)
            return;
        var selected = category.SelectedItem as string ?? "Курс";
        var modules = catalogue.Tasks.Where(t => Category(t) == selected).Select(t => t.Module).Distinct().ToArray();
        module.ItemsSource = modules;
        module.SelectedIndex = modules.Length > 0 ? 0 : -1;
        page = 0;
        UpdateList();
    }

    void UpdateList()
    {
        if (catalogue is null)
            return;
        progress.Text = $"Выполнено {catalogue.Completed.Length} / {catalogue.Tasks.Length}";
        var text = search.Text?.Trim() ?? "";
        var filtered = catalogue.Tasks.Where(t => t.Module == (module.SelectedItem as string) && (text.Length == 0 || t.Title.Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        var pages = Math.Max(1, (filtered.Length + 7) / 8);
        page = Math.Clamp(page, 0, pages - 1);
        pagination.IsVisible = pages > 1;
        pageText.Text = pages > 1 ? $"{page + 1}/{pages}" : "";
        tasks.ItemsSource = filtered.Skip(page * 8).Take(8).Select((t, i) => new TaskChoice(t.Id, $"{page * 8 + i + 1:00}  {(catalogue.Completed.Contains(t.Id) ? "✓ " : "")}{t.Title}")).ToArray();
    }

    async Task Select(string id)
    {
        if (current?.Id == id)
            return;
        var stamp = ++generation;
        await SaveDraft();
        var task = await api.Get<StudentTask>($"tasks/{id}");
        if (disposed || stamp != generation)
            return;
        DisplayTask(task);
        var preview = await new PendingExecution(id, "", false).Send(api);
        if (stamp != generation || disposed)
            return;
        RenderTables(preview.Tables);
    }

    void DisplayTask(StudentTask task)
    {
        current = task;
        owner.LastTaskId = task.Id;
        pending = owner.PendingFor(task.Id);
        heading.Text = task.Title + $" · Вариант {task.VariantIndex}";
        prompt.Text = task.Prompt;
        explanation.Children.Clear();
        explanation.Children.Add(new Expander { Header = "Объяснение", Content = Ui.Text(task.Concept) });
        explanation.Children.Add(new Expander { Header = "Пример", Content = Ui.Text(task.Example) });
        var terms = new ComboBox
        {
            ItemsSource = task.Terms
        };
        var definition = Ui.Text("");
        terms.SelectionChanged += (_, _) => definition.Text = task.Definitions.GetValueOrDefault(terms.SelectedItem as string ?? "", "");
        explanation.Children.Add(new Expander { Header = "Термины", Content = Ui.Stack(terms, definition) });
        loading = true;
        editor.Text = owner.UnsavedDraft(task.Id) ?? (task.Draft.Length > 0 ? task.Draft : task.Starter);
        loading = false;
        savedCode = task.Draft;
        saved.Text = owner.UnsavedDraft(task.Id)is not null ? "Изменения не сохранены" : task.Draft.Length > 0 ? "Черновик сохранён" : "";
        RenderHints();
        Policy();
        result.Children.Clear();
        tables.Children.Clear();
    }

    internal void Preview(CatalogueDto catalog, StudentTask task, ExecutionDto preview)
    {
        catalogue = catalog;
        UpdateModules();
        module.SelectedItem = task.Module;
        DisplayTask(task);
        RenderTables(preview.Tables);
        result.Children.Add(Ui.Table(preview.Output));
    }

    void RenderHints()
    {
        if (current is null)
            return;
        hints.Children.Clear();
        foreach (var hint in current.OpenedHints)
            hints.Children.Add(Ui.Text(hint));
        nextHint.IsVisible = current.OpenedHints.Length < current.HintTotal;
    }

    void RenderTables(TablePreview[] previews)
    {
        tables.Children.Clear();
        foreach (var table in previews)
        {
            var structure = Ui.Text(string.Join("\n", (table.Columns ?? []).Select(c => $"{c.Name} · {c.Type}" + (c.PrimaryKey ? " · PRIMARY KEY" : "") + (c.NotNull ? " · NOT NULL" : ""))));
            var keys = Ui.Text(string.Join("\n", (table.ForeignKeys ?? []).Select(k => $"{k.Column} → {k.Table}.{k.Target}")));
            tables.Children.Add(new Expander { Header = table.Name, Content = Ui.Stack(structure, keys, Ui.Table(table.Data)) });
        }
    }

    void Policy()
    {
        if (current is null)
            return;
        var locked = current.LockedUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        editor.IsEnabled = !locked;
        run.IsEnabled = !locked;
        check.IsEnabled = !locked && !(current.Completed && current.Project is not null);
        nextHint.IsEnabled = !locked;
        pause.Text = locked ? "Работа приостановлена. Дождитесь снятия ограничения." : "";
        owner.Protect(current.Restricted);
    }

    async Task Execute(bool checking)
    {
        if (current is null)
            return;
        await SaveDraft();
        var stamp = generation;
        var id = current.Id;
        // A failed transport keeps pending; the next attempt retries exactly that operation.
        pending ??= new PendingExecution(id, editor.Text, checking);
        if (pending.TaskId != id)
            pending = new(id, editor.Text, checking);
        owner.RememberPending(pending);
        var submittedCheck = pending.Request.Check;
        if (owner.PendingFor(id)is not null)
            owner.Message("Повторяется незавершённая операция с прежним SQL.");
        var response = await pending.Send(api);
        owner.ForgetPending(id);
        pending = null;
        if (stamp != generation || disposed)
            return;
        result.Children.Clear();
        RenderTables(response.Tables);
        if (response.Error is not null)
        {
            result.Children.Add(Ui.Text(response.Error));
            result.Children.Add(Ui.Text(response.Explanation ?? ""));
            owner.Message("Запрос не выполнен.");
        }
        else
        {
            result.Children.Add(Ui.Text(submittedCheck ? response.Correct ? "Верно" : "Результат отличается от ожидаемого" : "Результат запроса", 18));
            result.Children.Add(Ui.Table(response.Output));
            if (submittedCheck && response.Correct)
            {
                var next = catalogue?.Tasks.SkipWhile(t => t.Id != id).Skip(1).FirstOrDefault();
                if (next is not null)
                    result.Children.Add(Ui.Button("Следующее задание", () => owner.Safe(async () =>
                    {
                        category.SelectedItem = Category(next);
                        module.SelectedItem = next.Module;
                        search.Text = "";
                        await Select(next.Id);
                    })));
            }
        }

        catalogue = await api.Get<CatalogueDto>("catalog");
        progress.Text = $"Выполнено {catalogue.Completed.Length} / {catalogue.Tasks.Length}";
        var updated = await api.Get<StudentTask>($"tasks/{id}");
        if (stamp == generation)
        {
            current = updated;
            Policy();
        }
    }

    async Task SaveLater(CancellationToken token)
    {
        try
        {
            await Task.Delay(650, token);
            if (!token.IsCancellationRequested)
                await SaveDraft();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)when (e is HttpRequestException or ApiException)
        {
            saved.Text = "Нет связи: черновик остаётся в открытом окне";
        }
    }

    public async Task SaveDraft()
    {
        await draftGate.WaitAsync();
        try
        {
            if (current is null || editor.Text == savedCode)
                return;
            var id = current.Id;
            var code = editor.Text;
            var version = editVersion;
            await api.Put<JsonElement>($"tasks/{id}/draft", new DraftRequest(code));
            if (current?.Id == id && editVersion == version)
            {
                savedCode = code;
                owner.ForgetDraft(id);
                saved.Text = "Черновик сохранён";
            }
        }
        finally
        {
            draftGate.Release();
        }
    }

    async Task Synchronize()
    {
        if (syncing || disposed || current is null)
            return;
        syncing = true;
        try
        {
            var id = current.Id;
            var update = await api.Get<StudentTask>($"tasks/{id}");
            if (current?.Id != id || disposed)
                return;
            current = update;
            Policy();
            RenderHints();
            await SaveDraft();
            if (owner.IsActive && !current.Completed && current.LockedUntil <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                await api.Post<JsonElement>($"tasks/{id}/activity", new ActivityRequest(2));
        }
        catch (ApiException e)when (e.Status == 401)
        {
            await owner.Reauthenticate();
        }
        catch (Exception e)when (e is HttpRequestException or ApiException or TaskCanceledException)
        {
            owner.Message("Связь с преподавателем потеряна. Запуск SQL недоступен; черновик остаётся в открытом окне.");
        }
        finally
        {
            syncing = false;
        }
    }

    public Task CheckShortcut() => check.IsEnabled ? Execute(true) : Task.CompletedTask;
    public async Task Violation()
    {
        if (current is null)
            return;
        var id = current.Id;
        await api.Post<JsonElement>($"tasks/{id}/violation");
        var update = await api.Get<StudentTask>($"tasks/{id}");
        if (current?.Id == id)
        {
            current = update;
            Policy();
        }
    }

    async Task Glossary()
    {
        await SaveDraft();
        var glossary = await api.Get<Dictionary<string, string>>("glossary");
        var window = new Window
        {
            Title = "Справочник SQL",
            Width = 700,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var filter = Ui.Input();
        filter.Watermark = "Поиск термина";
        var list = Ui.Stack();
        void Update()
        {
            list.Children.Clear();
            foreach (var entry in glossary.Where(g => g.Key.Contains(filter.Text ?? "", StringComparison.OrdinalIgnoreCase)))
                list.Children.Add(new Expander { Header = entry.Key, Content = Ui.Text(entry.Value) });
        }

        filter.TextChanged += (_, _) => Update();
        Update();
        var layout = new Grid
        {
            RowDefinitions = new("Auto,*"),
            RowSpacing = 12
        };
        layout.Children.Add(filter);
        var scroll = Ui.Scroll(list);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        window.Content = new Border
        {
            Padding = new Thickness(20),
            Child = layout
        };
        await window.ShowDialog(owner);
    }

    public void Dispose()
    {
        disposed = true;
        generation++;
        poll.Stop();
        debounce?.Cancel();
        debounce?.Dispose();
        ThemeManager.Changed -= ApplyTheme;
    }

    sealed record TaskChoice(string Id, string Title)
    {
        public override string ToString() => Title;
    }
}

static class SqlHighlighting
{
    public static IHighlightingDefinition Create(string ink)
    {
        var keyword = ink == "#FFFFFF" ? "#8DD9D3" : "#145D83";
        var comment = ink == "#FFFFFF" ? "#B1C1CE" : "#526770";
        var literal = ink == "#FFFFFF" ? "#F1D699" : "#885516";
        var xml = $"<SyntaxDefinition name='SQL' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'><Color name='Keyword' foreground='{keyword}' fontWeight='bold'/><Color name='Comment' foreground='{comment}'/><Color name='String' foreground='{literal}'/><RuleSet ignoreCase='true'><Span color='Comment' begin='--' end='\n'/><Span color='Comment' begin='/\\*' end='\\*/' multiline='true'/><Span color='String' begin=\"'\" end=\"'\"/><Keywords color='Keyword'><Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>JOIN</Word><Word>ON</Word><Word>GROUP</Word><Word>ORDER</Word><Word>BY</Word><Word>INSERT</Word><Word>UPDATE</Word><Word>DELETE</Word><Word>CREATE</Word><Word>TABLE</Word><Word>ALTER</Word><Word>WITH</Word><Word>BEGIN</Word><Word>COMMIT</Word><Word>LIMIT</Word><Word>AS</Word><Word>AND</Word><Word>OR</Word><Word>NULL</Word><Word>VALUES</Word></Keywords></RuleSet></SyntaxDefinition>";
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml));
        return AvaloniaEdit.Highlighting.Xshd.HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
