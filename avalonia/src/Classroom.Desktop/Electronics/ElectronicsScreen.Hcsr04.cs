using Avalonia.Controls;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    void InspectHcsr04(CircuitPart p)
    {
        properties.Children.Add(Ui.Text("HC-SR04: питание 5 В, общая GND. Trig — импульс не короче 10 мкс, Echo — вход платы. Для Pico Echo подключается через делитель (например, 1 кОм сверху и 2,2 кОм к GND). Между измерениями — не менее 60 мс.",12));
        try{var d=CircuitHcsr04.Devices(project).FirstOrDefault(d=>d.Id==p.Id);properties.Children.Add(Ui.Muted(d is null?"Добавьте исполняемую плату.":!d.Powered?"Проверьте питание 5 В и GND.":!d.Connected?"Проверьте соединения Trig/Echo.":"Подключение определено.",12));}catch(InvalidDataException e){properties.Children.Add(Ui.Text(e.Message,12));}
        var slider=new Slider{Minimum=2,Maximum=400,Value=p.Value,TickFrequency=1,IsSnapToTickEnabled=true};var label=Ui.Text($"Расстояние до цели · {p.Value:F0} см",12);properties.Children.Add(label);properties.Children.Add(slider);
        slider.PropertyChanged+=(_,e)=>{if(e.Property!=Slider.ValueProperty)return;_=Perform(()=>{var i=project.Parts.FindIndex(x=>x.Id==p.Id);if(i<0)return;project.Parts[i]=project.Parts[i] with{Value=Math.Round(slider.Value)};label.Text=$"Расстояние до цели · {slider.Value:F0} см";RecordHistory("control-change");Draw();QueueSave();});};
        Button? target=null;target=Ui.Button(p.Active?"Убрать цель":"Вернуть цель",()=>Perform(()=>{var i=project.Parts.FindIndex(x=>x.Id==p.Id);if(i<0)return;project.Parts[i]=project.Parts[i] with{Active=!project.Parts[i].Active};target!.Content=project.Parts[i].Active?"Убрать цель":"Вернуть цель";RecordHistory("control-change");Draw();QueueSave();}));properties.Children.Add(target);
        properties.Children.Add(Ui.Muted("Расстояние и наличие цели можно менять во время работы. Если цели нет, импульс Echo не поступает; код должен обрабатывать тайм-аут.",12));
    }
}
