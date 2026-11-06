using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using Classroom.Client;
using Classroom.Contracts;
using Path = System.IO.Path;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen : UserControl, IDisposable
{
    readonly MainWindow owner;
    readonly string savePath;
    readonly CircuitViewport viewport;
    readonly TextBlock zoomText=Ui.Text("100%",12);
    readonly ElectronicsSync? sync;
    readonly ElectronicsHistoryStore? history;
    ElectronicsAssignmentDto? assignment;
    readonly Button? submitButton;
    public string? AssignmentId => assignment?.Id;
    internal string SavePath => savePath;
    public bool AssignmentAvailable => assignment?.Available ?? true;
    readonly ElectronicsResultsView? resultsView;
    readonly TextBlock syncStatus = Ui.Text("",12);
    readonly TextBlock reviewStatus = Ui.Text("",13);
    CircuitProject project = CircuitProject.Blink();
    readonly Canvas canvas = new() { Width = 1800, Height = 1200, Background = Brushes.WhiteSmoke };
    readonly StackPanel properties = Ui.Stack();
    readonly TextEditor editor = new() { ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 15, SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C++") };
    readonly TextBox monitor = Ui.Input("", true);
    readonly TextBlock status = Ui.Text("");
    readonly TextBox name = new() { MinHeight = 36, MaxLength = 120 };
    readonly Grid workGrid = new() { ColumnDefinitions = new("280,*,0"), ColumnSpacing = 10 };
    readonly WrapPanel partCards = new() { Orientation = Orientation.Horizontal };
    readonly TextBox partSearch = new() { Watermark = "Поиск деталей…", MinHeight = 36 };
    readonly ComboBox partCategory = new() { MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly CheckBox simulationOnly = new() { Content = "С симуляцией", FontSize = 12 };
    readonly CheckBox favoritesOnly = new() { Content = "Избранное", FontSize = 12 };
    readonly TextBlock catalogCount = Ui.Text("", 12);
    readonly TextBlock codeGuideText = Ui.Text("", 12);
    readonly Button makeTemplate;
    readonly ComboBox wireColor = new() { ItemsSource = new[] { "Зелёный провод", "Красный провод", "Чёрный провод", "Синий провод" }, SelectedIndex = 0, MinHeight = 36, Margin = new Thickness(0,0,8,8) };
    readonly CheckBox showGrid = new() { Content = "Сетка", Margin = new Thickness(0,0,8,8) };
    readonly List<string> undo = [], redo = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer autosave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly FirmwareRunner firmware = new();
    CircuitResult? result;
    Terminal? startPin;
    string? selected;
    string? selectedWire;
    bool loading, running, ticking, disposed;
    double zoom = 1;
    int runId;
    CancellationTokenSource? compilation;
    PinDrive[]? outputs;

    public ElectronicsScreen(MainWindow owner, string path, ClassroomApi? classroom = null, bool teacher = false, ElectronicsAssignmentDto? assignment = null)
    {
        this.owner = owner; savePath = path;
        this.assignment = assignment;
        try
        {
        if (assignment is not null) project = CircuitProject.Parse(assignment.ProjectJson);
        ElectronicsHistoryContext? recoveryContext = classroom?.ProfileId is { } studentId && !teacher ? new(studentId, classroom.Connection.Fingerprint, classroom.LessonId!, assignment?.Id) : null;
        var priorQueue = recoveryContext is not null && ElectronicsOutboxRecovery.Ensure(path, recoveryContext, assignment?.TaskId);
        string? expectedClientId = null;
        if (priorQueue && !File.Exists(path)) project = CircuitProject.Parse(ElectronicsSync.ReadOutbox(path + ".sync.json", assignment?.Id).ProjectJson);
        InitializeManipulation();
        string? loadError = null;
        try { if (File.Exists(path)) project = CircuitProject.Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            if (classroom is null || teacher) savePath = path + ".recovered-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".json";
            loadError = "Не удалось прочитать сохранённую схему. Исходный файл оставлен без изменений; новая работа сохраняется отдельно. " + e.Message;
        }
        if (classroom?.ProfileId is { } profile && !teacher)
        {
            history = new ElectronicsHistoryStore(path + ".history", new(profile, classroom.Connection.Fingerprint, classroom.LessonId!, assignment?.Id), loadError is null ? project.Serialize() : null);
            try
            {
                try
                {
                    expectedClientId = ElectronicsHistoryStore.InspectBindings(path + ".history", recoveryContext!).ClientId;
                    if (File.Exists(savePath + ".sync.json")) ElectronicsSync.ReadOutbox(savePath + ".sync.json", assignment?.Id, expectedClientId);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
                { history.Dispose(); throw new ElectronicsOutboxRecoveryException(ElectronicsOutboxRecovery.Inspect(path, recoveryContext!, e.Message), assignment?.TaskId); }
                var restored = CircuitProject.Parse(history.ProjectJson);
                if (history.Reopened && (loadError is not null || restored.Serialize() != project.Serialize()))
                {
                    if (File.Exists(path)) { File.Copy(path, path + ".history-source-" + Guid.NewGuid().ToString("N"), false); loadError = "Открыта последняя проверенная версия из локальной истории. Несовпадающий исходный файл сохранён отдельной копией."; }
                    else loadError = "Схема восстановлена из последней проверенной локальной истории.";
                }
                project = restored;
            }
            catch { history.Dispose(); throw; }
        }
        if (classroom is not null && !teacher)
        {
            try
            {
                sync = new ElectronicsSync(classroom,savePath+".sync.json",text => syncStatus.Text=text,startTimer:false,assignmentId:assignment?.Id,receipt:OnReceipt,
                    journalDelivery:history is null ? null : token=>history.Deliver(classroom,sync!.ClientId,token),journalPending:history is null ? null : ()=>history.Pending,
                    requireExisting:priorQueue,expectedClientId:expectedClientId);
            }
            catch (Exception e) when (recoveryContext is not null && (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException))
            { history?.Dispose(); throw new ElectronicsOutboxRecoveryException(ElectronicsOutboxRecovery.Inspect(path,recoveryContext,e.Message),assignment?.TaskId); }
        }
        monitor.IsReadOnly = true;
        partCategory.ItemsSource = PartCatalog.Categories; partCategory.SelectedIndex = 0;
        partSearch.TextChanged += (_, _) => RenderLibrary();
        partCategory.SelectionChanged += (_, _) => RenderLibrary();
        simulationOnly.IsCheckedChanged += (_, _) => RenderLibrary(); favoritesOnly.IsCheckedChanged += (_, _) => RenderLibrary();
        var library = Ui.Stack(Ui.Text("Детали", 19), partSearch, partCategory, Ui.Row(simulationOnly, favoritesOnly), catalogCount, partCards); library.Spacing = 8;
        var layout = workGrid;
        var libraryCard=Ui.Card(Ui.Scroll(library));libraryCard.Padding=new Thickness(12,16);libraryCard.ClipToBounds=true;layout.Children.Add(libraryCard);
        var scroll = viewport = new CircuitViewport(canvas);viewport.ZoomRequested=ZoomAt;viewport.BackgroundPressed=()=>{selected=null;selectedWire=null;DrawSelection();Inspect();};
        var sceneLayer=new Grid();sceneLayer.Children.Add(viewport);
        var sceneCard=Ui.Card(sceneLayer);sceneCard.Padding=new Thickness(0);sceneCard.ClipToBounds=true;Grid.SetColumn(sceneCard, 1); layout.Children.Add(sceneCard);
        var inspector = Ui.Card(Ui.Scroll(properties));inspector.Padding=new Thickness(12,16);Grid.SetColumn(inspector, 2); layout.Children.Add(inspector);
        makeTemplate = Ui.Button("Создать шаблон по схеме", ApplyCodeTemplate);
        var codePage = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        codePage.Children.Add(new Expander { Header = "Подключения и настройка кода", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Ui.Stack(makeTemplate, new ScrollViewer { Content = codeGuideText, MaxHeight = 150 }) });
        Grid.SetRow(editor,1); codePage.Children.Add(editor);
        var workspace = BuildWorkspace(layout, libraryCard, inspector, codePage);
        Control body = workspace;
        TabControl? tabs = null;
        TabItem? resultsTab = null;
        if (classroom is not null && teacher)
        {
            resultsView = new ElectronicsResultsView(owner,classroom);
            resultsTab = new TabItem { Header = "Результаты учеников", Content = resultsView };
            tabs = new TabControl { Name = "ElectronicsWorkspaceTabs", ItemsSource = new[] { new TabItem { Header = "Рабочая область", Content = workspace }, resultsTab } };
            body = tabs;
            tabs.SelectionChanged += async (_,e) => { if(e.Source == tabs && tabs.SelectedItem == resultsTab) await owner.Safe(resultsView.Load); };
        }
        MenuItem Menu(string label,Func<Task> action){var item=new MenuItem{Header=label};item.Click+=async(_,_)=>await owner.Safe(action);return item;}
        var exampleMenu=new MenuItem{Header="Примеры",ItemsSource=CircuitExamples.Names.Select((label,index)=>Menu(label,()=>Replace(CircuitExamples.Create(index)))).ToArray()};
        var wireMenu=new MenuItem{Header="Цвет провода",ItemsSource=new[]{"Зелёный","Красный","Чёрный","Синий"}.Select((label,index)=>Menu(label,()=>{wireColor.SelectedIndex=index;return Task.CompletedTask;})).ToArray()};
        var fileMenu=new ContextMenu{ItemsSource=new object[]{Menu("Новая схема",()=>Replace(new CircuitProject())),Menu("Открыть…",Import),Menu("Сохранить JSON…",()=>Export(false)),Menu("Экспорт SVG…",()=>Export(true)),new Separator(),exampleMenu,wireMenu,new Separator(),Menu("Проверить код",()=>Compile(false))}};
        var fileButton=Ui.Button("Файл",()=>Task.CompletedTask);fileButton.ContextMenu=fileMenu;fileButton.Click+=(_,_)=>fileMenu.Open(fileButton);
        var toolbar=Ui.Row(fileButton,Ui.Button("Отменить",()=>Perform(()=>History(undo,redo,"undo"))),Ui.Button("Повторить",()=>Perform(()=>History(redo,undo,"redo"))));toolbar.Spacing=4;
        foreach(var button in toolbar.Children.OfType<Button>()){button.Padding=new Thickness(10,7);button.MinHeight=32;button.FontSize=12;}
        var toolbarCard=Ui.Card(toolbar);toolbarCard.Padding=new Thickness(5);toolbarCard.Margin=new Thickness(12);toolbarCard.HorizontalAlignment=HorizontalAlignment.Left;toolbarCard.VerticalAlignment=VerticalAlignment.Top;sceneLayer.Children.Add(toolbarCard);
        var mainActions=Ui.Row();
        if (classroom is not null && !teacher)
        {
            submitButton = Ui.Button(assignment is null ? "Отправить преподавателю" : "Отправить на проверку", () => owner.Safe(async () =>
            {
            if (this.assignment?.CanSubmit == false) { owner.Message("Работа уже отправлена на проверку."); return; }
            if (this.assignment is { } current && sync?.HasStaleSubmission(current.StateVersion) == true)
            {
                if (!await owner.Confirm("Преподаватель разрешил новую сдачу. Предыдущая неподтверждённая отправка останется в локальном архиве. Отправить текущую версию?")) return;
                sync.ArchiveStaleSubmissions(current.StateVersion);
            }
            Save(); if (!Report("submitted",project.Serialize())) return;
            if (this.assignment is not null) submitButton!.IsEnabled = false;
            if (await Synchronize()) owner.Message("Работа отправлена преподавателю.");
            else if (sync?.PendingSubmission == false) owner.Message("Работа отправлена преподавателю. История ещё передаётся.");
            else owner.Error("Работа сохранена локально, но передача пока не завершена. "+syncStatus.Text);
            }), CanSend);
            submitButton.IsEnabled = assignment?.CanSubmit ?? true;
            mainActions.Children.Add(submitButton);
        }
        mainActions.Children.Add(Ui.Button("Проверить код",()=>Compile(false)));mainActions.Children.Add(Ui.Primary(Ui.Button("Запустить",()=>Compile(true))));
        var stopButton=Ui.Button("■",()=>Perform(Stop));stopButton.Width=38;stopButton.Padding=new Thickness(0);stopButton.HorizontalContentAlignment=HorizontalAlignment.Center;stopButton.VerticalContentAlignment=VerticalAlignment.Center;ToolTip.SetTip(stopButton,"Остановить симуляцию");Avalonia.Automation.AutomationProperties.SetName(stopButton,"Остановить");mainActions.Children.Add(stopButton);
        showGrid.Content="▦";showGrid.FontSize=16;showGrid.Margin=new Thickness(4,0);ToolTip.SetTip(showGrid,"Сетка");Avalonia.Automation.AutomationProperties.SetName(showGrid,"Сетка");showGrid.IsCheckedChanged+=(_,_)=>Draw();
        var zoomControls=Ui.Row(Ui.Button("−",()=>Perform(()=>ZoomAt(new(viewport.Bounds.Width/2,viewport.Bounds.Height/2),zoom/1.2))),zoomText,Ui.Button("+",()=>Perform(()=>ZoomAt(new(viewport.Bounds.Width/2,viewport.Bounds.Height/2),zoom*1.2))),Ui.Button("Вписать",()=>Perform(FitScene)),showGrid);zoomControls.Spacing=4;zoomText.VerticalAlignment=VerticalAlignment.Center;zoomText.Width=44;zoomText.TextAlignment=TextAlignment.Center;
        foreach(var button in zoomControls.Children.OfType<Button>()){button.Padding=new Thickness(10,7);button.MinHeight=32;button.FontSize=12;}
        var zoomCard=Ui.Card(zoomControls);zoomCard.Padding=new Thickness(5);zoomCard.Margin=new Thickness(12);zoomCard.HorizontalAlignment=HorizontalAlignment.Right;zoomCard.VerticalAlignment=VerticalAlignment.Bottom;sceneLayer.Children.Add(zoomCard);
        ToolTip.SetTip(viewport,"Колесо — масштаб. Средняя или правая кнопка мыши — перемещение. Пробел + перетаскивание — перемещение.");
        name.TextChanged += (_, _) => { if (loading) return; project.Name = name.Text ?? ""; _ = Perform(() => { RecordHistory("rename"); QueueSave(); }); };
        editor.TextChanged += (_, _) => { if (loading) return; if (running) Stop(); project.Code = editor.Text; _ = Perform(() => { RecordHistory(codeHistoryKind); QueueSave(); }); };
        autosave.Tick += (_, _) => { autosave.Stop(); _ = Perform(Save); };
        timer.Tick += async (_, _) => await Tick();
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(16,14,16,10), RowSpacing = 8 };
        name.Width=280;name.FontSize=18;name.FontWeight=FontWeight.SemiBold;name.HorizontalAlignment=HorizontalAlignment.Left;ToolTip.SetTip(name,"Название проекта");
        var projectBar=new Grid{ColumnDefinitions=new("*,Auto"),Margin=new Thickness(8,0)};projectBar.Children.Add(name);Grid.SetColumn(mainActions,1);projectBar.Children.Add(mainActions);root.Children.Add(projectBar);
        status.Classes.Add("muted");status.FontSize=12;syncStatus.Classes.Add("muted");
        status.IsVisible=false;status.PropertyChanged+=(_,e)=>{if(e.Property==TextBlock.TextProperty)status.IsVisible=!string.IsNullOrWhiteSpace(status.Text);};
        syncStatus.PropertyChanged+=(_,e)=>{if(e.Property==TextBlock.TextProperty)syncStatus.IsVisible=classroom is not null&&!teacher&&!string.IsNullOrWhiteSpace(syncStatus.Text);};
        Grid.SetRow(body, 1); root.Children.Add(body);
        syncStatus.IsVisible = false;
        var footer = Ui.Stack(status,syncStatus);footer.Spacing=2; Grid.SetRow(footer, 2); root.Children.Add(footer);
        if (assignment is not null)
        {
            root.RowDefinitions = new("Auto,Auto,*,Auto");
            var statement = Ui.Scroll(Ui.Text(assignment.Prompt)); statement.MaxHeight = 100;
            reviewStatus.IsVisible = false;
            reviewStatus.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) reviewStatus.IsVisible = !string.IsNullOrWhiteSpace(reviewStatus.Text); };
            var review = Ui.Scroll(reviewStatus); review.MaxHeight = 110;
            var condition = Ui.Card(Ui.Stack(Ui.Text(assignment.Title, 20), statement, review));
            root.Children.Add(condition);
            Grid.SetRow(projectBar, 1); Grid.SetRow(body, 2); Grid.SetRow(footer, 3);
        }
        if (tabs is not null) tabs.SelectionChanged += (_,e) =>
        {
            if(e.Source != tabs) return;
            var results = resultsTab is not null && tabs.SelectedItem == resultsTab;
            toolbarCard.IsVisible = !results;toolbar.IsVisible=!results;mainActions.IsVisible=!results; name.IsVisible = !results; footer.IsVisible = !results;
            if(results) { Stop(); Save(); }
        };
        Content = root;
        ConfigureHistory();
        Ui.BindBrush(editor,TextEditor.BackgroundProperty,"Editor");Ui.BindBrush(editor,TextEditor.ForegroundProperty,"EditorInk");editor.Padding=new Thickness(16,20);ThemeManager.Changed+=ApplyEditorTheme;ApplyEditorTheme();
        Refresh();
        var initialFit = true;
        layout.LayoutUpdated += (_, _) =>
        {
            if (!initialFit || scroll.Bounds.Width < 200 || scroll.Bounds.Height < 100) return;
            initialFit = false;
            FitScene();
        };
        status.Text = loadError ?? "";
        if (classroom is not null && !teacher)
        {
            Save(); sync?.Start();
        }
        }
        catch
        {
            disposed = true; autosave.Stop(); timer.Stop(); ReleaseHistory(); sync?.Dispose(); history?.Dispose();
            ThemeManager.Changed -= ApplyEditorTheme; firmware.Dispose(); throw;
        }
    }

    void OnReceipt(ElectronicsSyncReceipt receipt)
    {
        if (history is not null) foreach (var id in receipt.ReportIds)
        {
            var reference = history.Journal.Changes.LastOrDefault(c => c.ReportId == id);
            if (reference?.ReportHash is { } hash && reference.Kind != "report-received")
                _ = Perform(() => RecordHistory("report-received", true, reportId: id, reportHash: hash));
        }
        if (assignment is null || receipt.CanSubmit is null || receipt.AssignmentVersion < assignment.StateVersion) return;
        assignment = assignment with { State = receipt.AssignmentState ?? assignment.State, CanSubmit = receipt.CanSubmit.Value, StateVersion = receipt.AssignmentVersion ?? assignment.StateVersion };
        UpdateSubmit();
    }
    public void UpdateAssignment(ElectronicsAssignmentDto value)
    {
        if (assignment?.Id != value.Id) throw new InvalidDataException("Это другая электронная работа.");
        if (value.StateVersion < assignment.StateVersion) return;
        assignment = value;
        UpdateSubmit();
    }
    public void UpdateAssignmentStatus(ElectronicsAssignmentStatus value)
    {
        if (assignment?.Id != value.Id || value.StateVersion < assignment.StateVersion) return;
        assignment = assignment with { State = value.State, CanSubmit = value.CanSubmit, Available = value.Available, StateVersion = value.StateVersion };
        reviewStatus.Text = value.LatestReview is { } review ? $"{ElectronicsReviewLabels.State(review.Action)}{(review.Grade is { } grade ? $" · Оценка: {grade}" : "")} · {ElectronicsLabels.Date(review.At)}\n{review.Comment}" : "";
        UpdateSubmit();
    }
    void UpdateSubmit()
    {
        if (submitButton is null || assignment is null) return;
        var stale = assignment.CanSubmit && sync?.HasStaleSubmission(assignment.StateVersion) == true;
        submitButton.IsEnabled = CanSend();
        submitButton.Content = stale ? "Отправить новую сдачу" : sync?.PendingSubmission == true ? "Ожидает передачи" : assignment.CanSubmit ? "Отправить на проверку" : ElectronicsReviewLabels.State(assignment.State);
    }
    bool CanSend() => assignment?.CanSubmit != false && (sync?.PendingSubmission != true || assignment is { } current && sync.HasStaleSubmission(current.StateVersion));

    Task Perform(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { status.Text = e.Message; owner.Error(e.Message); }
        return Task.CompletedTask;
    }
    void Checkpoint()
    {
        Stop(); undo.Add(project.Serialize()); if (undo.Count > 50) undo.RemoveAt(0); redo.Clear();
    }
    void History(List<string> from, List<string> to, string kind)
    {
        if (from.Count == 0) return;
        Stop(); to.Add(project.Serialize()); project = CircuitProject.Parse(from[^1]); from.RemoveAt(from.Count - 1); selected = null; selectedWire = null; startPin = null; Refresh(); Save(kind, true);
    }
    void QueueSave() { autosave.Stop(); autosave.Start(); }
    void Save() => Save("scheme-edit");
    void Save(string kind, bool force = false, string? gestureId = null)
    {
        project.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        var json = project.Serialize(); history?.Record(json, kind, force, gestureId); ElectronicsHistoryStore.Mirror(savePath, json);
        try { sync?.QueueDraft(project.Serialize()); }
        catch (Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException) { syncStatus.Text="Локальная схема сохранена. Не удалось сохранить очередь передачи."; owner.Error(e.Message); }
    }
    bool Report(string kind,string json,string detail="")
    {
        if(sync is null) return false;
        try { var id=Guid.NewGuid().ToString("N"); RecordHistory("report-"+kind,true,reportId:id,reportHash:SqlSyncHash.Text(json)); sync.Record(kind,json,detail,kind == "submitted" ? assignment?.StateVersion : null,id); return true; }
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException) { owner.Error(e.Message); return false; }
    }
    public async Task<bool> Synchronize() { Save(); return sync is null || await sync.Drain(); }
    void Refresh()
    {
        loading = true; name.Text = project.Name; editor.Text = project.Code; loading = false;
        var guide = CircuitCodeGuide.Build(project); codeGuideText.Text = guide.Notes; makeTemplate.IsEnabled = guide.Sketch is not null;
        result = CircuitSolver.Solve(project, outputs); Draw(); Inspect();
        RenderLibrary();
        UpdateSerialControls();
    }
    async Task ApplyCodeTemplate()
    {
        var guide = CircuitCodeGuide.Build(project);
        if (guide.Sketch is null || !await owner.Confirm("Заменить текущий код начальным шаблоном по соединениям? Изменение можно отменить.")) return;
        if (disposed) return;
        await Perform(() => { Checkpoint(); project.Code = guide.Sketch; Refresh(); Save("code-template"); });
    }
    void RenderLibrary()
    {
        partCards.Children.Clear();
        var query = partSearch.Text?.Trim() ?? "";
        var category = partCategory.SelectedItem as string;
        var definitions = PartCatalog.All.Where(d => (category is null or "Все категории" || d.Category == category)
            && (!simulationOnly.IsChecked.GetValueOrDefault() || d.Simulated)
            && (!favoritesOnly.IsChecked.GetValueOrDefault() || project.Favorites.Contains(d.Id))
            && (d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || d.ReferenceName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(d => d.Category == "Платы" ? 0 : d.Category == "Монтаж" ? 1 : 2).ThenBy(d => d.Category).ThenBy(d => d.Name).ToArray();
        catalogCount.Text = $"{definitions.Length} из {PartCatalog.All.Length} деталей";
        foreach (var definition in definitions)
        {
            var add = Ui.Button("", () => Perform(() => Add(definition)));
            add.Padding = new Thickness(6, 10); add.Width = 116; add.Height = 146;
            var summary = Ui.Stack(PartArtwork.Thumbnail(definition.Art),
                new TextBlock { Text = definition.Name, TextWrapping = TextWrapping.Wrap, FontSize = 11, Height = 31, MaxWidth = 105, TextAlignment = TextAlignment.Center },
                new TextBlock { Text = definition.Simulated ? "Симуляция" : "Для схемы", FontSize = 10, Foreground = definition.Simulated ? Brushes.SeaGreen : Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center });
            summary.Spacing = 4; add.Content = summary;
            Avalonia.Automation.AutomationProperties.SetName(add, "Добавить: " + definition.Name);
            ToolTip.SetTip(add, definition.Simulated ? definition.Name : definition.Name + "\nИзображение для схемы. Поведение в симуляторе пока не реализовано. Вид иллюстративный, не распиновка производителя.");
            var star=new PathIcon{Width=14,Height=14,Data=Geometry.Parse("M12,2.5 L14.9,8.4 L21.4,9.3 L16.7,13.9 L17.8,20.4 L12,17.3 L6.2,20.4 L7.3,13.9 L2.6,9.3 L9.1,8.4 Z")};Ui.BindBrush(star,PathIcon.ForegroundProperty,project.Favorites.Contains(definition.Id)?"Accent":"Muted");
            var favorite = new Button { Content = star, Padding = new Thickness(0), Width = 28, Height = 28, MinHeight = 0, MinWidth = 0, HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(0,4,4,0),CornerRadius=new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            Avalonia.Automation.AutomationProperties.SetName(favorite,"Избранное: "+definition.Name);
            ToolTip.SetTip(favorite, "Избранное");
            favorite.Click += (_, _) => _ = Perform(() => { if (!project.Favorites.Remove(definition.Id)) project.Favorites.Add(definition.Id); Save(); RenderLibrary(); });
            var card = new Grid { Margin = new Thickness(0, 0, 7, 7) }; card.Children.Add(add); card.Children.Add(favorite); partCards.Children.Add(card);
        }
    }
    void Add(PartDefinition definition)
    {
        var kind = definition.Kind;
        if (project.Parts.Count >= 80) throw new InvalidDataException("На схеме допускается до 80 компонентов.");
        if (kind is PartKind.Uno or PartKind.Mega && project.Parts.Any(p => AvrBoards.For(p) is not null)) throw new InvalidDataException("Пока доступна одна исполняемая плата на схему.");
        Checkpoint(); var id = Guid.NewGuid().ToString("N");
        var center=viewport.ToWorld(new(viewport.Bounds.Width/2,viewport.Bounds.Height/2));
        var part = new CircuitPart(id, kind, PartArtwork.Coordinate(center.X-70+project.Parts.Count%3*20), PartArtwork.Coordinate(center.Y-70+project.Parts.Count%3*20), definition.Value, ModelId: definition.Id, CustomPins: kind == PartKind.Visual ? definition.Pins ?? [] : null);
        var trial = CircuitProject.Parse(project.Serialize()); trial.Parts.Add(part); trial.Validate();
        project.Parts.Add(part);
        selected = id; selectedWire = null; Refresh(); Save("part-add");
    }
    async Task Replace(CircuitProject next)
    {
        if (project.Parts.Count > 0 && !await owner.Confirm("Заменить открытую схему? Её можно предварительно сохранить в JSON.")) return;
        await Perform(() => { Checkpoint(); project = next; selected = null; selectedWire = null; startPin = null; Refresh(); Save("project-replace",true); });
    }
    async Task Import()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = "Открыть схему", AllowMultiple = false, FileTypeFilter = [new("Схема JSON") { Patterns = ["*.json"] }] });
        if (files.Count == 0) return;
        await PerformAsync(async () =>
        {
            await using var stream = await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > 2_000_000) throw new InvalidDataException("Схема больше 2 МБ.");
            using var reader = new StreamReader(stream); var text = await reader.ReadToEndAsync();
            await Replace(CircuitProject.Parse(text));
        });
    }
    async Task Export(bool svg)
    {
        await PerformAsync(async () =>
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new() { Title = svg ? "Экспорт схемы SVG" : "Сохранить схему", SuggestedFileName = svg ? "circuit.svg" : "circuit.json" });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(svg ? Svg() : project.Serialize());
            status.Text = "Проект сохранён.";
        });
    }
    async Task PerformAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or UnauthorizedAccessException) { status.Text = e.Message; owner.Error(e.Message); }
    }
    void Inspect()
    {
        properties.Children.Clear();
        InspectPanelSelection();
        if (selectedWire is { } wireId && project.Wires.FirstOrDefault(w => w.Id == wireId) is { } wire)
        {
            properties.Children.Add(Ui.Text("Провод", 20));
            properties.Children.Add(Ui.Text(wire.From.Pin + " → " + wire.To.Pin));
            var color = Ui.Input(wire.Color);
            properties.Children.Add(Ui.Field("Цвет #RRGGBB", color));
            properties.Children.Add(Ui.Button("Применить", () => Perform(() =>
            {
                var trial = CircuitProject.Parse(project.Serialize());
                var index = trial.Wires.FindIndex(w => w.Id == wireId); trial.Wires[index] = wire with { Color = color.Text ?? "" }; trial.Validate();
                Checkpoint(); project = trial; Draw(); Save();
            })));
            properties.Children.Add(Ui.Button("Удалить провод", () => Perform(() => { Checkpoint(); project.Wires.RemoveAll(w => w.Id == wireId); selectedWire = null; Refresh(); Save(); })));
            return;
        }
        if (selected is null || project.Parts.FirstOrDefault(p => p.Id == selected) is not { } p)
        { properties.Children.Add(Ui.Text("Выберите компонент или провод.", 16)); return; }
        properties.Spacing=10;
        properties.Children.Add(Ui.Muted("СВОЙСТВА ДЕТАЛИ",10));properties.Children.Add(Ui.Text(PartLibrary.Name(p), 18));
        if (!PartCatalog.CanSimulate(p)){var badge=Ui.Muted("Только схема · без симуляции",12);ToolTip.SetTip(badge,"Симуляция поведения этой модели ещё не реализована.");properties.Children.Add(badge);}
        if (p.Kind == PartKind.Visual)
        {
            var pins = Ui.Input(string.Join(", ", PartLibrary.Pins(p)));
            var pinFields=Ui.Stack(Ui.Field("Названия через запятую",pins));
            pinFields.Children.Add(Ui.Button("Применить выводы", () => Perform(() =>
            {
                var names = (pins.Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var trial = CircuitProject.Parse(project.Serialize()); var index = trial.Parts.FindIndex(part => part.Id == p.Id);
                trial.Parts[index] = trial.Parts[index] with { CustomPins = names }; trial.Validate();
                Checkpoint(); project = trial; Refresh(); Save();
            })));
            properties.Children.Add(new Expander{Header=$"Контакты · {PartLibrary.Pins(p).Length}",Content=pinFields,HorizontalAlignment=HorizontalAlignment.Stretch});
        }
        if(p.Kind==PartKind.LcdI2c)InspectLcd(p);
        if (p.Kind is PartKind.Resistor or PartKind.Potentiometer or PartKind.Pot3 or PartKind.Led or PartKind.Diode or PartKind.Battery or PartKind.AnalogSensor or PartKind.Motor or PartKind.Buzzer)
        {
            var input = Ui.Input(p.Value.ToString(CultureInfo.InvariantCulture));
            properties.Children.Add(Ui.Field(p.Kind is PartKind.Resistor or PartKind.Potentiometer or PartKind.Pot3 or PartKind.Motor or PartKind.Buzzer ? "Сопротивление модели, Ом" : p.Kind is PartKind.Led or PartKind.Diode ? "Прямое напряжение, В" : "Напряжение, В", input));
            properties.Children.Add(Ui.Button("Применить", () => Perform(() =>
            {
                if (!double.TryParse(input.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw new InvalidDataException("Введите корректное число.");
                var next = p with { Value = value }; var trial = CircuitProject.Parse(project.Serialize()); trial.Parts[trial.Parts.FindIndex(x => x.Id == p.Id)] = next; trial.Validate();
                Checkpoint(); project.Parts[project.Parts.FindIndex(x => x.Id == p.Id)] = next; Refresh(); Save();
            })));
        }
        if (p.Kind is PartKind.Pot3 or PartKind.Potentiometer or PartKind.AnalogSensor)
        {
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = p.Kind == PartKind.Pot3 ? p.Setting : p.Kind == PartKind.AnalogSensor ? p.Value / 5 : Math.Clamp(p.Value / 100000, 0, 1) };
            properties.Children.Add(Ui.Field("Регулятор · можно менять во время симуляции", slider));
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                _ = Perform(() =>
                {
                    var i = project.Parts.FindIndex(part => part.Id == p.Id);
                    if (i < 0) return;
                    project.Parts[i] = p.Kind == PartKind.Pot3 ? project.Parts[i] with { Setting = slider.Value }
                        : project.Parts[i] with { Value = p.Kind == PartKind.AnalogSensor ? slider.Value * 5 : Math.Max(1, slider.Value * 100000) };
                    RecordHistory("control-change"); result = CircuitSolver.Solve(project, outputs); Draw(); QueueSave();
                });
            };
        }
        if (p.Kind == PartKind.Switch) properties.Children.Add(Ui.Button(p.Active ? "Разомкнуть" : "Замкнуть", () => Perform(() => Toggle(p.Id))));
        properties.Children.Add(Ui.Muted("ПРЕОБРАЗОВАНИЕ",10));
        var metrics=new Grid{ColumnDefinitions=new("*,*"),RowDefinitions=new("Auto,Auto"),ColumnSpacing=8,RowSpacing=8};
        Border Metric(string label,string value){var box=new Border{Child=Ui.Stack(Ui.Muted(label,11),Ui.Text(value,14)),Padding=new Thickness(10),CornerRadius=new CornerRadius(8)};Ui.BindBrush(box,Border.BackgroundProperty,"Subtle");return box;}
        metrics.Children.Add(Metric("Ширина",$"{p.Scale*p.StretchX*100:F0}%"));var height=Metric("Высота",$"{p.Scale*p.StretchY*100:F0}%");Grid.SetColumn(height,1);metrics.Children.Add(height);var rotation=Metric("Поворот",$"{PartArtwork.Degrees(p):F1}°");Grid.SetRow(rotation,1);Grid.SetColumnSpan(rotation,2);metrics.Children.Add(rotation);properties.Children.Add(metrics);
        SelectionColorControls();
        properties.Children.Add(Ui.Row(
            Ui.Button("Меньше", () => ChangeLayout(p.Id, -0.2, 0)),
            Ui.Button("Больше", () => ChangeLayout(p.Id, 0.2, 0))));
        properties.Children.Add(Ui.Row(
            Ui.Button("↶ 90°", () => ChangeLayout(p.Id, 0, -1)),
            Ui.Button("↷ 90°", () => ChangeLayout(p.Id, 0, 1))));
        properties.Children.Add(Ui.Button("Размер 100%", () => Perform(() => { Checkpoint();var i=project.Parts.FindIndex(x=>x.Id==p.Id);var before=PartArtwork.Body(p);var next=p with {Scale=1,StretchX=1,StretchY=1};var after=PartArtwork.Body(next);project.Parts[i]=next with {X=PartArtwork.Coordinate(p.X+(before.Width-after.Width)/2),Y=PartArtwork.Coordinate(p.Y+(before.Height-after.Height)/2)};Refresh();Save(); })));
        properties.Children.Add(Ui.Button("Отразить по горизонтали", () => Perform(() => { Checkpoint(); var i = project.Parts.FindIndex(x => x.Id == p.Id); project.Parts[i] = p with { Rotation = (p.Rotation + 1) % 2 }; Refresh(); Save(); })));
        properties.Children.Add(Ui.Button("Удалить компонент", () => Perform(() =>
        {
            Checkpoint(); project.Parts.RemoveAll(x => x.Id == p.Id); project.Wires.RemoveAll(w => w.From.Part == p.Id || w.To.Part == p.Id); selected = null; Refresh(); Save();
        })));
        var connections=Ui.Stack();
        foreach (var connectedWire in project.Wires.Where(w => w.From.Part == p.Id || w.To.Part == p.Id).ToArray())
            connections.Children.Add(Ui.Button($"Удалить {connectedWire.From.Pin} → {connectedWire.To.Pin}", () => Perform(() => { Checkpoint(); project.Wires.Remove(connectedWire); Refresh(); Save(); })));
        if(connections.Children.Count>0)properties.Children.Add(new Expander{Header=$"Соединения · {connections.Children.Count}",Content=connections});
    }
    Task ChangeLayout(string id, double scaleDelta, int turnDelta) => Perform(() =>
    {
        var i = project.Parts.FindIndex(p => p.Id == id); if (i < 0) return;
        var part = project.Parts[i];
        var scale = Math.Round(Math.Clamp(part.Scale + scaleDelta, 0.4, 2), 2);
        var turns = (part.QuarterTurns + turnDelta + 4) % 4;
        if (scale == part.Scale && turns == part.QuarterTurns) return;
        Checkpoint(); project.Parts[i] = PartArtwork.WithLayout(part, scale, turns); Refresh(); Save();
    });
    void Toggle(string id)
    {
        var i = project.Parts.FindIndex(p => p.Id == id); if (i < 0) return;
        // Interactive input may change while firmware runs; topology remains fixed.
        project.Parts[i] = project.Parts[i] with { Active = !project.Parts[i].Active };
        result = CircuitSolver.Solve(project, outputs); Draw(); Inspect(); Save();
    }

    static double PartHeight(CircuitPart part) => PartArtwork.Body(part).Height;
    static Point PinPoint(CircuitPart part, string pin)
    {
        return PartArtwork.Pin(part, pin);
    }
    void FitScene(){viewport.Fit(PartArtwork.Bounds(project.Parts),1.2);zoom=viewport.Zoom;Draw();}
    void ZoomAt(Point anchor,double scale)
    {
        var world=viewport.ToWorld(anchor);zoom=Math.Clamp(scale,0.1,3);viewport.SetView(zoom,new(anchor.X-world.X*zoom,anchor.Y-world.Y*zoom));Draw();
    }
    void Draw()
    {
        canvas.Children.Clear();canvas.Width=Math.Max(1,viewport.Bounds.Width);canvas.Height=Math.Max(1,viewport.Bounds.Height);canvas.ClipToBounds=false;
        viewport.ShowGrid=showGrid.IsChecked==true;viewport.SetView(zoom,viewport.Pan);zoomText.Text=$"{zoom*100:F0}%";
        DrawWires();
        foreach (var p in project.Parts)
        {
            var lit = result?.Parts.GetValueOrDefault(p.Id)?.Lit == true;
            var size = PartArtwork.Body(p);
            var group = new Canvas { Width = size.Width * zoom, Height = size.Height * zoom };
            Canvas.SetLeft(group, p.X * zoom); Canvas.SetTop(group, p.Y * zoom); canvas.Children.Add(group);
            if (lit || p.Kind == PartKind.Switch && p.Active)
                group.Children.Add(new Ellipse { Width = size.Width * zoom, Height = size.Height * zoom, Fill = p.Kind == PartKind.Switch ? Brushes.LightGreen : p.ModelId == "led-green" ? Brushes.LimeGreen : p.ModelId == "led-blue" ? Brushes.DodgerBlue : Brushes.OrangeRed, Opacity = 0.3 });
            var board = new Border { Width = size.Width * zoom, Height = size.Height * zoom, Background = Brushes.Transparent, Tag = p.Id, Cursor=new Cursor(StandardCursorType.SizeAll) };
            var baseSize = PartArtwork.BaseBody(p);
            var face = new Canvas { Width = baseSize.Width, Height = baseSize.Height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                RenderTransformOrigin = new RelativePoint(0,0,RelativeUnit.Relative), RenderTransform = new MatrixTransform { Matrix = PartArtwork.TransformMatrix(p, zoom) } };
            face.Children.Add(p.Kind == PartKind.Breadboard ? BreadboardFace(1) : p.Kind==PartKind.LcdI2c?new LcdDisplayFace(p,displays.GetValueOrDefault(p.Id)):PartArtwork.Thumbnail(PartCatalog.For(p).Art, baseSize.Width, baseSize.Height));
            board.Child = face;
            group.Children.Add(board);
            var title = new TextBlock { Text = PartLibrary.Name(p), FontSize = 12 * zoom, MaxWidth = Math.Max(80, size.Width) * zoom, TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetTop(title, -22 * zoom); group.Children.Add(title); title.IsHitTestVisible = false;
            Point origin = default; CircuitPart drag = p; bool moving = false, changed = false; string? moveGesture = null;
            board.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(board).Properties.IsLeftButtonPressed) return;
                canvas.Focus();selected = p.Id; selectedWire = null; Inspect();workGrid.UpdateLayout(); DrawSelection();
                if (e.ClickCount == 2 && p.Kind == PartKind.Switch) { Toggle(p.Id); e.Handled = true; return; }
                origin = e.GetPosition(canvas); drag = project.Parts.Single(x => x.Id == p.Id); moving = true; moveGesture = Guid.NewGuid().ToString("N"); e.Pointer.Capture(board); e.Handled = true;
            };
            board.PointerMoved += (_, e) =>
            {
                if (!moving) return;
                var point = e.GetPosition(canvas); var i = project.Parts.FindIndex(x => x.Id == p.Id);
                if (!changed && Math.Abs(point.X - origin.X) + Math.Abs(point.Y - origin.Y) < 3) return;
                if (!changed) { _ = Perform(() => RecordHistory("move-start", true, moveGesture)); Checkpoint(); changed = true; }
                project.Parts[i] = drag with { X = PartArtwork.Coordinate(drag.X + (point.X - origin.X) / zoom), Y = PartArtwork.Coordinate(drag.Y + (point.Y - origin.Y) / zoom) };
                _ = Perform(() => RecordHistory("move", gestureId: moveGesture));
                Canvas.SetLeft(group, project.Parts[i].X * zoom); Canvas.SetTop(group, project.Parts[i].Y * zoom);
                foreach (var line in canvas.Children.OfType<Polyline>().ToArray()) canvas.Children.Remove(line);
                DrawWires();
                DrawSelection();
            };
            board.PointerReleased += (_, e) => { if (!moving) return; moving = false; e.Pointer.Capture(null); Draw(); Inspect(); if(changed) _ = Perform(()=>Save("move-end",true,moveGesture)); };
            board.PointerCaptureLost += (_, _) => { if (!moving) return; moving=false; if(changed) _ = Perform(()=>Save("move-capture-lost",true,moveGesture)); };
            if (result?.Parts.TryGetValue(p.Id, out var reading) == true)
            {
                var indication = p.Kind == PartKind.Servo ? reading.Indicator is { } angle ? $" · {angle:F0}°" : " · нет PWM" : p.Kind == PartKind.Motor ? $" · {reading.Indicator:F0}%" : "";
                var text = Ui.Text($"{reading.Voltage:F2} В · {Math.Abs(reading.Current) * 1000:F1} мА{indication}", 10 * zoom);
                Canvas.SetLeft(text, 0); Canvas.SetTop(text, (size.Height + 5) * zoom); group.Children.Add(text); text.IsHitTestVisible = false;
            }
            foreach (var pin in PartLibrary.Pins(p))
            {
                var point = PinPoint(p, pin);
                var hole = p.Kind == PartKind.Breadboard; var dotDiameter = (hole ? 7 : 13) * zoom; var diameter = hole ? dotDiameter : Math.Max(12, dotDiameter);
                var dot = new Ellipse { Width = dotDiameter, Height = dotDiameter, Fill = startPin == new Terminal(p.Id, pin) ? Brushes.Gold : hole ? Brushes.SlateGray : Brushes.DodgerBlue, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
                var button = new Button { MinWidth = 0, MinHeight = 0, Width = diameter, Height = diameter, CornerRadius = new CornerRadius(diameter), Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Content = dot, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(button, pin + (result?.Voltages.TryGetValue(new(p.Id, pin), out var v) == true ? $" · {v:F2} В" : ""));
                Avalonia.Automation.AutomationProperties.SetName(button, PartLibrary.Name(p) + ": " + pin);
                Canvas.SetLeft(button, (point.X - p.X) * zoom - diameter / 2); Canvas.SetTop(button, (point.Y - p.Y) * zoom - diameter / 2); group.Children.Add(button);
                if (!hole)
                {
                    var pico = PartCatalog.For(p).ReferenceName == "Raspberry Pi Pico" && PartLibrary.Pins(p).Length == 40;
                    var left = point.X - p.X < size.Width / 2;
                    var sideways = pico && p.QuarterTurns % 2 == 1;
                    var mega=p.Kind==PartKind.Mega;var megaDigital=mega&&pin.StartsWith('D')&&int.TryParse(pin[1..],out _);
                    var label = Ui.Text(sideways ? pin.Split(':')[0] : megaDigital?pin[1..]:pin, (pico ? 10 : mega?9:8) * zoom); label.IsHitTestVisible = false;
                    if (pico && !sideways) { label.Width = 95 * zoom; label.TextAlignment = left ? TextAlignment.Right : TextAlignment.Left; }
                    Canvas.SetLeft(label, (point.X - p.X + (pico && !sideways ? left ? -105 : 10 : -7)) * zoom);
                    Canvas.SetTop(label, (point.Y - p.Y + (sideways ? point.Y - p.Y < size.Height / 2 ? -20 : 9 : pico ? -7 : p.Kind == PartKind.Uno && pin.StartsWith('D') ? -16 : 9)) * zoom); group.Children.Add(label);
                    if(mega&&PartArtwork.Degrees(p)==0&&megaDigital)
                    {
                        var number=int.Parse(pin[1..],System.Globalization.CultureInfo.InvariantCulture);
                        Canvas.SetLeft(label,(point.X-p.X+(number<22?-6:number%2==0?-27:9))*zoom);
                        Canvas.SetTop(label,(point.Y-p.Y+(number<22?-17:-7))*zoom);
                    }
                }
                button.Click += (_, _) => _ = Perform(() => Connect(new(p.Id, pin)));
            }
        }
        DrawSelection();
    }
    internal static Control BreadboardFace(double scale)
    {
        var face = new Canvas { Width = 290 * scale, Height = 430 * scale, Background = Brushes.White };
        var slot = new Rectangle { Width = 10 * scale, Height = 390 * scale, Fill = Brushes.Gainsboro };
        Canvas.SetLeft(slot, 140 * scale); Canvas.SetTop(slot, 20 * scale); face.Children.Add(slot);
        foreach (var x in new[] { 15, 30, 260, 275 })
        {
            var line = new Rectangle { Width = 1, Height = 365 * scale, Fill = x is 15 or 260 ? Brushes.IndianRed : Brushes.SteelBlue };
            Canvas.SetLeft(line, (x - 5) * scale); Canvas.SetTop(line, 30 * scale); face.Children.Add(line);
        }
        for (var row = 1; row <= 30; row++)
            if (row == 1 || row % 5 == 0)
            {
                var label = Ui.Text(row.ToString(CultureInfo.InvariantCulture), 7 * scale);
                Canvas.SetLeft(label, 43 * scale); Canvas.SetTop(label, (36 + (row - 1) * 12) * scale); face.Children.Add(label);
            }
        for (var col = 0; col < 10; col++)
        {
            var label = Ui.Text(((char)('a' + col)).ToString(), 7 * scale);
            Canvas.SetLeft(label, (col < 5 ? 62 + col * 15 : 162 + (col - 5) * 15) * scale); Canvas.SetTop(label, 20 * scale); face.Children.Add(label);
        }
        return new Border { Child = face, CornerRadius = new CornerRadius(8), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1) };
    }
    void DrawWires()
    {
        foreach (var w in project.Wires)
        {
            var a = PinPoint(project.Parts.Single(p => p.Id == w.From.Part), w.From.Pin); var b = PinPoint(project.Parts.Single(p => p.Id == w.To.Part), w.To.Pin);
            var line = new Polyline { Points = new Points([new(a.X * zoom, a.Y * zoom), new((a.X + b.X) / 2 * zoom, a.Y * zoom), new((a.X + b.X) / 2 * zoom, b.Y * zoom), new(b.X * zoom, b.Y * zoom)]), Stroke = Brush.Parse(w.Color), StrokeThickness = (w.Id == selectedWire ? 5 : 3) * zoom };
            line.PointerPressed += (_, e) => { selected = null; selectedWire = w.Id; Inspect(); DrawSelection(); e.Handled = true; };
            canvas.Children.Insert(0, line);
        }
    }
    void Connect(Terminal pin)
    {
        if (running) Stop();
        if (startPin is null) startPin = pin;
        else
        {
            if (startPin != pin && !project.Wires.Any(w => w.From == startPin && w.To == pin || w.From == pin && w.To == startPin))
            { if (project.Wires.Count >= 200) throw new InvalidDataException("На схеме допускается до 200 проводов."); Checkpoint(); var color = new[] { "#26966C", "#D94444", "#30363D", "#326CD7" }[Math.Clamp(wireColor.SelectedIndex,0,3)]; project.Wires.Add(new(Guid.NewGuid().ToString("N"), startPin, pin, color)); Save(); }
            startPin = null;
        }
        Refresh();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.Key==Key.Escape && manipulation is not null) { EndManipulation(true); e.Handled=true; return; }
        if (e.Key == Key.Escape) { startPin = null; Draw(); e.Handled = true; }
        base.OnKeyDown(e);
    }
    async Task Compile(bool run)
    {
        await PerformAsync(async () =>
        {
            Stop(); Save(); RecordHistory(run?"run-request":"compile-request",true); compilation = new(); var generation = runId; running = true;
            var snapshot = project.Serialize(); var failureKind = run ? "run-error" : "compile-error";
            var controller=project.Parts.SingleOrDefault(p=>AvrBoards.For(p) is not null);var board=controller is null?null:AvrBoards.For(controller);
            status.Text = "Компиляция "+(board?.Name??"платы")+"…";
            try
            {
                var hasUno = board is not null;
                if (run && project.Parts.FirstOrDefault(p => !PartCatalog.CanSimulate(p)) is { } unsupported)
                    throw new InvalidDataException($"«{PartLibrary.Name(unsupported)}» добавлена для схемы. Её симуляция пока не реализована. Уберите такие детали или работайте в режиме схемы.");
                if (!hasUno && !run) throw new InvalidDataException("Для проверки кода добавьте Arduino Uno или Mega 2560 на схему.");
                if(run&&controller?.Kind==PartKind.Mega&&project.Wires.Any(w=>w.From==new Terminal(controller.Id,"VIN")||w.To==new Terminal(controller.Id,"VIN")))
                    throw new InvalidDataException("Вход VIN пока доступен для схемы. Для симуляции Mega используется виртуальное питание USB; модель внешнего регулятора ещё не подключена.");
                if (hasUno)
                {
                    failureKind = "compile-error";
                    var hex = await FirmwareRunner.Compile(project.Code, compilation.Token,board!.Id);
                    if (generation != runId || disposed) return;
                    Report("compiled",snapshot);
                    failureKind = "run-error";
                    if (run) {await firmware.Start(hex,board!.Id);serialReady=true;ClearSerialDisplay();UpdateSerialControls();}
                }
                if (generation != runId || disposed) return;
                if (run) { timer.Start(); Report("started",snapshot); status.Text = hasUno ? $"Симуляция запущена. Модель постоянного тока; {board!.Name} питается от виртуального USB 5 В." : "Симуляция постоянного тока запущена."; }
                else { running = false; status.Text = "Код скомпилирован без ошибок."; }
            }
            catch (Exception e) { if (generation != runId || disposed) return; Report(failureKind,snapshot,e.Message); Stop(); throw; }
        });
    }
    async Task Tick()
    {
        if (!running || ticking || disposed) return;
        ticking = true;UpdateSerialControls();var generation = runId;
        try
        {
            var uno = project.Parts.FirstOrDefault(p => AvrBoards.For(p) is not null);
            if (uno is not null)
            {
                result = CircuitSolver.Solve(project, outputs);
                var board=AvrBoards.For(uno)!;
                var inputs = Enumerable.Range(0, board.DigitalCount).Select(i => result.Voltages.GetValueOrDefault(new(uno.Id, "D" + i)) > 2.5).ToArray();
                var analog = Enumerable.Range(0, board.AnalogCount).Select(i => result.Voltages.GetValueOrDefault(new(uno.Id, "A" + i))).ToArray();
                var serial=pendingSerial;pendingSerial=null;
                var frame = await firmware.Step(inputs, analog,serial?.Text,serial?.Port??0,CircuitI2c.Devices(project,result));
                if (generation != runId || disposed) return;
                if(serial is not null)RecordHistory("serial-input",true,serialIo:serial);
                outputs = frame.Pins;
                ReceiveSerial(frame);
                if(frame.I2c is {Length:>0} transfers)RecordHistory("i2c-transfer",true,i2cIo:transfers);
                displays=(frame.Displays??[]).ToDictionary(d=>d.Id);
            }
            result = CircuitSolver.Solve(project, outputs); Draw();
            if (result.Warnings.Length > 0) status.Text = string.Join(" ", result.Warnings);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        { if (generation == runId && !disposed) { Report("run-error",project.Serialize(),e.Message); Stop(); status.Text = e.Message; owner.Error(e.Message); } }
        finally { ticking = false;UpdateSerialControls(); }
    }
    public void Suspend() { Stop(); Save(); }
    void Stop()
    {
        if (running) { status.Text = "Симуляция остановлена."; _ = Perform(()=>RecordHistory("simulation-stop",true)); }
        runId++; running = false; timer.Stop(); compilation?.Cancel(); compilation?.Dispose(); compilation = null; firmware.Dispose(); outputs = null;
        serialReady=false;pendingSerial=null;displays.Clear();UpdateSerialControls();
    }
    string Svg()
    {
        string E(string text) => System.Security.SecurityElement.Escape(text) ?? "";
        var scene = PartArtwork.Bounds(project.Parts);
        var b = new StringBuilder(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{scene.X} {scene.Y} {scene.Width} {scene.Height}\"><rect x=\"{scene.X}\" y=\"{scene.Y}\" width=\"{scene.Width}\" height=\"{scene.Height}\" fill=\"white\"/>"));
        b.Append(PartArtwork.SvgDefinitions());
        foreach (var w in project.Wires)
        {
            var a = PinPoint(project.Parts.Single(p => p.Id == w.From.Part), w.From.Pin); var z = PinPoint(project.Parts.Single(p => p.Id == w.To.Part), w.To.Pin);
            b.Append(FormattableString.Invariant($"<path d=\"M{a.X},{a.Y}H{(a.X + z.X) / 2}V{z.Y}H{z.X}\" fill=\"none\" stroke=\"{w.Color}\" stroke-width=\"3\"/>"));
        }
        foreach (var p in project.Parts)
        {
            b.Append(PartArtwork.SvgPart(p));
            b.Append(FormattableString.Invariant($"<text x=\"{p.X}\" y=\"{p.Y - 12}\" font-family=\"sans-serif\" font-size=\"13\">{E(PartLibrary.Name(p))}</text>"));
            foreach (var pin in PartLibrary.Pins(p))
            {
                var a = PinPoint(p, pin); b.Append(FormattableString.Invariant($"<circle cx=\"{a.X}\" cy=\"{a.Y}\" r=\"3\" fill=\"#fff\" stroke=\"#334455\"/>"));
                if (p.Kind != PartKind.Breadboard) b.Append(FormattableString.Invariant($"<text x=\"{a.X + 4}\" y=\"{a.Y - 6}\" font-size=\"8\">{E(pin)}</text>"));
            }
        }
        return b.Append("</svg>").ToString();
    }
    void ApplyEditorTheme()=>editor.SyntaxHighlighting=CodeAppearance.CppForTheme();
    public void Dispose()
    {
        if (disposed) return; Stop(); autosave.Stop();
        try { Save(); } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { owner.Error("Не удалось сохранить схему: " + e.Message); }
        ReleaseHistory(); disposed = true; history?.Dispose(); ThemeManager.Changed-=ApplyEditorTheme;sync?.Dispose(); resultsView?.Dispose();
    }
}
