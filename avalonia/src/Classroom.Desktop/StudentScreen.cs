using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed partial class StudentScreen : UserControl, IDisposable
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    SqlLocalStore local = null!;
    bool previewOnly;
    string? lessonId;
    string workScope = "course";
    bool retiredWork;
    internal string CurrentWorkScope => workScope;
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
    readonly ContentControl result = new(){HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
    readonly TextEditor editor = new()
    {
        ShowLineNumbers = true,
        FontFamily = new FontFamily("Consolas, Cascadia Code, Menlo"),
        FontSize = 14,
        MinHeight = 0,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };
    readonly TextBlock saved = Ui.Text("");
    readonly TextBlock pause = Ui.Text("");
    readonly TextBlock restrictionNotice = Ui.Text("", 14);
    readonly TextBlock restrictionFeedback = Ui.Text("", 14);
    readonly TextBlock pageText = Ui.Text("");
    readonly StackPanel pagination;
    readonly Button[] categoryTabs;
    readonly StackPanel topicSections=new(){Spacing=0};
    readonly ProgressBar progressBar=new(){Height=3,Minimum=0,Maximum=100,Margin=new Thickness(0,12,0,8)};
    readonly Button nextHint;
    readonly Button run;
    readonly Button check;
    readonly DispatcherTimer poll = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    readonly SemaphoreSlim draftGate = new(1, 1);
    long lastInput = Environment.TickCount64;
    CatalogueDto? catalogue;
    StudentTask? current;
    PendingExecution? pending;
    int page;
    long generation;
    bool loading, syncing, disposed, executing;
    bool openingHint;
    readonly Button resolveConflict;
    readonly Button retrySave;
    CancellationTokenSource? debounce;
    public bool Restricted => current?.Restricted == true;

    public StudentScreen(MainWindow owner, ClassroomApi api)
    {
        this.owner = owner;
        this.api = api;
        if (api.ProfileId is not null) local = owner.SqlStore(api);
        lessonId = api.LessonId;
        AddHandler(InputElement.KeyDownEvent, (_, _) => lastInput = Environment.TickCount64, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerPressedEvent, (_, _) => lastInput = Environment.TickCount64, RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => lastInput = Environment.TickCount64, RoutingStrategies.Tunnel, true);
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
        categoryTabs=new[]{"Курс","Практика","Проекты"}.Select((label,index)=>
        {
            var button=Ui.Button(label,()=>{category.SelectedIndex=index;return Task.CompletedTask;});button.Classes.Add("nav-tab");button.Padding=new Thickness(4,6);button.MinHeight=32;button.MinWidth=0;button.HorizontalAlignment=HorizontalAlignment.Stretch;return button;
        }).ToArray();
        var sections=new UniformGrid{Columns=3};foreach(var tab in categoryTabs)sections.Children.Add(tab);
        var segment=new Border{Child=sections,CornerRadius=new CornerRadius(8),Padding=new Thickness(3)};Ui.BindBrush(segment,Border.BackgroundProperty,"Subtle");
        var categoryHost=new ContentControl{Content=segment,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        var categoryDisclosure=new Expander{Header=category.SelectedItem as string??"Курс",HorizontalContentAlignment=HorizontalAlignment.Stretch,HorizontalAlignment=HorizontalAlignment.Stretch};
        Avalonia.Automation.AutomationProperties.SetName(categoryDisclosure,"Раздел SQL");
        category.SelectionChanged+=(_,_)=>{categoryDisclosure.Header=category.SelectedItem as string??"Курс";categoryDisclosure.IsExpanded=false;};
        var progressHeading=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=6};
        progressHeading.Children.Add(Ui.Muted("Выполнено",12));progress.FontSize=12;progress.Classes.Add("muted");Grid.SetColumn(progress,1);progressHeading.Children.Add(progress);
        progressBar.Height=4;
        Ui.BindBrush(progressBar,ProgressBar.ForegroundProperty,"ProgressFill");Ui.BindBrush(progressBar,ProgressBar.BackgroundProperty,"ProgressTrack");
        Button? dictionaryButton=null;
        dictionaryButton=Ui.Button("Справочник",()=>owner.Safe(()=>OpenGlossary(dictionaryButton)));
        navigationBody=Ui.Scroll(Ui.Stack(categoryHost,search,topicSections,dictionaryButton));
        // These controls can leave StudentScreen's visual subtree for the
        // shared rail/overlay. Retain the previous activity-input semantics.
        navigationBody.AddHandler(InputElement.KeyDownEvent,(_,_)=>lastInput=Environment.TickCount64,RoutingStrategies.Tunnel,true);
        navigationBody.AddHandler(InputElement.PointerPressedEvent,(_,_)=>lastInput=Environment.TickCount64,RoutingStrategies.Tunnel,true);
        navigationBody.AddHandler(InputElement.PointerWheelChangedEvent,(_,_)=>lastInput=Environment.TickCount64,RoutingStrategies.Tunnel,true);
        navigationSummary=Ui.Stack(progressHeading,progressBar);
        navigationContainer=new Grid{RowDefinitions=new("*,Auto"),RowSpacing=10};
        navigationContainer.Children.Add(navigationBody);Grid.SetRow(navigationSummary,1);navigationContainer.Children.Add(navigationSummary);
        nextHint = Ui.Button("Открыть подсказку", () => owner.Safe(async () =>
        {
            if (!CanOpenHint()) return;
            openingHint = true;
            try
            {
                var id = current!.Id;
                var lesson = lessonId!;
                var scope = workScope;
                var stamp = generation;
                var index = current.OpenedHints.Length;
                local.Mark(lesson, scope, id, "hint-request", new(HintIndex: index));
                var update = await api.Post<StudentTask>($"tasks/{id}/hint", new HintRequest(index, lesson, scope));
                if (disposed) return;
                if (update.OpenedHints.Length > index)
                    local.Mark(lesson, scope, id, "hint-opened", new(HintIndex: index));
                if (stamp == generation && current?.Id == id && workScope == scope)
                {
                    current = update;
                    RenderHints();
                }
            }
            finally { openingHint = false; }
        }),CanOpenHint);
        run = Ui.Button("Запустить", () => owner.Safe(() => Execute(false)), () => CanExecute(false));
        check = Ui.Button("Проверить", () => owner.Safe(() => Execute(true)), () => CanExecute(true));
        Ui.Primary(check);
        resolveConflict=Ui.Button("Разрешить конфликт черновика",ShowDraftConflict);resolveConflict.IsVisible=false;
        retrySave=Ui.Button("Повторить сохранение",()=>owner.Safe(SaveDraft));retrySave.IsVisible=false;
        editor.TextChanged += (_, _) =>
        {
            RefreshEditingCommands();UpdateResultStale();
            if (loading || current is null)
                return;
            try { local.Edit(lessonId!, workScope, current.Id, editor.Text, editor.CaretOffset, editKind); RefreshSaveStatus(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { retrySave.IsVisible=true;saved.Text = "Не удалось сохранить на ПК: " + e.Message; }
            debounce?.Cancel();
            debounce = new();
            _ = SaveLater(debounce.Token);
        };
        editor.ContextMenu = null;
        // Commands can be invoked by accessibility, menus and input bindings without a KeyDown.
        // Consume them before AvaloniaEdit's default bindings as well as filtering shortcuts.
        foreach (var command in new[] { ApplicationCommands.Copy, ApplicationCommands.Cut, ApplicationCommands.Paste })
            editor.TextArea.CommandBindings.Insert(0, new RoutedCommandBinding(command, (_, e) =>
            {
                e.Handled = true;
                _ = owner.Safe(() => Violation(command == ApplicationCommands.Copy ? "copy" : command == ApplicationCommands.Cut ? "cut" : "paste"));
            }, (_, e) => e.CanExecute = Restricted));
        var grid = new Grid
        {
            ColumnDefinitions = new("240,*")
        };
        var sidebar=new Border{Child=navigationContainer,Padding=new Thickness(12,24,12,16),BorderThickness=new Thickness(0,0,1,0)};Ui.BindBrush(sidebar,Border.BackgroundProperty,"Surface");Ui.BindBrush(sidebar,Border.BorderBrushProperty,"Line");grid.Children.Add(sidebar);
        var workspace=CreateSqlWorkspace();
        heading.FontSize=26;heading.LineHeight=31.2;heading.FontWeight=FontWeight.SemiBold;heading.MaxLines=2;heading.TextTrimming=TextTrimming.CharacterEllipsis;
        var content=new Grid{RowDefinitions=new("Auto,*"),RowSpacing=16,Margin=new Thickness(16)};
        content.Children.Add(CreateSqlHeading());Grid.SetRow(workspace,1);content.Children.Add(workspace);Grid.SetColumn(content,1);grid.Children.Add(content);
        Content = grid;
        refreshNavigationLayout=()=>
        {
            sidebar.IsVisible=!navigationHosted;dictionaryButton.IsVisible=!navigationHosted;
            if(navigationHosted&&!ReferenceEquals(categoryHost.Content,categoryDisclosure))
            {
                categoryHost.Content=null;categoryDisclosure.Content=segment;categoryHost.Content=categoryDisclosure;sections.Columns=1;
            }
            else if(!navigationHosted&&!ReferenceEquals(categoryHost.Content,segment))
            {
                categoryHost.Content=null;categoryDisclosure.Content=null;categoryHost.Content=segment;sections.Columns=3;
            }
            grid.ColumnDefinitions[0].Width=new GridLength(navigationHosted?0:Bounds.Width<1200?220:240);
            content.Margin=new Thickness(navigationHosted?0:16);
        };
        SizeChanged += (_, _) =>refreshNavigationLayout();
        poll.Tick += async (_, _) => await Synchronize();
        ThemeManager.Changed += ApplyTheme;
        ConfigureHistory();
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        editor.Background = Application.Current!.Resources["EditorBrush"] as IBrush;
        editor.Foreground = Application.Current.Resources["EditorInkBrush"] as IBrush;
        editor.LineNumbersForeground = Application.Current.Resources["LineNumbersBrush"] as IBrush;
        editor.SyntaxHighlighting = SqlHighlighting.Create(ThemeManager.Ink(((SolidColorBrush)editor.Background!).Color.ToString()));
    }

    public async Task Load(string? taskId = null)
    {
        catalogue = await api.Get<CatalogueDto>("catalog");
        UpdateModules();
        poll.Start();
        if (catalogue.Tasks.Length > 0)
            await Select(taskId is not null && catalogue.Tasks.Any(t => t.Id == taskId) ? taskId : catalogue.Tasks.Any(t => t.Id == owner.LastTaskId) ? owner.LastTaskId! : catalogue.Tasks[0].Id);
    }

    internal async Task OpenTask(string? id)
    {
        catalogue = await api.Get<CatalogueDto>("catalog"); UpdateModules();
        var target = catalogue.Tasks.FirstOrDefault(t => t.Id == id) ?? catalogue.Tasks.FirstOrDefault();
        if (target is null) { retiredWork = true; await owner.ShowStudentHome(); return; }
        if (current?.Id == target.Id)
        {
            var updated = await api.Get<StudentTask>($"tasks/{target.Id}");
            if (!retiredWork && updated.WorkScope == workScope) return;
            await SaveDraft(); current = null;
        }
        category.SelectedItem = Category(target); module.SelectedItem = target.Module;
        await Select(target.Id);
    }

    // Same grouping as main's components/task-navigation.tsx.
    static string Category(CatalogueItem item) => item.Project is not null ? "Проекты" : System.Text.RegularExpressions.Regex.IsMatch(item.Module,@"^\d") ? "Курс" : "Практика";
    void UpdateModules()
    {
        if (catalogue is null)
            return;
        var selected = category.SelectedItem as string ?? "Курс";
        for(var i=0;i<categoryTabs.Length;i++)categoryTabs[i].Classes.Set("active",i==category.SelectedIndex);
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
        RefreshProgress();
        var text = search.Text?.Trim() ?? "";
        var filtered = catalogue.Tasks.Where(t => t.Module == (module.SelectedItem as string) && (text.Length == 0 || t.Title.Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        var pages = Math.Max(1, (filtered.Length + 7) / 8);
        page = Math.Clamp(page, 0, pages - 1);
        pagination.IsVisible = pages > 1;
        pageText.Text = pages > 1 ? $"{page + 1}/{pages}" : "";
        tasks.ItemsSource = filtered.Skip(page * 8).Take(8).Select((t, i) => new TaskChoice(t.Id, $"{page * 8 + i + 1:00}  {(catalogue.Completed.Contains(t.Id) ? "✓ " : "")}{t.Title}")).ToArray();
        RenderTopics();
    }
    void RenderTopics()
    {
        if(catalogue is null)return;
        if(tasks.Parent is Panel oldTasks)oldTasks.Children.Remove(tasks);if(pagination.Parent is Panel oldPage)oldPage.Children.Remove(pagination);
        topicSections.Children.Clear();
        foreach(var topic in module.Items.OfType<string>())
        {
            var selected=topic==module.SelectedItem as string;var members=catalogue.Tasks.Where(t=>t.Module==topic).ToArray();
            var title=Ui.Text(System.Text.RegularExpressions.Regex.Replace(topic,@"^\d+\s*[·.]\s*",""),13);
            var label=new Grid{ColumnDefinitions=new("*,Auto,Auto"),ColumnSpacing=8};label.Children.Add(title);var count=Ui.Muted($"{members.Count(t=>catalogue.Completed.Contains(t.Id))}/{members.Length}",11);Grid.SetColumn(count,1);label.Children.Add(count);var arrow=Ui.Muted(selected?"⌃":"⌄",14);Grid.SetColumn(arrow,2);label.Children.Add(arrow);
            var button=Ui.Button("",()=>{module.SelectedItem=topic;return Task.CompletedTask;});button.Content=label;button.HorizontalAlignment=HorizontalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Stretch;button.Padding=new Thickness(8,13);button.BorderThickness=new Thickness(0);button.Background=Brushes.Transparent;
            Avalonia.Automation.AutomationProperties.SetName(button,"Тема: "+title.Text);topicSections.Children.Add(button);
            if(selected){topicSections.Children.Add(tasks);topicSections.Children.Add(pagination);}
            var divider=new Border{Height=1,Margin=new Thickness(0,8)};Ui.BindBrush(divider,Border.BackgroundProperty,"Line");topicSections.Children.Add(divider);
        }
        if(current is not null)tasks.SelectedItem=tasks.Items.OfType<TaskChoice>().FirstOrDefault(t=>t.Id==current.Id);
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
        if (current is not null && historyVisible) RecordHistory("work-leave");
        DisplayTask(task);
        lastInput = Environment.TickCount64;
        local.Action(lessonId!, workScope, id, "opened");
        await FlushEvents();
        if (executing || task.LockedUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            return;
        var preview = await new PendingExecution(id, "", false, lessonId, task.WorkScope).Send(api);
        if (stamp != generation || disposed)
            return;
        RenderTables(preview.Tables);
    }

    void DisplayTask(StudentTask task)
    {
        workScope = task.WorkScope;
        retiredWork = false;
        current = task;
        tasks.SelectedItem=tasks.Items.OfType<TaskChoice>().FirstOrDefault(t=>t.Id==task.Id);
        restrictionFeedback.Text = "";
        restrictionFeedback.IsVisible = false;
        owner.LastTaskId = task.Id;
        var stored = local.Observe(lessonId!, task);
        pending = local.Pending(lessonId!, workScope, task.Id);
        heading.Text = task.Title;ToolTip.SetTip(heading,task.Title+$" · Вариант {task.VariantIndex}");
        prompt.Text = task.Prompt;
        explanation.Children.Clear();
        explanation.Children.Add(Ui.Text(task.Concept,14));
        explanation.Children.Add(Ui.Muted("Пример",13));
        var example=Ui.Text(task.Example,14);example.FontFamily=new FontFamily("Consolas, monospace");var exampleBox=new Border{Child=example,CornerRadius=new CornerRadius(8),Padding=new Thickness(14)};Ui.BindBrush(exampleBox,Border.BackgroundProperty,"Subtle");explanation.Children.Add(exampleBox);
        var terms = new ComboBox
        {
            ItemsSource = task.Terms
        };
        var definition = Ui.Text("");
        terms.SelectionChanged += (_, _) => definition.Text = task.Definitions.GetValueOrDefault(terms.SelectedItem as string ?? "", "");
        explanation.Children.Add(new Expander { Header = "Термины", Content = Ui.Stack(terms, definition) });
        loading = true;
        editor.Text = stored.Code;
        editor.CaretOffset = Math.Clamp(stored.Caret, 0, editor.Text.Length);
        loading = false;
        RefreshSaveStatus();
        RenderHints();
        Policy();
        ResetSqlResult();resetMaterials();
        tables.Children.Clear();
    }

    internal void Preview(CatalogueDto catalog, StudentTask task, ExecutionDto preview)
    {
        if (local is null)
        {
            previewOnly = true; lessonId = Guid.NewGuid().ToString("N");
            local = new SqlLocalStore(Path.Combine(Path.GetTempPath(), "classroom-sql-preview-" + Guid.NewGuid().ToString("N")), api.Connection.Fingerprint, "preview");
        }
        catalogue = catalog;
        UpdateModules();
        module.SelectedItem = task.Module;
        DisplayTask(task);
        RenderTables(preview.Tables);
        if(preview.Error is not null||preview.Output.Columns.Length>0||preview.Output.Values.Length>0)ShowSqlResult(preview,editor.Text,false);
    }

    void RenderHints()
    {
        if (current is null)
            return;
        hints.Children.Clear();
        foreach (var hint in current.OpenedHints)
            hints.Children.Add(Ui.Text(hint));
        nextHint.IsVisible = current.OpenedHints.Length < current.HintTotal;
        hintEnd.Text=!nextHint.IsVisible&&current.HintTotal>0?"Все подсказки открыты":"";
        hintEnd.IsVisible=hintEnd.Text.Length>0;
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
        restrictionNotice.Text = current.Restricted
            ? "Запрещено: Ctrl+C, Ctrl+X, Ctrl+V и снимки этого окна.\nПопытка работы с буфером — пауза на 30 секунд."
            : "Ограничения преподавателем не включены. Копирование и вставка разрешены.";
        if (!current.Restricted)
        {
            restrictionFeedback.Text = "";
            restrictionFeedback.IsVisible = false;
        }
        editor.IsEnabled = !locked;
        run.IsEnabled = CanExecute(false);
        check.IsEnabled = CanExecute(true);
        nextHint.IsEnabled = CanOpenHint();
        pause.Text = locked ? "Работа приостановлена на 30 секунд за запрещённое действие. Дождитесь снятия ограничения." : "";
        owner.Protect(current.Restricted);
        refreshWorkspacePolicy();UpdateResultCount();
    }

    bool CanExecute(bool checking) => !disposed && !executing && current is not null && current.LockedUntil <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && !(checking && current.Completed && current.Project is not null);
    bool CanOpenHint()=>!disposed&&!openingHint&&current is { } task&&
        task.LockedUntil<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()&&task.OpenedHints.Length<task.HintTotal;

    void RefreshProgress()
    {
        if(catalogue is null)return;
        progress.Text=$"{catalogue.Completed.Length} из {catalogue.Tasks.Length}";
        progressBar.Value=catalogue.Tasks.Length==0?0:catalogue.Completed.Length*100d/catalogue.Tasks.Length;
    }

    async Task Execute(bool checking)
    {
        if (!CanExecute(checking))
            return;
        executing = true;
        var stamp = generation;
        var id = current!.Id;
        var code = editor.Text;
        PendingExecution? submitted = null;
        try
        {
            Policy();
            var remembered = local.Pending(lessonId!, workScope, id);
            await SaveDraft();
            if (stamp != generation || disposed)
                return;
            // A failed transport keeps pending; the next attempt retries exactly that operation.
            if (local.Find(lessonId!, workScope, id)?.Conflict is not null) throw new InvalidDataException("Сначала разрешите конфликт черновика.");
            var retry = remembered is not null || local.Pending(lessonId!, workScope, id) is not null;
            pending = remembered ?? local.Pending(lessonId!, workScope, id) ?? new PendingExecution(id, code, checking, lessonId, workScope);
            if (pending.TaskId != id)
                pending = local.Pending(lessonId!, workScope, id) ?? new(id, code, checking, lessonId, workScope);
            submitted = pending;
            local.Remember(pending);
            RefreshSaveStatus();
            var submittedCheck = pending.Request.Check;
            if (retry)
                owner.Message("Повторяется незавершённая операция с прежним SQL.");
            var response = await submitted.Send(api);
            local.Acknowledge(submitted, response);
            if (ReferenceEquals(pending, submitted))
                pending = null;
            if (stamp != generation || disposed)
                return;
            if (response.Error is null) RenderTables(response.Tables);
            ShowSqlResult(response,submitted.Request.Code,submittedCheck,id);

            catalogue = await api.Get<CatalogueDto>("catalog");
            RefreshProgress();
            var updated = await api.Get<StudentTask>($"tasks/{id}");
            if (stamp == generation)
            {
                current = updated;
                Policy();
            }
        }
        catch (ApiException e) when (e.Status is >= 400 and < 500 and not (401 or 429))
        {
            if (submitted is not null)
            {
                local.Reject(submitted, e.Message);
                if (ReferenceEquals(pending, submitted)) pending = null;
                RefreshSaveStatus();
            }
            throw;
        }
        finally
        {
            executing = false;
            if (!disposed)
            {
                RefreshSaveStatus();Policy();
            }
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
        catch (Exception e) when (e is HttpRequestException or ApiException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            RefreshSaveStatus();
        }
    }

    public void SaveLocalDraft()
    {
        if (current is not null && local.Find(lessonId!, workScope, current.Id)?.Code != editor.Text)
            local.Edit(lessonId!, workScope, current.Id, editor.Text, editor.CaretOffset);
    }

    public async Task SaveDraft()
    {
        await draftGate.WaitAsync();
        try
        {
            if (current is null) return;
            SaveLocalDraft();
            using var transfer = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await local.Drain(api, transfer.Token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
            RefreshSaveStatus();
        }
        finally
        {
            draftGate.Release();
        }
    }

    async Task Synchronize()
    {
        if (syncing || disposed)
            return;
        syncing = true;
        try
        {
            await local.Drain(api);
            if (disposed || current is null || retiredWork) return;
            var id = current.Id;
            var update = await api.Get<StudentTask>($"tasks/{id}?workScope={Uri.EscapeDataString(workScope)}");
            if (current?.Id != id || disposed)
                return;
            if (update.WorkScope != workScope)
            {
                await SaveDraft(); retiredWork = true; await owner.ShowStudentHome();
                owner.Message("Преподаватель изменил выдачу. Прежний черновик сохранён; откройте работу в «Моё занятие».");
                return;
            }
            for (var hint = current.OpenedHints.Length; hint < update.OpenedHints.Length; hint++)
                RecordHistory("hint-opened", new(HintIndex: hint));
            current = update;
            Policy();
            RenderHints();
            await FlushEvents();
            await SaveDraft();
            if (current?.Id != id || disposed)
                return;
            if (owner.IsActive && owner.IsLessonVisible && Environment.TickCount64 - lastInput < 120000 && !current.Completed && current.LockedUntil <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                local.Action(lessonId!, workScope, id, "activity", 2);
                await local.Drain(api);
            }
        }
        catch (ApiException e) when (e.Status == 401)
        {
            await owner.Reauthenticate();
        }
        catch (ApiException e) when (e.Status is 403 or 409)
        {
            await owner.Safe(async () => { await SaveDraft(); retiredWork = true; await owner.ShowStudentHome(); owner.Message("Преподаватель изменил выдачу. Прежняя работа сохранена."); });
        }
        catch (ApiException e) when (e.Status == 423)
        {
            // The main window explains the class pause/end. Retain pending saves without a false network error.
            try { await SaveDraft(); }
            catch (Exception savingError) when (savingError is HttpRequestException or ApiException or TaskCanceledException)
            { RefreshSaveStatus(); }
        }
        catch (Exception e) when (e is HttpRequestException or ApiException or TaskCanceledException)
        {
            RefreshSaveStatus();
            owner.Error("Связь с преподавателем потеряна. Запуск SQL недоступен; локально сохранённый черновик будет передан после подключения.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            RefreshSaveStatus();
            owner.Error("Не удалось записать локальное подтверждение. Очередь сохранена для повторной передачи. " + e.Message);
        }
        finally
        {
            syncing = false;
            RefreshSaveStatus();
        }
    }

    public Task CheckShortcut() => check.IsEnabled ? Execute(true) : Task.CompletedTask;
    public Task Violation() => Violation("clipboard");
    public async Task Violation(string action)
    {
        if (current?.Restricted != true)
            return;
        ShowRestrictionFeedback("Копирование, вырезание и вставка запрещены преподавателем.");
        var id = current.Id;
        local.Action(lessonId!, workScope, id, action);
        await FlushEvents();
        if (previewOnly) return;
        var update = await api.Get<StudentTask>($"tasks/{id}");
        if (current?.Id == id)
        {
            current = update;
            Policy();
        }
    }

    public void CaptureAttempt()
    {
        if (Restricted)
        {
            ShowRestrictionFeedback("Снимки этого окна запрещены преподавателем.");
            try { local.Action(lessonId!, workScope, current!.Id, "capture"); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { saved.Text = "Не удалось сохранить событие на ПК: " + e.Message; return; }
            _ = owner.Safe(FlushEvents);
        }
    }

    Task FlushEvents() => previewOnly ? Task.CompletedTask : local.Drain(api);

    void ShowRestrictionFeedback(string text)
    {
        restrictionFeedback.Text = text;
        restrictionFeedback.IsVisible = true;
        owner.Error(text);
    }

    async Task Glossary(Control? trigger)
    {
        if (owner.IsGlossaryOpen) return;
        await SaveDraft();
        var glossary = await api.Get<Dictionary<string, string>>("glossary");
        if (disposed || trigger?.IsEffectivelyVisible == false) return;
        var resumeHistory = historyVisible;
        RecordHistory("dictionary-open");
        if (resumeHistory)
        {
            RecordHistory("work-leave");
            historyVisible = false;
        }
        try { await owner.ShowGlossaryDialog(glossary, trigger); }
        finally
        {
            RecordHistory("dictionary-close");
            if (resumeHistory && !disposed && owner.IsLessonVisible)
            {
                historyVisible = true;
                historyFocused = owner.IsActive;
                RecordHistory("work-open");
            }
        }
    }


    public void Dispose()
    {
        if (disposed) return;
        if (historyVisible) RecordHistory("work-close");
        ReleaseHistory();
        disposed = true;
        generation++;
        poll.Stop();
        debounce?.Cancel();
        debounce?.Dispose();
        ThemeManager.Changed -= ApplyTheme;
        if (previewOnly) local.Dispose();
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
