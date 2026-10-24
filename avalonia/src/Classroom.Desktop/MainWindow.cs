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
    readonly HostManager? host;
    ClassroomApi? api;
    LoginDto? login;
    IDisposable? screen;
    StudentScreen? student;
    ElectronicsScreen? electronics;
    object? pageReturnContent;
    Action? pageCleanup;
    bool pageOpen;
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
    readonly Dictionary<string, string> unsavedDrafts = new();
    internal string? LastTaskId { get; set; }
    internal bool IsLessonVisible => !pageOpen && body.Content is StudentScreen;

    string DraftKey(string id) => $"{login?.Id}:{id}";
    readonly Dictionary<string, PendingExecution> pendingRequests = new();
    internal PendingExecution? PendingFor(string id) => pendingRequests.GetValueOrDefault(DraftKey(id));
    internal void RememberPending(PendingExecution request) => pendingRequests[DraftKey(request.TaskId)] = request;
    internal void ForgetPending(string id) => pendingRequests.Remove(DraftKey(id));
    internal void RememberDraft(string id, string code) => unsavedDrafts[DraftKey(id)] = code;
    internal string? UnsavedDraft(string id) => unsavedDrafts.GetValueOrDefault(DraftKey(id));
    internal void ForgetDraft(string id) => unsavedDrafts.Remove(DraftKey(id));
    bool discardDraft;
    TrayIcon? tray;
    public MainWindow(bool teacher) : this(teacher, true)
    {
    }

    internal MainWindow(bool teacher, bool initializeOnOpen)
    {
        this.teacher = teacher;
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
        Closed += (_,_) => notification.Dismiss();
        if (initializeOnOpen)
            Opened += async (_, _) => await Safe(Initialize);
        Closing += OnClosing;
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
        catch (ApiException e)when (e.Status == 401 && screen is not null)
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
            login = null;
            await ShowLogin();
            Error("Сессия завершилась. Войдите снова; несохранённый черновик остаётся в этом окне.");
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
        var directory = Environment.GetEnvironmentVariable("CLASSROOM_STUDENT_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQL-Classroom", "student");
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
        ClosePage(false);
        screen?.Dispose();
        electronics?.Dispose();
        electronics = null;
        screen = null;
        student = null;
        Protect(false);
        actions.Children.Clear();
        var status = await api!.Get<StatusDto>("status");
        var setup = teacher && status.NeedsSetup;
        var name = Ui.Input();
        var password = Ui.Input();
        password.PasswordChar = '●';
        var fields = Ui.Stack(Ui.Text(setup ? "Настройка преподавателя" : teacher ? "Вход преподавателя" : "Вход ученика", 24));
        if (!teacher)
            fields.Children.Add(Ui.Field("Логин", name));
        fields.Children.Add(Ui.Field("Пароль", password));
        fields.Children.Add(Ui.Primary(Ui.Button(setup ? "Создать пароль" : "Войти", () => Safe(async () =>
        {
            if (setup)
                await api.Post<JsonElement>("setup", new PasswordRequest(password.Text ?? ""));
            login = await api.Login(new(teacher ? "teacher" : "student", name.Text ?? "", password.Text ?? ""));
            password.Text = "";
            notification.Dismiss();
            ThemeManager.Apply(login.Theme);
            actions.Children.Add(Ui.Button("Оформление", () => Safe(ShowAppearance)));
            actions.Children.Add(Ui.Button("Выйти", () => Safe(async () =>
            {
                if (student is not null)
                    await student.SaveDraft();
                if (electronics is not null) await electronics.Synchronize();
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
                await EnterLesson(login.Destination);
            }

            state.Status = "";
        }))));
        if (!teacher)
            fields.Children.Add(Ui.Button("Другой преподаватель", () =>
            {
                ShowConnect();
                return Task.CompletedTask;
            }));
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

    internal async Task EnterLesson(LessonDestination? destination)
    {
        if (destination?.StandId == "electronics")
        {
            var directory = Environment.GetEnvironmentVariable("CLASSROOM_STUDENT_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQL-Classroom", "student");
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(api!.Connection.Fingerprint + ":" + login!.Id)));
            electronics = new ElectronicsScreen(this, Path.Combine(directory, "electronics", identity + ".json"),api);
            screen = electronics;
            body.Content = electronics;
            return;
        }
        if (destination?.StandId is not (null or "sql")) throw new InvalidDataException("Этот стенд не поддерживается. Обновите приложение ученика.");
        student = new(this, api!);
        screen = student;
        body.Content = student;
        await student!.Load();
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
        if (pageOpen) return;
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
        if (pageOpen) return;
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
        if (pageOpen) return;
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
                await student.SaveDraft();
            }
            catch (Exception ex)when (ex is HttpRequestException or ApiException or TaskCanceledException)
            {
                if (!await Confirm("Связь потеряна. Последние изменения черновика не сохранены. Закрыть приложение?"))
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
        ClosePage(false);
        screen?.Dispose();
        electronics?.Dispose();
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
