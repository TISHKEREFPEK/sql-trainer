using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Classroom.Desktop;

public sealed class ConnectionCodeView : UserControl
{
    public ConnectionCodeView(string? code, bool localOnly, string standName = "SQL")
    {
        var value = new TextBlock
        {
            Text = code ?? "Код пока недоступен", FontSize = code is null ? 24 : 44,
            FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18)
        };
        Content = new Border
        {
            Padding = new Thickness(28),
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Stack(Ui.Text("Код для учеников", 24), Ui.Text("Стенд: " + standName, 22), value,
                Ui.Text("Откройте приложение ученика и введите этот код. Затем войдите по своему логину и паролю."),
                Ui.Text(localOnly
                    ? "Сейчас подключение доступно только на этом компьютере. Доступ для класса включается в настройках сети преподавателя."
                    : "Компьютеры должны быть в одной локальной сети. Код меняется после перезапуска преподавателя."))
        };
    }
}
