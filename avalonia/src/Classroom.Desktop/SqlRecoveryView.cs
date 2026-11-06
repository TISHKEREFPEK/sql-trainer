using Avalonia.Controls;

namespace Classroom.Desktop;

public sealed class SqlRecoveryView : UserControl
{
    public SqlRecoveryView(MainWindow owner, SqlRecoveryCandidate candidate, Func<Task> done)
    {
        var content = Ui.Stack(Ui.Text("Восстановление SQL-работы", 24),
            Ui.Text("Не удалось прочитать сохранённую SQL-работу. Исходный файл сохранён."),
            new Expander { Header = "Подробности ошибки", Content = Ui.Text(candidate.Problem) });
        if (candidate.Backup is { } backup)
        {
            content.Children.Add(Ui.Text("Предыдущая копия: " + backup.Task.Title, 18));
            content.Children.Add(Ui.Text("В ней могут отсутствовать последние изменения. Исходный повреждённый файл будет сохранён отдельно."));
            var code = Ui.Input(backup.Code, true); code.IsReadOnly = true; code.MaxHeight = 280; content.Children.Add(code);
            if (backup.Execution is not null) content.Children.Add(Ui.Text("Есть незавершённый SQL-запрос. Его статус будет проверен после входа."));
            content.Children.Add(Ui.Primary(Ui.Button("Восстановить предыдущую копию", () => owner.Safe(async () =>
            {
                SqlLocalStore.RestoreBackup(candidate);
                owner.Message("Копия восстановлена. Повреждённый оригинал сохранён отдельно.");
                await done();
            }))));
        }
        else content.Children.Add(Ui.Text("Проверенной резервной копии нет. Сохраните исходный файл для разбора; автоматическая замена недоступна."));
        content.Children.Add(Ui.Button("Вернуться ко входу", () => owner.Safe(done)));
        var card = Ui.Card(content); card.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top; card.MaxWidth = 1000;
        Content = Ui.Scroll(card); Margin = new Avalonia.Thickness(24);
    }
}
