using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;

namespace Classroom.Desktop;
public sealed class NotificationState : ObservableObject
{
    string status = "";
    public string Status { get => status; set => SetProperty(ref status, value); }
}

public sealed class MainWindow : Window
{
    readonly bool teacher;
    readonly string? studentDataRoot;
    readonly HostManager? host;
    ClassroomApi? api;
    LoginDto? login;
    IDisposable? screen;
    StudentScreen? student;
    ElectronicsScreen? electronics;
    ElectronicsScreen? practiceElectronics;
    readonly Dictionary<string, ElectronicsScreen> assignedElectronics = new();
    StudentHomeControl? studentHome;
    object? pageReturnContent;
    Action? pageCleanup;
    bool pageOpen;
    StudentReviewPage? teacherReview;
    readonly ContentControl body = new();
    readonly WrapPanel actions = new()
    {
        Orientation = Orientation.Horizontal,
        MaxWidth = 680,
        ItemSpacing = 10,
        LineSpacing = 8
    };
    readonly NotificationState state = new();
    readonly NotificationToast notification = new();
    bool quitting;
    string? protectionWarning;
    bool reconnecting;
    internal string? LastTaskId { get; set; }
    internal bool IsLessonVisible => !pageOpen && body.Content is StudentScreen;
    readonly Dictionary<string, SqlLocalStore> sqlStores = new();
    readonly DispatcherTimer sqlDeliveryPoll = new() { Interval = TimeSpan.FromSeconds(2) };
    bool deliveringSql;
    readonly DispatcherTimer electronicsDeliveryPoll = new() { Interval = TimeSpan.FromSeconds(2) };
    ElectronicsBackgroundDelivery? electronicsDelivery;
    readonly HashSet<string> electronicsDeliveryErrors = new();
    string StudentDataDirectory => studentDataRoot ?? Environment.GetEnvironmentVariable("CLASSROOM_STUDENT_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQL-Classroom", "student");
    void StartElectronicsDelivery()
    {
        if (teacher || api?.ProfileId is null || login?.Role != "student") return;
        electronicsDelivery ??= new ElectronicsBackgroundDelivery(StudentDataDirectory, api, path =>
            assignedElectronics.Values.Append(practiceElectronics).Append(electronics).Any(editor => editor is not null && string.Equals(editor.SavePath, path, StringComparison.OrdinalIgnoreCase)));
        electronicsDeliveryPoll.Start();
    }
    async Task StopElectronicsDelivery()
    {
        electronicsDeliveryPoll.Stop(); var previous = electronicsDelivery; electronicsDelivery = null;
        if (previous is not null) await previous.DisposeAsync(); electronicsDeliveryErrors.Clear();
    }
    internal SqlLocalStore SqlStore(ClassroomApi client)
    {
        var id = client.ProfileId ?? throw new InvalidDataException("Сначала войдите в ученический профиль.");
        var key = client.Connection.Fingerprint + ":" + id;
        if (!sqlStores.TryGetValue(key, out var store))
        {
            var root = StudentDataDirectory;
            store = new SqlLocalStore(root, client.Connection.Fingerprint, id); sqlStores.Add(key, store);
        }
        return store;
    }

    bool discardDraft;
    DispatcherTimer? admissionPoll;
    readonly DispatcherTimer lessonPoll = new() { Interval = TimeSpan.FromSeconds(1) };
    bool pollingLesson;
    Button? sqlSubjectButton, electronicsSubjectButton;
    readonly TextBlock lessonMessage = Ui.Text("", 20);
    readonly Border lessonBlock = new() { IsVisible = false, Padding = new Thickness(32), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    TrayIcon? tray;
    public MainWindow(bool teacher) : this(teacher, true)
    {
    }

    internal MainWindow(bool teacher, bool initializeOnOpen, string? studentDataRoot = null)
    {
        this.teacher = teacher;
        this.studentDataRoot = studentDataRoot;
        host = teacher ? new() : null;
        Title = teacher ? "SQL · Преподаватель" : "SQL · Ученик";
        if (Environment.GetEnvironmentVariable("CLASSROOM_DEMO") == "1")
            Title += " · Демонстрация";
        Width = 1320;
        Height = 900;
        MinWidth = 850;
        MinHeight = 600;
        var header = new Grid
        {
            ColumnDefinitions = new("240,*"),
            Margin = new Thickness(20, 12,24,12),
            MinHeight=48
        };
        var mark=new Border{Width=32,Height=32,CornerRadius=new CornerRadius(8),Child=new TextBlock{Text=">_",FontFamily=new FontFamily("Consolas"),FontSize=15,FontWeight=FontWeight.Bold,Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
        Ui.BindBrush(mark,Border.BackgroundProperty,"Accent");
        var brandName=Ui.Text("",18);brandName.Inlines!.Add(new Avalonia.Controls.Documents.Run("sql"));var brandDot=new Avalonia.Controls.Documents.Run(".");Ui.BindBrush(brandDot,Avalonia.Controls.Documents.TextElement.ForegroundProperty,"Accent");brandName.Inlines.Add(brandDot);brandName.Inlines.Add(new Avalonia.Controls.Documents.Run("пространство"));
        var brand=Ui.Row(mark,brandName);brand.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(brand);
        Grid.SetColumn(actions, 1);
        actions.HorizontalAlignment=HorizontalAlignment.Right;
        header.Children.Add(actions);
        var message = Ui.Text("");
        message.Bind(TextBlock.ForegroundProperty,new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BackgroundInkBrush"));
        message.Bind(TextBlock.TextProperty, new Binding(nameof(NotificationState.Status)) { Source = state });
        var root = new Grid
        {
            RowDefinitions = new("Auto,*,Auto")
        };
        var headerBorder=new Border{Child=header,BorderThickness=new Thickness(0,0,0,1)};Ui.BindBrush(headerBorder,Border.BackgroundProperty,"Surface");Ui.BindBrush(headerBorder,Border.BorderBrushProperty,"Line");root.Children.Add(headerBorder);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        lessonBlock.Child = lessonMessage;
        Ui.BindBrush(lessonBlock, Border.BackgroundProperty, "Surface");
        Grid.SetRow(lessonBlock, 1); root.Children.Add(lessonBlock);
        var messageBorder = new Border
        {
            Child = message,
            Padding = new Thickness(18, 8)
        };
        Grid.SetRow(messageBorder, 2);
        root.Children.Add(messageBorder);
        Grid.SetRow(notification,1);
        root.Children.Add(notification);
        Content = root;
        Closed += (_,_) => { notification.Dismiss(); admissionPoll?.Stop(); lessonPoll.Stop(); };
        lessonPoll.Tick += async (_, _) =>
        {
            if (pollingLesson || teacher || api is null || login is null) return;
            pollingLesson = true;
            try { await Safe(RefreshLessonPermission); }
            finally { pollingLesson = false; }
        };
        if (initializeOnOpen)
            Opened += async (_, _) => await Safe(Initialize);
        Closing += OnClosing;
        Closed += async (_, _) => { sqlDeliveryPoll.Stop(); await StopElectronicsDelivery(); foreach (var store in sqlStores.Values) store.Dispose(); };
        electronicsDeliveryPoll.Tick += async (_, _) =>
        {
            var current = electronicsDelivery; if (current is null) return;
            var result = await current.DrainOnce(); if (!ReferenceEquals(current, electronicsDelivery)) return;
            if (result.Error is { } message && electronicsDeliveryErrors.Add(message)) Error(message);
        };
        sqlDeliveryPoll.Tick += async (_, _) =>
        {
            if (deliveringSql || api?.ProfileId is null || login?.Role != "student") return;
            deliveringSql = true;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await SqlStore(api).Drain(api, limit.Token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ApiException) { /* Keep the durable queue; lesson polling handles connectivity/login. */ }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { Error(e.Message); }
            finally { deliveringSql = false; }
        };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel);
        AddHandler(TextBox.CopyingToClipboardEvent, OnClipboard);
        AddHandler(TextBox.CuttingToClipboardEvent, OnClipboard);
        AddHandler(TextBox.PastingFromClipboardEvent, OnClipboard);
        AddHandler(DragDrop.DragOverEvent, OnDrop, RoutingStrategies.Tunnel);
    }

    async Task Initialize()
    {
        if (teacher)
        {
            state.Status = "Запускается сервер преподавателя…";
            api = await host!.Start();
            ConfigureTray();
            await ShowLogin();
            state.Status = "";
        }
        else
            ShowConnect();
    }

    public async Task Safe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SqlLocalRecoveryException e)
        {
            sqlDeliveryPoll.Stop(); lessonPoll.Stop();
            actions.Children.Clear();
            body.Content = new SqlRecoveryView(this, e.Candidate, async () => { login = null; await ShowLogin(); });
        }
        catch (ApiException e)when (e.Status == 401 && (screen is not null || login is not null))
        {
            await Reauthenticate();
        }
        catch (Exception e)when (e is ApiException or HttpRequestException or TaskCanceledException or InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        {
            Error(e is HttpRequestException or TaskCanceledException ? "Нет связи с преподавателем. Повторите действие после подключения." : e.Message);
        }
    }

    public async Task Reauthenticate()
    {
        if (reconnecting)
            return;
        reconnecting = true;
        try
        {
            try { student?.SaveLocalDraft(); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            { Error("Повторный вход отложен: последние изменения не сохранены на ПК. " + e.Message); return; }
            login = null;
            await ShowLogin();
            Error("Сессия завершилась. Войдите снова; локально сохранённые работы останутся на ПК.");
        }
        catch (Exception e)when (e is HttpRequestException or TaskCanceledException)
        {
            Error("Нет связи с преподавателем. Повторите вход после восстановления связи.");
        }
        finally
        {
            reconnecting = false;
        }
    }

    public void Message(string text) => Notify(text,false);
    public void Error(string text) => Notify(text,true);
    public void ShowPage(string title, Control content, Action? cleanup = null)
    {
        ClosePage();
        pageReturnContent = body.Content;
        pageCleanup = cleanup;
        pageOpen = true;
        var page = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12, Margin = new Thickness(18) };
        page.Children.Add(Ui.Card(Ui.Row(Ui.Button("Назад", () => { ClosePage(); return Task.CompletedTask; }), Ui.Text(title,24))));
        Grid.SetRow(content,1); page.Children.Add(content);
        body.Content = page;
    }
    internal void ClosePage(bool restore = true)
    {
        if (!pageOpen) return;
        var previous = pageReturnContent; var cleanup = pageCleanup;
        pageOpen = false; pageReturnContent = null; pageCleanup = null;
        cleanup?.Invoke();
        if (restore) body.Content = previous;
        RefreshSurface();
        Protect(student?.Restricted == true);
    }
    internal async Task ShowAppearance()
    {
        if (api is null || login is null) return;
        ClosePage();
        electronics?.Suspend();
        var token = login.Token;
        var page = new AppearancePage(this,api,login.Theme,changed =>
        {
            if (login?.Token != token) return;
            if (changed is not null) login = login with { Theme = changed };
            ClosePage();
        });
        ShowPage("Оформление",page,page.Dispose);
        await page.Load();
    }
    void Notify(string text,bool error)
    {
        if(!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(()=>Notify(text,error)); return; }
        state.Status="";
        notification.ShowMessage(text,error);
    }
    internal void Preview(Control view) => body.Content = view;
    void ShowConnect()
    {
        admissionPoll?.Stop(); lessonPoll.Stop(); sqlDeliveryPoll.Stop();
        var directory = StudentDataDirectory;
        var path = Path.Combine(directory, "connection.json");
        state.Status = "";
        var code = Ui.Input();
        code.Watermark = "XXXX-XXXX-XXXX";
        code.MaxLength = 20;
        code.FontSize = 24;
        var connect = Ui.Primary(Ui.Button("Подключиться", () => Safe(async () =>
        {
            state.Status = "Ищем преподавателя…";
            var connection = await TeacherDiscovery.Find(code.Text ?? "");
            var next = new ClassroomApi(connection);
            try { await next.Get<StatusDto>("status"); }
            catch { next.Dispose(); throw; }
            api?.Dispose();
            api = next;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Wire.Write(next.Connection));
            await ShowLogin();
            state.Status = "";
        })));
        body.Content = new Border
        {
            Child = Ui.Card(Ui.Stack(Ui.Text("Подключение к занятию", 24), Ui.Text("Введите код, который показывает преподаватель."), Ui.Field("Код преподавателя", code), connect)),
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    async Task ShowLogin()
    {
        await StopElectronicsDelivery();
        admissionPoll?.Stop(); lessonPoll.Stop();
        ClosePage(false);
        body.IsEnabled = true; lessonBlock.IsVisible = false;
        screen?.Dispose();
        if (!ReferenceEquals(screen, student)) student?.Dispose();
        foreach (var editor in assignedElectronics.Values.Append(practiceElectronics).Append(electronics).Where(e => e is not null).Distinct()) editor!.Dispose();
        assignedElectronics.Clear(); practiceElectronics = null;
        electronics = null;
        screen = null;
        student = null;
        studentHome = null;
        Protect(false);
        actions.Children.Clear();
        var status = await api!.Get<StatusDto>("status");
        var setup = teacher && status.NeedsSetup;
        var group = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        var names = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        group.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GroupDto>((item, _) => Ui.Text(item?.Name ?? ""));
        names.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<StudentChoice>((item, _) => Ui.Text(item?.Name ?? ""));
        var admissionMessage = Ui.Text("");
        var registerName = Ui.Input(); var registerPassword = Ui.Input(); registerPassword.PasswordChar = '●';
        AdmissionDto? admission = null;
        var registrationId = Guid.NewGuid().ToString("N");
        var registrationFields = Ui.Stack(Ui.Text("Регистрация в группе", 18), Ui.Field("ФИО", registerName), Ui.Field("Новый пароль", registerPassword), Ui.Button("Зарегистрироваться", () => Safe(async () =>
        {
            var created = await api!.Post<StudentChoice>("student-registration", new StudentRegistrationRequest(registrationId, registerName.Text ?? "", registerPassword.Text ?? ""));
            registrationId = Guid.NewGuid().ToString("N"); registerPassword.Text = "";
            group.SelectedItem = group.Items.OfType<GroupDto>().FirstOrDefault(g => g.Id == admission?.RegistrationGroup?.Id);
            await UpdateNames(created.Id);
            Message("Учётная запись создана. Выберите своё ФИО и войдите с вашим паролем.");
        }))) ;
        registrationFields.IsVisible = false;
        bool directoryBusy = false;
        async Task UpdateNames(string? select = null)
        {
            if (group.SelectedItem is not GroupDto selected || admission?.AdmissionOpen != true) { names.ItemsSource = Array.Empty<StudentChoice>(); return; }
            var before = select ?? (names.SelectedItem as StudentChoice)?.Id;
            var items = await api!.Get<StudentChoice[]>("admission/students?groupId=" + Uri.EscapeDataString(selected.Id));
            if ((group.SelectedItem as GroupDto)?.Id != selected.Id) return;
            names.ItemsSource = items;
            names.SelectedItem = items.FirstOrDefault(n => n.Id == before);
        }
        async Task UpdateAdmission()
        {
            if (directoryBusy) return;
            directoryBusy = true;
            try
            {
                admission = await api!.Get<AdmissionDto>("admission"); admissionMessage.Text = admission.Message;
                var selected = (group.SelectedItem as GroupDto)?.Id;
                group.ItemsSource = admission.Groups;
                group.SelectedItem = admission.Groups.FirstOrDefault(g => g.Id == selected) ?? admission.Groups.FirstOrDefault();
                group.IsEnabled = names.IsEnabled = admission.AdmissionOpen;
                registrationFields.IsVisible = admission.RegistrationOpen;
                if (registrationFields.Children[0] is TextBlock label) label.Text = "Регистрация · " + admission.RegistrationGroup?.Name;
                await UpdateNames();
            }
            finally { directoryBusy = false; }
        }
        group.SelectionChanged += async (_, _) => { if (!directoryBusy) await Safe(() => UpdateNames()); };
        var password = Ui.Input();
        password.PasswordChar = '●';
        var fields = Ui.Stack(Ui.Text(setup ? "Настройка преподавателя" : teacher ? "Вход преподавателя" : "Вход ученика", 24));
        if (!teacher)
        {
            fields.Children.Add(admissionMessage);
            fields.Children.Add(Ui.Field("Группа", group));
            fields.Children.Add(Ui.Field("ФИО", names));
        }
        fields.Children.Add(Ui.Field("Пароль", password));
        fields.Children.Add(Ui.Primary(Ui.Button(setup ? "Создать пароль" : "Войти", () => Safe(async () =>
        {
            if (setup)
                await api.Post<JsonElement>("setup", new PasswordRequest(password.Text ?? ""));
            if (teacher) login = await api.Login(new("teacher", "", password.Text ?? ""));
            else
            {
                if (group.SelectedItem is not GroupDto selectedGroup || names.SelectedItem is not StudentChoice selectedStudent)
                { Error("Выберите группу и своё ФИО."); return; }
                login = await api.LoginStudent(new(selectedGroup.Id, selectedStudent.Id, password.Text ?? ""));
            }
            admissionPoll?.Stop();
            password.Text = "";
            notification.Dismiss();
            ThemeManager.Apply(login.Theme);
            actions.Children.Add(Ui.Button("Оформление", () => Safe(ShowAppearance)));
            actions.Children.Add(Ui.Button("Выйти", () => Safe(async () =>
            {
                if (student is not null)
                    await student.SaveDraft();
                if (electronics is not null) await electronics.Synchronize();
                await StopElectronicsDelivery();
                await api.Logout();
                login = null;
                await ShowLogin();
            })));
            if (teacher)
            {
                actions.Children.Add(Ui.Button("Завершить работу", () => Safe(Quit)));
                var view = new TeacherScreen(this, api, host!);
                var enteringToken = login.Token;
                screen = view;
                body.Content = view;
                await view.Load();
                if (login?.Token != enteringToken) return;
                var selectedStand = login.Destination?.StandId == "electronics" ? 1 : 0;
                var stands = new ComboBox { ItemsSource = new[] { "SQL", "Электроника" }, SelectedIndex = selectedStand, MinWidth = 160, MinHeight = 36 };
                Avalonia.Automation.AutomationProperties.SetName(stands, "Стенд");
                actions.Children.Insert(0, stands);
                async Task ShowStand()
                {
                    ClosePage(false);
                    if (selectedStand == 0)
                    {
                        electronics?.Suspend(); body.Content = view; await view.ShowWorkspace();
                    }
                    else
                    {
                        var path = Path.Combine(host!.DataDirectory, "electronics", "teacher-project.json");
                        electronics ??= new ElectronicsScreen(this, path,api,teacher:true);
                        body.Content = electronics;
                    }
                }
                var selecting = false;
                stands.SelectionChanged += async (_, _) =>
                {
                    if (selecting) return;
                    stands.IsEnabled = false;
                    try { await Safe(async () =>
                    {
                        var next = stands.SelectedIndex;
                        await api.Put<LessonDestination>("teacher/lesson", new LessonDestination(next == 1 ? "electronics" : "sql"));
                        selectedStand = next;
                        await ShowStand();
                    }); }
                    finally { selecting = true; stands.SelectedIndex = selectedStand; selecting = false; stands.IsEnabled = true; }
                };
                actions.Children.Add(Ui.Button("Код подключения", () => Safe(async () =>
                {
                    if (!stands.IsEnabled) return;
                    ClosePage(false);
                    electronics?.Suspend();
                    body.Content = Ui.Card(Ui.Stack(Ui.Button("Назад к стенду", ShowStand),
                        new ConnectionCodeView(host!.ConnectionCode, host.Settings.Address == "127.0.0.1", selectedStand == 1 ? "Электроника" : "SQL")));
                    await Task.CompletedTask;
                })));
                await ShowStand();
            }
            else
            {
                sqlSubjectButton = Ui.Button("SQL", () => Safe(() => SwitchStudentSubject("sql")));
                electronicsSubjectButton = Ui.Button("Электроника", () => Safe(() => SwitchStudentSubject("electronics")));
                actions.Children.Insert(0, sqlSubjectButton); actions.Children.Insert(1, electronicsSubjectButton);
                actions.Children.Insert(0, Ui.Button("Моё занятие", () => Safe(ShowStudentHome)));
                actions.Children.Add(Ui.Button("Проверка преподавателя", () => Safe(ShowTeacherReview)));
                SqlStore(api);
                sqlDeliveryPoll.Start();
                StartElectronicsDelivery();
                await ShowStudentHome();
                await RefreshLessonPermission(); lessonPoll.Start();
            }

            state.Status = "";
        }))));
        if (!teacher)
        {
            fields.Children.Add(registrationFields);
            fields.Children.Add(Ui.Button("Другой преподаватель", () =>
            {
                ShowConnect();
                return Task.CompletedTask;
            }));
            await UpdateAdmission();
            admissionPoll = new() { Interval = TimeSpan.FromSeconds(2) };
            admissionPoll.Tick += async (_, _) =>
            {
                try { await UpdateAdmission(); }
                catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException) { admissionMessage.Text = "Нет связи с преподавателем."; }
            };
            admissionPoll.Start();
        }
        if (teacher)
            fields.Children.Add(Ui.Button("Завершить работу", () => Safe(Quit)));
        body.Content = new Border
        {
            Child = Ui.Card(fields),
            MaxWidth = 550,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    internal async Task EnterLesson(LessonDestination? destination, string? taskId = null)
    {
        if (destination?.StandId == "electronics")
        {
            await StopElectronicsDelivery();
            try
            {
                Protect(false);
                var directory = StudentDataDirectory;
                var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(api!.Connection.Fingerprint + ":" + login!.Id)));
                if (taskId is not null)
                {
                    var work = await api.Get<ElectronicsAssignmentDto>("electronics/tasks/" + Uri.EscapeDataString(taskId));
                    if (!assignedElectronics.TryGetValue(work.Id, out var editor))
                    {
                        editor = new ElectronicsScreen(this, Path.Combine(directory, "electronics", identity + "-" + work.Id + ".json"), api, assignment: work);
                        assignedElectronics.Add(work.Id, editor);
                    }
                    else editor.UpdateAssignment(work);
                    electronics = editor;
                }
                else
                {
                    practiceElectronics ??= new ElectronicsScreen(this, Path.Combine(directory, "electronics", identity + ".json"), api);
                    electronics = practiceElectronics;
                }
                screen = electronics;
                body.Content = electronics;
                return;
            }
            finally { StartElectronicsDelivery(); }
        }
        if (destination?.StandId is not (null or "sql")) throw new InvalidDataException("Этот стенд не поддерживается. Обновите приложение ученика.");
        var first = student is null;
        student ??= new(this, api!);
        screen = student;
        body.Content = student;
        if (first) await student!.Load(taskId);
        else await student.OpenTask(taskId ?? LastTaskId);
        Protect(student.Restricted);
    }

    async Task SwitchStudentSubject(string subject, string? taskId = null)
    {
        if (api is null) return;
        var state = await api.Get<StudentLessonDto>("student/lesson");
        if (!state.CanWork || (subject == "sql" ? state.Lesson?.AllowSql != true : state.Lesson?.AllowElectronics != true))
        { Error(state.Message.Length > 0 ? state.Message : "Преподаватель не разрешил это направление."); return; }
        if (student is not null) await student.SaveDraft();
        electronics?.Suspend(); ClosePage(false);
        await EnterLesson(new LessonDestination(subject), taskId);
        await RefreshLessonPermission();
    }

    internal async Task ShowStudentHome()
    {
        if (api is null) return;
        if (student is not null) await student.SaveDraft();
        electronics?.Suspend(); ClosePage(false); Protect(false);
        studentHome ??= new(this, api, (subject, id) => SwitchStudentSubject(subject, id));
        body.Content = studentHome;
        await studentHome.Refresh();
    }

    async Task ShowTeacherReview()
    {
        if (api is null || teacher || login is null) return;
        if (student is not null) await student.SaveDraft();
        electronics?.Suspend();
        ClosePage();
        var view = new StudentReviewPage(this, api);
        ShowPage("Проверка преподавателя", view, () =>
        {
            teacherReview = null; view.Dispose();
            body.IsEnabled = false; lessonBlock.IsVisible = true; lessonMessage.Text = "Проверяем доступ к занятию…";
        });
        teacherReview = view;
        body.IsEnabled = true; lessonBlock.IsVisible = false; Protect(false);
    }

    async Task RefreshLessonPermission()
    {
        if (api is null || login is null || teacher) return;
        if (teacherReview is not null) { body.IsEnabled = true; lessonBlock.IsVisible = false; return; }
        var lesson = await api.Get<StudentLessonDto>("student/lesson");
        if (sqlSubjectButton is not null) sqlSubjectButton.IsVisible = lesson.Lesson?.AllowSql == true;
        if (electronicsSubjectButton is not null) electronicsSubjectButton.IsVisible = lesson.Lesson?.AllowElectronics == true;
        var currentAllowed = screen is ElectronicsScreen ? lesson.Lesson?.AllowElectronics == true : lesson.Lesson?.AllowSql == true;
        if (body.Content is ElectronicsScreen circuit && circuit.AssignmentId is { } workId)
        {
            circuit.UpdateAssignmentStatus(await api.Get<ElectronicsAssignmentStatus>("electronics/assignments/" + workId + "/status"));
            currentAllowed &= circuit.AssignmentAvailable;
        }
        var canWork = lesson.CanWork && currentAllowed;
        if (body.Content is StudentHomeControl home)
        {
            body.IsEnabled = true; lessonBlock.IsVisible = false; await home.Refresh(); return;
        }
        var wasEnabled = body.IsEnabled;
        body.IsEnabled = canWork;
        lessonBlock.IsVisible = !canWork;
        lessonMessage.Text = lesson.Message.Length > 0 ? lesson.Message : "Преподаватель отключил это направление. Выберите другое вверху.";
        if (!canWork && wasEnabled)
        {
            electronics?.Suspend();
            if (student is not null) await student.SaveDraft();
        }
    }

    void RefreshSurface()
    {
        Background = Application.Current!.Resources["BackgroundBrush"] as IBrush;
        if (student is not null)
            student.ApplyTheme();
        if (screen is TeacherScreen teacherView)
            teacherView.ApplyTheme();
    }

    public void Protect(bool restricted)
    {
        restricted = restricted && IsLessonVisible;
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } handle)
        {
            if (!NativeWindowProtection.TrySet(handle, restricted, out var error) && restricted)
            {
                protectionWarning = $"Windows не включила защиту захвата окна (код {error}). Ограничение копирования действует.";
                state.Status = protectionWarning;
            }
            else if (state.Status == protectionWarning)
            {
                state.Status = "";
                protectionWarning = null;
            }
        }
    }

    async void OnKey(object? sender, KeyEventArgs e)
    {
        if (!IsLessonVisible || !body.IsEnabled) return;
        if (student is not null && e.Key == Key.Enter && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            e.Handled = true;
            await Safe(student.CheckShortcut);
            return;
        }

        if (student?.Restricted != true)
            return;
        if (e.Key is Key.Snapshot or Key.Print || (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            e.Handled = true;
            student.CaptureAttempt();
            return;
        }
        if (((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key is Key.C or Key.X or Key.V) || (e.Key == Key.Insert && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0) || (e.Key == Key.Delete && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            e.Handled = true;
            var action = e.Key == Key.C || e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Control) ? "copy" : e.Key is Key.X or Key.Delete ? "cut" : "paste";
            await Safe(() => student.Violation(action));
        }
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!IsLessonVisible || !body.IsEnabled) return;
        if (student?.Restricted == true)
        {
            e.Handled = true;
            e.DragEffects = DragDropEffects.None;
            if (e.RoutedEvent == DragDrop.DropEvent)
                await Safe(() => student.Violation("drop"));
        }
    }

    async void OnClipboard(object? sender, RoutedEventArgs e)
    {
        if (!IsLessonVisible || !body.IsEnabled) return;
        if (student?.Restricted != true)
            return;
        e.Handled = true;
        var action = e.RoutedEvent == TextBox.CopyingToClipboardEvent ? "copy" : e.RoutedEvent == TextBox.CuttingToClipboardEvent ? "cut" : "paste";
        await Safe(() => student.Violation(action));
    }

    void ConfigureTray()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var menu = new NativeMenu();
        var show = new NativeMenuItem("Открыть преподавателя");
        show.Click += (_, _) =>
        {
            Show();
            Activate();
        };
        var stop = new NativeMenuItem("Завершить работу");
        stop.Click += async (_, _) => await Safe(Quit);
        menu.Add(show);
        menu.Add(stop);
        tray = new TrayIcon
        {
            ToolTipText = "SQL · Преподаватель",
            Menu = menu,
            IsVisible = true
        };
        // The executable icon is also used for the notification area.
        using var iconStream = GetType().Assembly.GetManifestResourceStream("Classroom.Desktop.classroom.ico");
        if (iconStream is not null)
            tray.Icon = new WindowIcon(iconStream);
        tray.Clicked += (_, _) =>
        {
            Show();
            Activate();
        };
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
    }

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (quitting)
            return;
        if (teacher && OperatingSystem.IsWindows() && tray is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        e.Cancel = true;
        if (student is not null)
        {
            try
            {
                student.SaveLocalDraft();
            }
            catch (Exception ex)when (ex is HttpRequestException or ApiException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                if (!await Confirm("Последние изменения не удалось сохранить: " + ex.Message + " Закрыть приложение?"))
                    return;
                discardDraft = true;
            }
        }

        await Safe(Quit);
    }

    async Task Quit()
    {
        if (quitting)
            return;
        if (student is not null && !discardDraft)
            await student.SaveDraft();
        if (electronics is not null) await electronics.Synchronize();
        await StopElectronicsDelivery();
        ClosePage(false);
        screen?.Dispose();
        foreach (var editor in assignedElectronics.Values.Append(practiceElectronics).Append(electronics).Where(e => e is not null).Distinct()) editor!.Dispose();
        if (!ReferenceEquals(screen, student)) student?.Dispose();
        if (host is not null)
            await host.Stop(api);
        quitting = true;
        tray?.Dispose();
        api?.Dispose();
        Close();
    }

    public async Task<bool> Confirm(string text)
    {
        var dialog = new Window
        {
            Title = "Подтверждение",
            Width = 480,
            Height = 230,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Stack(Ui.Text(text), Ui.Row(Ui.Button("Отмена", () =>
            {
                dialog.Close(false);
                return Task.CompletedTask;
            }), Ui.Button("Подтвердить", () =>
            {
                dialog.Close(true);
                return Task.CompletedTask;
            })))
        };
        return await dialog.ShowDialog<bool>(this);
    }
}

static class NativeWindowProtection
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    public static bool TrySet(nint handle, bool enabled, out int error)
    {
        var desired = enabled ? 0x11u : 0u;
        if (!SetWindowDisplayAffinity(handle, desired) || !GetWindowDisplayAffinity(handle, out var actual))
        {
            error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }
        error = 0;
        return actual == desired;
    }
}
