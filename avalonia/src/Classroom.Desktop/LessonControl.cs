using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class LessonControl : UserControl
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly ComboBox group = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly CheckBox sql = new() { Content = "SQL", IsChecked = true };
    readonly CheckBox electronics = new() { Content = "Электроника", IsChecked = true };
    readonly CheckBox admission = new() { Content = "Разрешить вход учеников", IsChecked = true };
    readonly CheckBox allGroups = new() { Content = "Свободный вход из разных групп" };
    readonly CheckBox registration = new() { Content = "Разрешить регистрацию в выбранную группу" };
    readonly CheckBox practice = new() { Content = "Свободная практика по каталогу" };
    readonly TextBlock status = Ui.Text("Занятие ещё не начато", 20);
    readonly TextBlock context = Ui.Muted("");
    readonly TextBlock participants = Ui.Text("—", 20);
    readonly TextBlock entry = Ui.Text("");
    readonly TextBlock signUp = Ui.Text("");
    readonly TextBlock issuedWorks = Ui.Muted("Задания ещё не выданы.");
    readonly StackPanel facts;
    readonly ScrollViewer lessonDetails;
    readonly Expander settings;
    readonly Expander work;
    readonly StackPanel history = Ui.Stack();
    readonly Button start;
    readonly Button finish;
    readonly Button apply;
    readonly Button results;
    readonly LessonTaskPicker taskPicker;
    readonly LessonElectronicsPicker electronicsPicker;
    readonly Func<LessonView, Task> openHistoryResults;
    string draftId = Guid.NewGuid().ToString("N");
    LessonView? current;
    LessonView? shownLesson;
    LessonView[] shownHistory = [];
    bool busy;
    bool binding;
    bool loaded;
    int refreshVersion;
    GroupDto[] knownGroups = [];

    public LessonControl(MainWindow owner, ClassroomApi api, Func<Task> connection, Func<LessonView, Task> openResults)
    {
        this.owner = owner; this.api = api;
        taskPicker = new(owner, api);
        electronicsPicker = new(owner, api);
        start = GraphiteUi.Action("Начать занятие", () => Run(async () =>
        {
            var creating = current is null;
            current ??= await api.Post<LessonView>("teacher/lessons", new CreateLessonRequest(draftId, Policy()));
            if (creating || current.State == "preparing") { await taskPicker.Save(current.Id); await electronicsPicker.Save(current.Id); }
            var action = current.State == "preparing" ? "start" : !current.Activated ? "restore" : current.State == "paused" ? "resume" : "pause";
            current = await api.Post<LessonView>($"teacher/lessons/{current.Id}/{action}");
        }), () => !busy, primary: true);
        finish = GraphiteUi.Action("Завершить занятие", () => Run(async () =>
        {
            if (current is not null && await owner.Confirm("Завершить занятие? Работы и результаты сохранятся, вход и выполнение заданий будут закрыты."))
            {
                await api.Post<LessonView>($"teacher/lessons/{current.Id}/finish");
                current = null;
                draftId = Guid.NewGuid().ToString("N");
            }
        }), () => !busy && current is not null);
        apply = GraphiteUi.Action("Применить настройки", () => Run(async () =>
        {
            if (current is not null) current = await api.Put<LessonView>($"teacher/lessons/{current.Id}/policy", Policy());
        }), () => !busy && current is not null);
        results = GraphiteUi.Action("Результаты занятия",
            () => current is { } lesson ? owner.Safe(() => openResults(lesson)) : Task.CompletedTask,
            () => current?.StartedAt is not null);
        var connectionButton = GraphiteUi.Action("Подключить учеников", connection, () => true);

        facts = Ui.Stack(GraphiteUi.Separator(), GraphiteUi.WrapActions(
            Fact("Вошли на занятие", participants), Fact("Вход учеников", entry), Fact("Регистрация", signUp)));
        AutomationProperties.SetName(group, "Группа занятия");
        var setup = Ui.Stack(Ui.Field("Группа", group), Ui.Text("Направления", 16),
            GraphiteUi.WrapActions(sql, electronics), practice,
            GraphiteUi.Separator(), Ui.Text("Доступ учеников", 16), admission, allGroups, registration,
            GraphiteUi.WrapActions(apply));
        settings = Section("Настройки занятия", setup, expanded: true);
        work = Section("Задания занятия", Ui.Stack(taskPicker, electronicsPicker));
        var workTitle = Ui.Stack(Ui.Text("Задания занятия"), issuedWorks);
        workTitle.Spacing = 2;
        work.Header = workTitle;
        taskPicker.IssuedChanged += UpdateIssuedWorks;
        electronicsPicker.IssuedChanged += UpdateIssuedWorks;
        var chooseWork = GraphiteUi.Action("Выбрать задания", () => Reveal(work), () => !busy);
        var operations = GraphiteUi.WrapActions(start, connectionButton, chooseWork, results, finish);
        history.Children.Add(Ui.Muted("Завершённых занятий пока нет."));
        var previous = Section("История занятий", history);
        var overview = Ui.Stack(Ui.Text("Занятие", 26),
            GraphiteUi.Panel(Ui.Stack(Ui.Stack(status, context), facts, operations)));
        lessonDetails = Ui.Scroll(Ui.Stack(work, settings, previous));
        var layout = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        layout.Children.Add(overview);
        Grid.SetRow(lessonDetails, 1);
        layout.Children.Add(lessonDetails);
        Content = layout;
        openHistoryResults = openResults;
        group.SelectionChanged += (_, _) => UpdateSummary();
        foreach (var option in new[] { sql, electronics, admission, allGroups, registration, practice })
            option.IsCheckedChanged += (_, _) => UpdateSummary();
        UpdateSummary();
    }

    static Expander Section(string title, Control content, bool expanded = false)
    {
        var section = new Expander
        {
            Header = title, Content = content, IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(section, title);
        section.Classes.Add("teacher-lesson-section");
        return section;
    }

    static StackPanel Fact(string label, TextBlock value)
    {
        value.MinHeight = 30;
        var fact = Ui.Stack(value, Ui.Muted(label));
        fact.Spacing = 2;
        fact.Margin = new Thickness(0, 0, 24, 0);
        return fact;
    }

    void UpdateIssuedWorks()
    {
        if (taskPicker.LessonId != current?.Id || electronicsPicker.LessonId != current?.Id) return;
        var sqlTasks = taskPicker.Issued;
        var schemes = electronicsPicker.Issued;
        if (sqlTasks.Count == 0 && schemes.Count == 0)
        {
            issuedWorks.Text = "Задания ещё не выданы.";
            return;
        }
        var lines = new List<string>(2);
        if (sqlTasks.Count > 0) lines.Add(Preview("SQL", sqlTasks.Select(x => x.Title), sqlTasks.Count));
        if (schemes.Count > 0) lines.Add(Preview("Схемы", schemes.Select(x => x.Title), schemes.Count));
        issuedWorks.Text = string.Join("\n", lines);

        static string Preview(string subject, IEnumerable<string> titles, int count) =>
            subject + " · " + string.Join(", ", titles.Take(2)) + (count > 2 ? $" · ещё {count - 2}" : "");
    }

    static Task Reveal(Expander section)
    {
        section.IsExpanded = true;
        Dispatcher.UIThread.Post(() => section.BringIntoView(), DispatcherPriority.Loaded);
        return Task.CompletedTask;
    }

    LessonPolicyRequest Policy() => new((group.SelectedItem as GroupChoice)?.Id is { Length: > 0 } id ? id : null,
        sql.IsChecked == true, electronics.IsChecked == true, admission.IsChecked == true,
        allGroups.IsChecked == true, registration.IsChecked == true, practice.IsChecked == true);
    static LessonPolicyRequest AppliedPolicy(LessonView lesson) => new(lesson.GroupId, lesson.AllowSql, lesson.AllowElectronics,
        lesson.AdmissionOpen, lesson.OpenToAllGroups, lesson.RegistrationOpen, lesson.FreePractice);

    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        refreshVersion++;
        UpdateSummary();
        try { await owner.Safe(action); await owner.Safe(() => Refresh(true)); }
        finally { busy = false; UpdateSummary(); }
    }
    public async Task Refresh(bool force = false, GroupDto[]? availableGroups = null, CatalogueItem[]? availableTasks = null)
    {
        if (busy && !force) return;
        var request = ++refreshVersion;
        var dashboard = await api.Get<LessonDashboard>("teacher/lessons");
        if (request != refreshVersion) return;
        if (availableGroups is not null) knownGroups = availableGroups;
        var before = shownLesson;
        var desired = Policy();
        current = shownLesson = dashboard.Current;
        var lessonChanged = !loaded || before?.Id != current?.Id;
        if (lessonChanged) issuedWorks.Text = current is null ? "Задания ещё не выданы." : "Загрузка выдачи…";
        if (before is not null && current is null)
        {
            draftId = Guid.NewGuid().ToString("N");
            desired = desired with { GroupId = null };
        }
        if (lessonChanged || before?.StartedAt is null && current?.StartedAt is not null)
        {
            settings.IsExpanded = current?.StartedAt is null;
            work.IsExpanded = false;
            lessonDetails.Offset = default;
        }
        loaded = true;
        if (current is not null && (lessonChanged || before is not null && desired == AppliedPolicy(before)))
            desired = AppliedPolicy(current);
        BindPolicy(desired);
        RenderHistory(dashboard.History);
        UpdateSummary();
        await taskPicker.Refresh(current?.Id, availableTasks);
        if (request == refreshVersion) await electronicsPicker.Refresh(current?.Id);
    }

    void BindPolicy(LessonPolicyRequest desired)
    {
        var choices = new[] { new GroupChoice("", "Выберите группу") }
            .Concat(knownGroups.Select(g => new GroupChoice(g.Id, g.Name))).ToArray();
        binding = true;
        try
        {
            if (!group.Items.OfType<GroupChoice>().SequenceEqual(choices)) group.ItemsSource = choices;
            var selected = group.Items.OfType<GroupChoice>().FirstOrDefault(choice => choice.Id == (desired.GroupId ?? ""))
                ?? group.Items.OfType<GroupChoice>().First();
            if (!ReferenceEquals(group.SelectedItem, selected)) group.SelectedItem = selected;
            sql.IsChecked = desired.AllowSql;
            electronics.IsChecked = desired.AllowElectronics;
            admission.IsChecked = desired.AdmissionOpen;
            allGroups.IsChecked = desired.OpenToAllGroups;
            registration.IsChecked = desired.RegistrationOpen;
            practice.IsChecked = desired.FreePractice;
        }
        finally { binding = false; }
    }

    void UpdateSummary()
    {
        if (binding) return;
        status.Text = current switch
        {
            null or { State: "preparing" } => "Занятие ещё не начато",
            { Activated: false } => "Занятие нужно восстановить",
            { State: "paused" } => "Занятие на паузе",
            _ => "Занятие идёт"
        };
        var policy = current is null ? Policy() : AppliedPolicy(current);
        var directions = (policy.AllowSql, policy.AllowElectronics) switch
        {
            (true, true) => "SQL и электроника",
            (true, false) => "SQL",
            (false, true) => "Электроника",
            _ => "Выберите направление"
        };
        var groupName = current?.GroupName ?? (group.SelectedItem is GroupChoice { Id.Length: > 0 } choice ? choice.Name : null);
        context.Text = groupName is null ? directions : groupName + " · " + directions;
        facts.IsVisible = current is not null;
        var running = current is { Activated: true, State: "running" };
        participants.Text = current?.Participants.ToString() ?? "—";
        entry.Text = !running || !policy.AdmissionOpen ? "Закрыт" : policy.OpenToAllGroups ? "Все группы" : "Выбранная группа";
        signUp.Text = running && policy.RegistrationOpen && policy.GroupId is not null ? "Открыта" : "Закрыта";
        start.Content = current is null || current.State == "preparing" ? "Начать занятие" : !current.Activated ? "Восстановить занятие" : current.State == "paused" ? "Продолжить занятие" : "Пауза для всех";
        start.IsEnabled = !busy;
        finish.IsVisible = apply.IsVisible = current is not null;
        finish.IsEnabled = apply.IsEnabled = current is not null && !busy;
        results.IsVisible = current?.StartedAt is not null;
        results.IsEnabled = !busy && results.IsVisible;
        settings.IsEnabled = work.IsEnabled = !busy;
        settings.Header = current is not null && Policy() != AppliedPolicy(current)
            ? "Настройки занятия · есть изменения" : "Настройки занятия";
    }

    void RenderHistory(LessonView[] lessons)
    {
        if (shownHistory.SequenceEqual(lessons)) return;
        shownHistory = lessons;
        history.Children.Clear();
        foreach (var lesson in lessons)
        {
            var date = Ui.Time(lesson.StartedAt ?? lesson.CreatedAt);
            var button = GraphiteUi.Action("Результаты", () => owner.Safe(() => openHistoryResults(lesson)), () => true);
            AutomationProperties.SetName(button, $"Результаты занятия {lesson.GroupName}, {date}");
            history.Children.Add(GraphiteUi.WorkRow(lesson.GroupName, $"{date} · вошли: {lesson.Participants}", button));
        }
        if (lessons.Length == 0) history.Children.Add(Ui.Muted("Завершённых занятий пока нет."));
    }
    sealed record GroupChoice(string Id, string Name) { public override string ToString() => Name; }
}
