using Avalonia.Controls;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    void InspectDht11(CircuitPart p)
    {
        properties.Children.Add(Ui.Text("DHT11: VDD 3–5,5 В, DATA, NC (не подключать), GND. Рекомендуется резистор 5,1 кОм от DATA к питанию. Для Pico используйте 3,3 В. После включения нужен прогрев 1 с; библиотека читает не чаще раза в 2 с.",12));
        var temperature=new Slider{Minimum=0,Maximum=50,Value=p.Value,TickFrequency=1,IsSnapToTickEnabled=true};
        var humidity=new Slider{Minimum=20,Maximum=90,Value=CircuitDht11.Humidity(p),TickFrequency=1,IsSnapToTickEnabled=true};
        var t=Ui.Text($"Температура · {p.Value:F0} °C",12);var h=Ui.Text($"Влажность · {CircuitDht11.Humidity(p)} %",12);
        properties.Children.Add(t);properties.Children.Add(temperature);properties.Children.Add(h);properties.Children.Add(humidity);
        void Apply()
        {
            _=Perform(()=>{
                var i=project.Parts.FindIndex(part=>part.Id==p.Id);if(i<0)return;
                project.Parts[i]=project.Parts[i] with{Value=Math.Round(temperature.Value),Setting=(Math.Round(humidity.Value)-20)/70};
                t.Text=$"Температура · {temperature.Value:F0} °C";h.Text=$"Влажность · {humidity.Value:F0} %";
                RecordHistory("control-change");result=CircuitSolver.Solve(project,outputs);Draw();QueueSave();
            });
        }
        temperature.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty)Apply();};
        humidity.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty)Apply();};
        properties.Children.Add(Ui.Muted("Можно менять во время симуляции. Датчик возвращает предыдущее измерение; новое значение появится после следующего цикла чтения.",12));
    }
}
