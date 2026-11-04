using Avalonia.Controls;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class LessonControl : UserControl
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly ComboBox group = new() { MinWidth = 230 };
    readonly CheckBox sql = new() { Content = "SQL", IsChecked = true };
    readonly CheckBox electronics = new() { Content = "Электроника", IsChecked = true };
    readonly CheckBox admission = new() { Content = "Разрешить вход учеников", IsChecked = true };
    readonly CheckBox allGroups = new() { Content = "Свободный вход из разных групп" };
    readonly CheckBox registration = new() { Content = "Разрешить регистрацию в выбранную группу" };
    readonly CheckBox practice = new() { Content = "Свободная практика по каталогу" };
    readonly TextBlock status = Ui.Text("Занятие ещё не начато", 20);
    readonly TextBlock participants = Ui.Text("");
    readonly StackPanel history = Ui.Stack();
    readonly Button start;
    readonly Button finish;
    readonly Button apply;
    string draftId = Guid.NewGuid().ToString("N");
    LessonView? current;
    bool busy;
    GroupDto[] knownGroups = [];

    public LessonControl(MainWindow owner, ClassroomApi api, Func<Task> connection)
    {
        this.owner = owner; this.api = api;
        start = Ui.Primary(Ui.Button("Начать занятие", () => Run(async () =>
        {
            current ??= await api.Post<LessonView>("teacher/lessons", new CreateLessonRequest(draftId, Policy()));
            var action = current.State == "preparing" ? "start" : !current.Activated ? "restore" : current.State == "paused" ? "resume" : "pause";
            current = await api.Post<LessonView>($"teacher/lessons/{current.Id}/{action}");
        })));
        finish = Ui.Button("Завершить занятие", () => Run(async () =>
        {
            if (current is not null && await owner.Confirm("Завершить занятие? Работы и результаты сохранятся, вход и выполнение заданий будут закрыты."))
            {
                await api.Post<LessonView>($"teacher/lessons/{current.Id}/finish");
                current = null;
                draftId = Guid.NewGuid().ToString("N");
            }
        }));
        apply = Ui.Button("Применить настройки", () => Run(async () =>
        {
            if (current is not null) current = await api.Put<LessonView>($"teacher/lessons/{current.Id}/policy", Policy());
        }));
        Content = Ui.Scroll(Ui.Stack(Ui.Text("Занятие", 26),
            Ui.Card(Ui.Stack(status, participants, Ui.Row(start, finish, Ui.Button("Подключить учеников", connection)))),
            Ui.Card(Ui.Stack(Ui.Field("Группа", group), Ui.Row(sql, electronics), admission, allGroups, registration,
                practice, apply)),
            Ui.Card(Ui.Stack(Ui.Text("Предыдущие занятия", 20), history))));
    }

    LessonPolicyRequest Policy() => new((group.SelectedItem as GroupChoice)?.Id is { Length: > 0 } id ? id : null,
        sql.IsChecked == true, electronics.IsChecked == true, admission.IsChecked == true,
        allGroups.IsChecked == true, registration.IsChecked == true, practice.IsChecked == true);
    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; start.IsEnabled = finish.IsEnabled = apply.IsEnabled = false;
        try { await owner.Safe(action); await Refresh(true); }
        finally { busy = false; start.IsEnabled = true; finish.IsEnabled = apply.IsEnabled = current is not null; }
    }
    public async Task Refresh(bool force = false, GroupDto[]? availableGroups = null)
    {
        if (busy && !force) return;
        var dashboard = await api.Get<LessonDashboard>("teacher/lessons");
        if (availableGroups is not null) knownGroups = availableGroups;
        var groups = knownGroups;
        var before = current?.Id; current = dashboard.Current;
        if (before is not null && current is null) draftId = Guid.NewGuid().ToString("N");
        var selected = (group.SelectedItem as GroupChoice)?.Id;
        group.ItemsSource = new[] { new GroupChoice("", "Выберите группу") }.Concat(groups.Select(g => new GroupChoice(g.Id, g.Name))).ToArray();
        group.SelectedItem = group.Items.OfType<GroupChoice>().FirstOrDefault(g => g.Id == (force || before != current?.Id ? current?.GroupId : selected)) ?? group.Items.OfType<GroupChoice>().First();
        if ((force || before != current?.Id) && current is not null)
        {
            sql.IsChecked = current.AllowSql; electronics.IsChecked = current.AllowElectronics;
            admission.IsChecked = current.AdmissionOpen; allGroups.IsChecked = current.OpenToAllGroups;
            registration.IsChecked = current.RegistrationOpen; practice.IsChecked = current.FreePractice;
        }
        status.Text = current is null ? "Занятие ещё не начато" : !current.Activated ? "Вход закрыт: начните или восстановите занятие"
            : current.State == "paused" ? "Занятие на паузе" : "Занятие идёт · " + current.GroupName;
        participants.Text = current is null ? "Выберите группу и направления, затем начните занятие." : "Вошло учеников: " + current.Participants;
        start.Content = current is null || current.State == "preparing" ? "Начать занятие" : !current.Activated ? "Восстановить занятие" : current.State == "paused" ? "Продолжить занятие" : "Пауза для всех";
        finish.IsEnabled = apply.IsEnabled = current is not null && !busy;
        history.Children.Clear();
        foreach (var row in dashboard.History)
            history.Children.Add(Ui.Text($"{DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt).ToLocalTime():dd.MM.yyyy HH:mm} · {row.GroupName} · учеников: {row.Participants}"));
        if (dashboard.History.Length == 0) history.Children.Add(Ui.Text("Завершённых занятий пока нет."));
    }
    sealed record GroupChoice(string Id, string Name) { public override string ToString() => Name; }
}
