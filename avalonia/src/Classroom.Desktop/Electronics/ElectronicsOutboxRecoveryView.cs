using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Classroom.Desktop.Electronics;

public sealed class ElectronicsOutboxRecoveryView : UserControl
{
    public ElectronicsOutboxRecoveryView(ElectronicsOutboxCandidate candidate, Func<Task> restore, Func<Task> refresh)
    {
        var content = Ui.Stack(Ui.Text("Сохранённые результаты", 20),
            Ui.Text("Очередь результатов не удалось открыть. Исходные файлы сохранены; новая копия работы не создавалась."));
        if (candidate.Backup is { } backup)
        {
            var project = CircuitProject.Parse(backup.ProjectJson);
            content.Children.Add(Ui.Text("Предыдущая копия: " + project.Name, 18));
            content.Children.Add(Ui.Text($"Сохранённых результатов: {backup.Reports.Count}. Из них сдач: {backup.Reports.Count(r => r.Kind == "submitted")}."));
            content.Children.Add(Ui.Text("В предыдущей копии могут отсутствовать последние результаты. Перед заменой прежняя очередь будет сохранена отдельным файлом. Текущая схема и её история сохраняются отдельно."));
            var code = Ui.Input(project.Code, true); code.IsReadOnly = true; code.MaxHeight = 220; content.Children.Add(code);
            content.Children.Add(Ui.Primary(Ui.Button("Восстановить предыдущую очередь", restore)));
        }
        else content.Children.Add(Ui.Text("Проверенной резервной копии нет. Сохраните исходные файлы для восстановления; автоматическая замена недоступна."));
        content.Children.Add(Ui.Button("Проверить файлы заново", refresh));
        content.Children.Add(new Expander { Header = "Подробности", Content = Ui.Text(candidate.Problem) });
        var card = Ui.Card(content); card.MaxWidth = 1000; card.VerticalAlignment = VerticalAlignment.Top;
        Content = Ui.Scroll(card); Margin = new Thickness(20);
    }
}
