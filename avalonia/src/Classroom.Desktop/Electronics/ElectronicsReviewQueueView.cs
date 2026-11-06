using Avalonia.Controls;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed class ElectronicsReviewQueueView : UserControl, IDisposable
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly bool restrictedReview;
    readonly string routeRoot;
    readonly Control queueRoot;
    ElectronicsReviewWorkspace? workspace;
    readonly ListBox list = new();
    readonly ContentControl detail = new() { Content = Ui.Text("Выберите сданную работу.") };
    readonly CheckBox showAll = new() { Content = "Показывать проверенные работы" };
    readonly ComboBox group = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    readonly ComboBox lesson = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    readonly TextBlock count = Ui.Text("");
    readonly Button earlier, later;
    int offset, generation;
    bool disposed, loadingList, hasMore, singleReport;

    public ElectronicsReviewQueueView(MainWindow owner, ClassroomApi api, bool restrictedReview = false)
    {
        this.owner = owner; this.api = api;
        this.restrictedReview = restrictedReview;
        routeRoot = restrictedReview ? "review-access/electronics" : "teacher/reviews/electronics";
        group.IsVisible = lesson.IsVisible = showAll.IsVisible = !restrictedReview;
        earlier = Ui.Button("Раньше", () => owner.Safe(() => Load(offset + 30)), () => hasMore);
        later = Ui.Button("Позже", () => owner.Safe(() => Load(Math.Max(0, offset - 30))), () => offset > 0);
        list.SelectionChanged += async (_, _) => { if (!loadingList && list.SelectedItem is Row row) await owner.Safe(() => Open(row.Item)); };
        showAll.IsCheckedChanged += async (_, _) => await owner.Safe(() => Load(0));
        group.SelectionChanged += async (_, _) => { if (!loadingList) await owner.Safe(() => Load(0)); };
        lesson.SelectionChanged += async (_, _) => { if (!loadingList) await owner.Safe(() => Load(0)); };
        var left = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8 };
        left.Children.Add(Ui.Stack(group, lesson, showAll)); Grid.SetRow(list, 1); left.Children.Add(list);
        var nav = Ui.Row(later, earlier); Grid.SetRow(nav, 2); left.Children.Add(nav);
        var body = new Grid { ColumnDefinitions = new("320,*"), ColumnSpacing = 16 };
        body.Children.Add(left); Grid.SetColumn(detail, 1); body.Children.Add(detail);
        var root = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12, Margin = new Avalonia.Thickness(12) };
        root.Children.Add(Ui.Row(Ui.Text("Проверка схем", 22), count, Ui.Button("Обновить", () => owner.Safe(() => Load(offset)))));
        Grid.SetRow(body, 1); root.Children.Add(body); queueRoot = root; Content = root;
    }

    public async Task Load(int start = 0)
    {
        if (disposed) return;
        var version = ++generation;
        var groupId = (group.SelectedItem as Choice)?.Id ?? "*";
        var lessonId = (lesson.SelectedItem as Choice)?.Id ?? "*";
        var query = $"{routeRoot}?offset={start}";
        if (!restrictedReview) query += $"&status={(showAll.IsChecked == true ? "all" : "pending")}";
        if (groupId != "*") query += "&groupId=" + Uri.EscapeDataString(groupId);
        if (lessonId != "*") query += "&lessonId=" + Uri.EscapeDataString(lessonId);
        var data = await api.Get<ElectronicsReviewQueue>(query);
        var filters = restrictedReview ? new ElectronicsReviewFilters([], []) : await api.Get<ElectronicsReviewFilters>("teacher/reviews/electronics/filters");
        if (disposed || version != generation) return;
        offset = start;
        count.Text = $"Ожидают: {data.PendingCount}";
        var selected = (list.SelectedItem as Row)?.Item;
        loadingList = true;
        group.ItemsSource = new[] { new Choice("*", "Все группы"), new Choice("none", "Без группы") }.Concat(filters.Groups.Select(g => new Choice(g.Id, g.Name))).ToArray();
        group.SelectedItem = ((Choice[])group.ItemsSource).FirstOrDefault(g => g.Id == groupId) ?? ((Choice[])group.ItemsSource)[0];
        lesson.ItemsSource = new[] { new Choice("*", "Все занятия") }.Concat(filters.Lessons.Select(l => new Choice(l.Id, ElectronicsLabels.Date(l.CreatedAt) + " · " + l.GroupName))).ToArray();
        lesson.SelectedItem = ((Choice[])lesson.ItemsSource).FirstOrDefault(l => l.Id == lessonId) ?? ((Choice[])lesson.ItemsSource)[0];
        list.ItemsSource = data.Items.Select(i => new Row(i)).ToArray();
        hasMore = data.HasMore; earlier.IsEnabled = hasMore; later.IsEnabled = start > 0;
        list.SelectedItem = (list.ItemsSource as Row[])!.FirstOrDefault(r => r.Item.StudentId == selected?.StudentId && r.Item.ReportId == selected.ReportId);
        loadingList = false;
        if (data.Items.Length == 0) detail.Content = Ui.Text(showAll.IsChecked == true ? "Сданных работ пока нет." : "Все работы проверены.");
    }

    public Task OpenReport(string studentId, string reportId)
    {
        if (!singleReport)
        {
            singleReport = true;
            if (detail.Parent is Panel parent) parent.Children.Remove(detail);
            Content = detail;
        }
        return Open(new(studentId,"",null,"",reportId,null,null,"",0,"",null));
    }
    async Task Open(ElectronicsReviewItem item)
    {
        if (disposed) return;
        var version = ++generation;
        var route = $"{routeRoot}/{item.StudentId}/{item.ReportId}";
        var data = await api.Get<ElectronicsReviewDetail>(route);
        item = data.Item;
        if (disposed || version != generation) return;
        var decision = new ComboBox { ItemsSource = new[] { new Choice("accepted", "Принято"), new Choice("changes-requested", "На доработку"), new Choice("rejected", "Не принято") }, SelectedIndex = 0, MinWidth = 150 };
        var grade = new ComboBox { ItemsSource = new[] { "Без оценки", "1", "2", "3", "4", "5" }, SelectedIndex = data.Item.Grade ?? 0, MinWidth = 120 };
        var comment = Ui.Input("", true); comment.Watermark = "Комментарий ученику"; comment.MaxLength = 8000; comment.MinHeight = 80; comment.MaxHeight = 160;
        var prior = data.History.LastOrDefault(r => r.Action != "allow-resubmit");
        comment.Text = prior?.Comment ?? "";
        if (prior is not null) decision.SelectedItem = ((Choice[])decision.ItemsSource!).First(c => c.Id == prior.Action);
        var failure = Ui.Text("");
        string? operationId = null;
        ElectronicsReviewRequest? pendingRequest = null;
        async Task Save(string action)
        {
            var request = new ElectronicsReviewRequest(action, comment.Text ?? "", action == "allow-resubmit" || grade.SelectedIndex == 0 ? null : grade.SelectedIndex,
                data.History.LastOrDefault()?.Id, data.AssignmentVersion);
            if (request != pendingRequest) { operationId = Guid.NewGuid().ToString("N"); pendingRequest = request; }
            try
            {
                await api.Put<ElectronicsReviewDecision>(route + "/decisions/" + operationId, request);
                if (disposed) return;
                if (!singleReport) await Load(offset);
                if (disposed) return;
                await Open(item);
                if (disposed) return;
                owner.Message(action == "allow-resubmit" ? "Пересдача разрешена." : "Результат проверки сохранён.");
            }
            catch (ApiException e) { failure.Text = e.Message; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            { failure.Text = "Нет связи с сервером. Комментарий сохранён в форме; повторите отправку или обновите работу."; }
            catch (ObjectDisposedException) when (disposed) { }
        }
        var save = Ui.Primary(Ui.Button("Сохранить результат", () => Save(((Choice)decision.SelectedItem!).Id)));
        var grant = Ui.Button("Разрешить пересдачу", () => Save("allow-resubmit"));
        grant.IsVisible = data.Item.AssignmentId is not null && data.IsLatestSubmission && !data.CanSubmit && data.Item.State != "awaiting-review";
        var history = Ui.Stack(data.History.Select(r => (Control)Ui.Text($"{ElectronicsLabels.Date(r.At)} · {ElectronicsReviewLabels.State(r.Action)}{(r.Grade is { } value ? $" · Оценка: {value}" : "")}\n{r.Comment}", 13)).ToArray());
        var codePanel = new ContentControl();
        var codeButton = Ui.Button("Код проверки у ученика", () => owner.Safe(async () =>
        {
            var code = await api.Post<ReviewAccessCode>("teacher/review-access", new CreateReviewAccess(item.StudentId));
            var input = Ui.Input(code.Code); input.IsReadOnly = true; input.Width = 190;
            codePanel.Content = Ui.Stack(Ui.Text("Одноразовый код · " + item.StudentName + " · до " + ElectronicsLabels.Date(code.Expires), 12),
                Ui.Row(input, Ui.Button("Отозвать доступ", () => owner.Safe(async () => { await api.Delete<System.Text.Json.JsonElement>("teacher/review-access/" + code.Id); codePanel.Content = Ui.Text("Доступ отозван."); }))));
        }));
        codeButton.IsVisible = !restrictedReview;
        ToolTip.SetTip(codeButton, "Новый код завершает прежний доступ к проверке этого ученика.");
        var prompt = Ui.Scroll(Ui.Text(data.Prompt)); prompt.MaxHeight = 90;
        var summary = Ui.Stack(Ui.Text(item.StudentName + " · " + item.GroupName, 20), Ui.Text(item.Title, 18), prompt,
            Ui.Text("Сдано: " + ElectronicsLabels.Date(item.SubmittedAt), 12), Ui.Row(Ui.Stack(Ui.Text("Результат", 12), decision), Ui.Stack(Ui.Text("Оценка", 12), grade)), comment, Ui.Row(save, grant), failure,
            new Expander { Header = "История проверки", Content = history });
        var pane = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        var form = Ui.Scroll(summary); form.MaxHeight = 360; pane.Children.Add(form);
        var snapshot = new ElectronicsSnapshotView(new(data.ProjectJson, "submitted", item.SubmittedAt, ""));
        var preview = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        preview.Children.Add(Ui.Stack(Ui.Row(Ui.Button("Открыть и запустить копию", () => owner.Safe(() => OpenWorkspace(data))), codeButton), codePanel));
        Grid.SetRow(snapshot, 1); preview.Children.Add(snapshot);
        Grid.SetRow(preview, 1); pane.Children.Add(preview); detail.Content = pane;
    }
    Task OpenWorkspace(ElectronicsReviewDetail submission)
    {
        if (disposed) return Task.CompletedTask;
        workspace?.Dispose(); workspace = new(owner, submission);
        var root = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 10 };
        root.Children.Add(Ui.Button("Назад к проверке", () => { CloseWorkspace(); return Task.CompletedTask; }));
        Grid.SetRow(workspace, 1); root.Children.Add(workspace); Content = root;
        return Task.CompletedTask;
    }
    void CloseWorkspace() { workspace?.Dispose(); workspace = null; Content = singleReport ? detail : queueRoot; }
    public void Dispose() { disposed = true; generation++; workspace?.Dispose(); workspace = null; }
    sealed record Row(ElectronicsReviewItem Item)
    {
        public override string ToString() => $"{Item.StudentName} · {Item.GroupName}\n{Item.Title}\n{ElectronicsReviewLabels.State(Item.State)}{(Item.Grade is { } grade ? $" · {grade}" : "")} · {ElectronicsLabels.Date(Item.SubmittedAt)}";
    }
    sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
}
