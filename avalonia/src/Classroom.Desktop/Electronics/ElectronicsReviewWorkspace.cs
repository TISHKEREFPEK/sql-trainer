using Avalonia.Controls;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

// This copy has no ClassroomApi or student outbox. Its runner uses the usual
// compiler/process limits, and all editable files belong to a new temp directory.
public sealed class ElectronicsReviewWorkspace : UserControl, IDisposable
{
    readonly MainWindow owner;
    readonly ElectronicsReviewDetail submission;
    readonly string directory = Path.Combine(Path.GetTempPath(), "classroom-review-" + Guid.NewGuid().ToString("N"));
    readonly ContentControl body = new();
    ElectronicsScreen? editor;
    bool disposed;

    public ElectronicsReviewWorkspace(MainWindow owner, ElectronicsReviewDetail submission)
    {
        this.owner = owner; this.submission = submission;
        CircuitProject.Parse(submission.ProjectJson);
        var prompt = Ui.Scroll(Ui.Text(submission.Prompt)); prompt.MaxHeight = 80;
        var tools = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        tools.Children.Add(Ui.Text("Проверочная копия. Сданная версия и результаты ученика сохраняются отдельно.", 12));
        var reset = Ui.Button("Восстановить сданную версию", () => owner.Safe(() => { Reset(); return Task.CompletedTask; }));
        Grid.SetColumn(reset, 1); tools.Children.Add(reset);
        var header = Ui.Stack(Ui.Text(submission.Item.StudentName + " · " + submission.Item.Title, 20), prompt, tools);
        var root = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        root.Children.Add(header); Grid.SetRow(body, 1); root.Children.Add(body); Content = root;
        Reset();
        DetachedFromVisualTree += (_, _) => editor?.Suspend();
    }

    void Reset()
    {
        if (disposed) return;
        editor?.Dispose();
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "review.json");
        File.WriteAllText(file, submission.ProjectJson);
        editor = new ElectronicsScreen(owner, file, teacher: true);
        body.Content = editor;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; editor?.Dispose(); editor = null; body.Content = null;
        var target = Path.GetFullPath(directory);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("classroom-review-", StringComparison.Ordinal))
            throw new InvalidOperationException("Некорректная папка проверочной копии.");
        try { if (Directory.Exists(target)) Directory.Delete(target, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { owner.Error("Не удалось удалить временную проверочную копию. Закройте использующую её программу."); }
    }
}
