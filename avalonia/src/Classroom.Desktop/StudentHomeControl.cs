using Avalonia.Controls;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class StudentHomeControl : UserControl
{
    readonly ClassroomApi api;
    readonly MainWindow owner;
    readonly Func<string, string?, Task> open;
    readonly StackPanel page = Ui.Stack();
    string? previous;
    public StudentHomeControl(MainWindow owner, ClassroomApi api, Func<string, string?, Task> open)
    {
        this.owner = owner; this.api = api; this.open = open;
        page.Children.Add(Ui.Text("Загружаются задания занятия…"));
        Content = Ui.Scroll(page); Margin = new Avalonia.Thickness(24);
    }
    public async Task Refresh()
    {
        var data = await api.Get<StudentHomeDto>("student/home");
        var fingerprint = Wire.Write(data);
        if (fingerprint == previous) return;
        previous = fingerprint; page.Children.Clear();
        page.Children.Add(Ui.Text("Моё занятие", 28));
        page.Children.Add(Ui.Text(data.Name + " · " + data.Group));
        var canWork = data.Lesson.Activated && data.Lesson.State == "running";
        page.Children.Add(Ui.Text(!data.Lesson.Activated ? "Дождитесь начала или восстановления занятия преподавателем."
            : data.Lesson.State == "paused" ? "Занятие на паузе. Работы сохранены."
            : data.Lesson.State == "finished" ? "Занятие завершено. Работы сохранены." : "Задания на эту пару", 18));
        if (data.Lesson.AllowSql)
        {
            var section = Ui.Stack(Ui.Text("SQL", 20));
            foreach (var work in data.Required)
            {
                var status = work.State switch { "completed-before" => "Решено раньше", "completed" => "Решено", "in-progress" => "В работе", _ => "Не начато" };
                var action = Ui.Button(work.State == "in-progress" ? "Продолжить" : "Открыть", () => owner.Safe(() => open("sql", work.Id)));
                action.IsEnabled = canWork;
                section.Children.Add(Ui.Card(Ui.Stack(Ui.Text(work.Title, 18), Ui.Text(work.Module + " · " + status), action)));
            }
            if (data.Required.Length == 0) section.Children.Add(Ui.Text("Преподаватель пока не выдал SQL-задания."));
            if (data.Lesson.FreePractice)
            {
                var practice = Ui.Button("Открыть каталог SQL", () => owner.Safe(() => open("sql", null)));
                practice.IsEnabled = canWork; section.Children.Add(practice);
            }
            page.Children.Add(section);
        }
        if (data.Lesson.AllowElectronics)
        {
            var electronics = Ui.Button("Открыть электронику", () => owner.Safe(() => open("electronics", null)));
            electronics.IsEnabled = canWork;
            page.Children.Add(Ui.Card(Ui.Stack(Ui.Text("Электроника", 20), electronics)));
        }
    }
}
