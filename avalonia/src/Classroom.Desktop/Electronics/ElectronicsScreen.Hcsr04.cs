namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    void InspectHcsr04(CircuitPart p)
    {
        properties.Children.Add(Ui.Text("HC-SR04: питание 5 В, общая GND. Trig — импульс не короче 10 мкс, Echo — вход платы. Для Pico Echo подключается через делитель (например, 1 кОм сверху и 2,2 кОм к GND). Между измерениями — не менее 60 мс.",12));
        try{var d=CircuitHcsr04.Devices(project).FirstOrDefault(d=>d.Id==p.Id);properties.Children.Add(Ui.Muted(d is null?"Добавьте исполняемую плату.":!d.Powered?"Проверьте питание 5 В и GND.":!d.Connected?"Проверьте соединения Trig/Echo.":"Подключение определено.",12));}catch(InvalidDataException e){properties.Children.Add(Ui.Text(e.Message,12));}
        AddInspectorSlider(p, distance);
        AddInspectorToggle(p);
        properties.Children.Add(Ui.Muted("Расстояние и наличие цели можно менять во время работы. Если цели нет, импульс Echo не поступает; код должен обрабатывать тайм-аут.",12));
    }
}
