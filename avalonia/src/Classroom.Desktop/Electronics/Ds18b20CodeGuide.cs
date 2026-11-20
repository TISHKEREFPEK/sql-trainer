namespace Classroom.Desktop.Electronics;

public sealed record Ds18b20Guide(bool Ready,string[] Notes,int[] Pins,string[] Declarations,string[] Setup,string[] Loop);
public static class Ds18b20CodeGuide
{
    public const string Includes="#include <OneWire.h>\n#include <DallasTemperature.h>\n";
    public static Ds18b20Guide Build(CircuitProject project,CircuitPart board)
    {
        var devices=CircuitDs18b20.Devices(project);if(devices.Length==0)return new(true,[],[],[],[],[]);
        var notes=new List<string>{"DS18B20: OneWire и DallasTemperature доступны автономно. Датчики одной шины имеют разные ROM; до первого преобразования показание 85 °C. Питание через DQ требует сильного HIGH; библиотека управляет им при ожидании результата."};var ready=true;
        var globals=new List<string>();var setup=new List<string>();var loop=new List<string>();var pins=new List<int>();var index=0;var pico=board.Kind==PartKind.Pico;var profile=pico?null:AvrBoards.For(board);
        foreach(var group in devices.GroupBy(d=>d.Pin)){
            if(group.Any(d=>!d.Connected||!d.Powered||!d.Pullup)){ready=false;notes.Add("DS18B20: исправьте питание, DQ и внешнюю подтяжку; для Pico используйте 3,3 В.");continue;}
            var pin=group.Key;var number=pico||pin<profile!.DigitalCount?pin.ToString():"A"+(pin-profile.DigitalCount);var name="thermometers"+index;var bus="oneWire"+index++;
            pins.Add(pin);globals.Add($"OneWire {bus}({number});\nDallasTemperature {name}(&{bus});");setup.Add($"{name}.begin();");
            loop.Add($"{name}.requestTemperatures(); for(uint8_t i=0;i<{name}.getDeviceCount();i++){{ Serial.print(\"DS18B20[\"); Serial.print(i); Serial.print(\"] C=\"); Serial.println({name}.getTempCByIndex(i),4); }}");
        }
        return new(ready,notes.ToArray(),pins.ToArray(),globals.ToArray(),setup.ToArray(),loop.ToArray());
    }
}
