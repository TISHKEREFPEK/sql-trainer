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
    readonly ContentControl body = new();
    readonly StackPanel actions = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10
    };
    readonly NotificationState state = new();
    bool quitting;
    bool reconnecting;
    readonly Dictionary<string, string> unsavedDrafts = new();
    internal string? LastTaskId { get; set; }

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
        Width = 1320;
        Height = 900;
        MinWidth = 850;
        MinHeight = 600;
        var header = new Grid
        {
            ColumnDefinitions = new("*,Auto"),
            Margin = new Thickness(18, 12)
        };
        header.Children.Add(Ui.Text(Title, 22));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        var message = Ui.Text("");
        message.Bind(TextBlock.TextProperty, new Binding(nameof(NotificationState.Status)) { Source = state });
        var root = new Grid
        {
            RowDefinitions = new("64,*,Auto")
        };
        root.Children.Add(header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var messageBorder = new Border
        {
            Child = message,
            Padding = new Thickness(18, 8)
        };
        Grid.SetRow(messageBorder, 2);
        root.Children.Add(messageBorder);
        Content = root;
        if (initializeOnOpen)
            Opened += async (_, _) => await Safe(Initialize);
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel);
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
        catch (Exception e)when (e is ApiException or HttpRequestException or TaskCanceledException or InvalidDataException or IOException or JsonException)
        {
            state.Status = e is HttpRequestException or TaskCanceledException ? "Нет связи с преподавателем. Повторите действие после подключения." : e.Message;
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
            state.Status = "Сессия завершилась. Войдите снова; несохранённый черновик остаётся в этом окне.";
        }
        catch (Exception e)when (e is HttpRequestException or TaskCanceledException)
        {
            state.Status = "Нет связи с преподавателем. Повторите вход после восстановления связи.";
        }
        finally
        {
            reconnecting = false;
        }
    }

    public void Message(string text) => state.Status = text;
    internal void Preview(Control view) => body.Content = view;
    void ShowConnect()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQL-Classroom", "student", "connection.json");
        ConnectionSettings? saved = null;
        try
        {
            if (File.Exists(path))
                saved = Wire.Read<ConnectionSettings>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
        }

        var address = Ui.Input(saved?.Address ?? "https://127.0.0.1:47831");
        var fingerprint = Ui.Input(saved?.Fingerprint);
        var connect = Ui.Button("Подключиться", () => Safe(async () =>
        {
            var connection = new ConnectionSettings(address.Text ?? "", fingerprint.Text ?? "");
            var next = new ClassroomApi(connection);
            await next.Get<StatusDto>("status");
            api?.Dispose();
            api = next;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Wire.Write(next.Connection));
            await ShowLogin();
        }));
        body.Content = new Border
        {
            Child = Ui.Card(Ui.Stack(Ui.Text("Подключение к преподавателю", 24), Ui.Text("Введите адрес и полный отпечаток сертификата с компьютера преподавателя."), Ui.Field("Адрес", address), Ui.Field("SHA-256 сертификата", fingerprint), connect)),
            MaxWidth = 650,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    async Task ShowLogin()
    {
        screen?.Dispose();
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
        fields.Children.Add(Ui.Button(setup ? "Создать пароль" : "Войти", () => Safe(async () =>
        {
            if (setup)
                await api.Post<JsonElement>("setup", new PasswordRequest(password.Text ?? ""));
            login = await api.Login(new(teacher ? "teacher" : "student", name.Text ?? "", password.Text ?? ""));
            password.Text = "";
            ThemeManager.Apply(login.Theme);
            actions.Children.Add(Ui.Button("Оформление", () => Safe(async () =>
            {
                var changed = await new AppearanceWindow(api, login.Theme).ShowDialog<ThemePreference?>(this);
                if (changed is not null)
                    login = login with
                    {
                        Theme = changed
                    };
                RefreshSurface();
            })));
            actions.Children.Add(Ui.Button("Выйти", () => Safe(async () =>
            {
                if (student is not null)
                    await student.SaveDraft();
                await api.Logout();
                login = null;
                await ShowLogin();
            })));
            if (teacher)
            {
                actions.Children.Add(Ui.Button("Завершить работу", () => Safe(Quit)));
                var view = new TeacherScreen(this, api, host!);
                screen = view;
                body.Content = view;
                await view.Load();
            }
            else
            {
                student = new(this, api);
                screen = student;
                body.Content = student;
                await student.Load();
            }

            state.Status = "";
        })));
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
            NativeWindowProtection.Set(handle, restricted);
    }

    async void OnKey(object? sender, KeyEventArgs e)
    {
        if (student is not null && e.Key == Key.Enter && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            e.Handled = true;
            await Safe(student.CheckShortcut);
            return;
        }

        if (student?.Restricted != true)
            return;
        if (((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key is Key.C or Key.X or Key.V) || (e.Key == Key.Insert && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0) || (e.Key == Key.Delete && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            e.Handled = true;
            await Safe(student.Violation);
        }
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        if (student?.Restricted == true)
        {
            e.Handled = true;
            e.DragEffects = DragDropEffects.None;
            if (e.RoutedEvent == DragDrop.DropEvent)
                await Safe(student.Violation);
        }
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
        if (teacher && OperatingSystem.IsWindows())
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
        screen?.Dispose();
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
    public static void Set(nint handle, bool enabled)
    {
        SetWindowDisplayAffinity(handle, enabled ? 0x11u : 0u);
    }
}
