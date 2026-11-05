using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class SqlAttemptReviewView : UserControl, IDisposable
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly SqlReviewAttempt source;
    readonly TextEditor editor = new() { ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 15 };
    readonly ContentControl output = new() { Content = Ui.Text("Выполните SQL в проверочной копии.") };
    readonly Button run, stop;
    readonly TextBlock status = Ui.Text("");
    readonly TabControl results = new();
    CancellationTokenSource? execution;
    bool disposed;

    public SqlAttemptReviewView(MainWindow owner, ClassroomApi api, SqlReviewAttempt source, bool showOriginalResult = false)
    {
        this.owner = owner; this.api = api; this.source = source;
        editor.Text = source.Code ?? ""; editor.MinHeight = 150;
        Ui.BindBrush(editor, TextEditor.BackgroundProperty, "Editor"); Ui.BindBrush(editor, TextEditor.ForegroundProperty, "EditorInk"); editor.Padding = new Avalonia.Thickness(14);
        run = Ui.Primary(Ui.Button("Выполнить SQL", Run, () => !disposed && execution is null && source.StateAvailability == "exact"));
        run.IsEnabled = source.StateAvailability == "exact";
        stop = Ui.Button("Остановить", () => { execution?.Cancel(); return Task.CompletedTask; }, () => execution is not null); stop.IsEnabled = false;
        var reset = Ui.Button("Вернуть ответ ученика", () =>
        {
            execution?.Cancel(); editor.Text = source.Code ?? ""; status.Text = "";
            output.Content = Ui.Text("Ответ ученика восстановлен. Выполните SQL в проверочной копии.");
            return Task.CompletedTask;
        });
        AttachedToVisualTree += (_, _) => { ThemeManager.Changed += ApplyTheme; ApplyTheme(); };
        DetachedFromVisualTree += (_, _) => ThemeManager.Changed -= ApplyTheme;
        editor.KeyDown += async (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; await Run(); } };
        var prompt = Ui.Scroll(Ui.Text(source.Prompt ?? "Условие и редакция задания для этой старой попытки не фиксировались.")); prompt.MaxHeight = 85;
        var availability = source.StateAvailability == "exact" ? "Каждый запуск начинается с исходной базы этой попытки. Результаты ученика сохраняются отдельно." :
            "Исходное состояние этой попытки не сохранялось. Точное воспроизведение недоступно.";
        var header = Ui.Stack(Ui.Text(source.StudentName + " · " + source.Title, 20),
            Ui.Text($"{Ui.Time(source.At)}" + (source.Variant is { } variant ? $" · вариант {variant}, редакция {source.Revision}" : ""), 12), prompt, Ui.Text(availability, 12));
        results.ItemsSource = new[] { new TabItem { Header = "Проверочный запуск", Content = output },
            new TabItem { Header = "Исходный результат", Content = source.OriginalResult is { } original ? Result(original, source.Action == "check" ? original.Correct ? "Первоначальная проверка: верно." : "Первоначальная проверка: не зачтено." : "Первоначальный запуск без проверки ответа.") : Ui.Text("Вывод этой старой попытки не сохранялся.") } };
        results.SelectedIndex = showOriginalResult ? 1 : 0;
        var split = new Grid { RowDefinitions = new("*,5,*") };
        split.Children.Add(editor); var grip = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(grip, 1); split.Children.Add(grip);
        Grid.SetRow(results, 2); split.Children.Add(results);
        var root = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 10, Margin = new Avalonia.Thickness(8) };
        root.Children.Add(header); var actions = Ui.Row(run, stop, reset, status); Grid.SetRow(actions, 1); root.Children.Add(actions); Grid.SetRow(split, 2); root.Children.Add(split); Content = root;
    }

    static Control Result(ExecutionDto result, string? description = null)
    {
        var page = Ui.Stack();
        if (description is not null) page.Children.Add(Ui.Text(description));
        if (result.Error is { } error) { page.Children.Add(Ui.Text(error)); if (result.Explanation is { } explanation) page.Children.Add(Ui.Text(explanation, 12)); }
        else page.Children.Add(Ui.Table(result.Output));
        foreach (var table in result.Tables) page.Children.Add(new Expander { Header = table.Name, Content = Ui.Table(table.Data), HorizontalAlignment = HorizontalAlignment.Stretch });
        return Ui.Scroll(page);
    }
    async Task Run()
    {
        if (disposed || execution is not null || source.StateAvailability != "exact") return;
        using var lifetime = new CancellationTokenSource(); execution = lifetime; run.IsEnabled = false; stop.IsEnabled = true; status.Text = "Выполняется…";
        try
        {
            var result = await api.Send<ExecutionDto>(HttpMethod.Post, $"teacher/sql-review/attempts/{source.Id}/execute", new SqlReviewRun(editor.Text), lifetime.Token);
            if (disposed || lifetime.IsCancellationRequested) return;
            output.Content = Result(result); results.SelectedIndex = 0; status.Text = result.Error is null ? "Выполнено" : "Ошибка SQL";
        }
        catch (OperationCanceledException) { if (!disposed) status.Text = "Проверка остановлена."; }
        catch (Exception e) when (e is ApiException or HttpRequestException) { if (!disposed) { status.Text = "Не удалось выполнить проверку."; owner.Error(e.Message); } }
        finally { execution = null; if (!disposed) { run.IsEnabled = true; stop.IsEnabled = false; } }
    }
    void ApplyTheme()
    {
        if (editor.Background is SolidColorBrush brush) editor.SyntaxHighlighting = SqlHighlighting.Create(ThemeManager.Ink(brush.Color.ToString()));
    }
    public void Dispose() { if (disposed) return; disposed = true; execution?.Cancel(); ThemeManager.Changed -= ApplyTheme; }
}
