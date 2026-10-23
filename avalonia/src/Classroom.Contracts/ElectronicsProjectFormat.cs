using System.Text.Json;
using System.Text.RegularExpressions;
namespace Classroom.Contracts;

// Validates bounded, non-executable project data on upload/restore. The desktop
// additionally validates device models and actual terminal names before rendering.
public static class ElectronicsProjectFormat
{
    public static readonly string[] ReportKinds=["submitted","compiled","compile-error","started","run-error"];
    public static (string Name,int Parts,int Wires) Inspect(string json)
    {
        if(json.Length>2_000_000) throw new InvalidDataException("Схема превышает 2 МБ.");
        try
        {
            using var doc=JsonDocument.Parse(json,new JsonDocumentOptions { MaxDepth=32 }); var root=doc.RootElement;
            string Text(JsonElement value,int max,bool empty=false) { if(value.ValueKind!=JsonValueKind.String) throw new InvalidDataException("Некорректный текст схемы."); var text=value.GetString()!; if(text.Length>max||!empty&&string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Некорректный размер поля схемы."); return text; }
            void Number(JsonElement value,double min,double max) { if(!value.TryGetDouble(out var n)||!double.IsFinite(n)||n<min||n>max) throw new InvalidDataException("Параметр схемы вне допустимого диапазона."); }
            if(root.GetProperty("version").GetInt32()!=1) throw new InvalidDataException("Версия схемы не поддерживается.");
            var name=Text(root.GetProperty("name"),120,true); Text(root.GetProperty("code"),100000,true);
            if(root.TryGetProperty("favorites",out var favorites)) { if(favorites.ValueKind!=JsonValueKind.Array||favorites.GetArrayLength()>256) throw new InvalidDataException("Некорректное избранное."); foreach(var item in favorites.EnumerateArray()) Text(item,100); }
            var parts=root.GetProperty("parts"); var wires=root.GetProperty("wires");
            if(parts.ValueKind!=JsonValueKind.Array||wires.ValueKind!=JsonValueKind.Array||parts.GetArrayLength()>80||wires.GetArrayLength()>200) throw new InvalidDataException("Превышен размер схемы.");
            var ids=new HashSet<string>();
            foreach(var p in parts.EnumerateArray())
            {
                if(!ids.Add(Text(p.GetProperty("id"),64))) throw new InvalidDataException("Повтор компонента.");
                var kind=Text(p.GetProperty("kind"),32);
                if(!new[]{"Uno","Resistor","Led","Switch","Battery","Ground","Potentiometer","AnalogSensor","Breadboard","Diode","Motor","Buzzer","Servo","Pot3","Visual"}.Contains(kind)) throw new InvalidDataException("Неизвестный тип компонента.");
                Number(p.GetProperty("x"),0,4000); Number(p.GetProperty("y"),0,3000);
                if(p.TryGetProperty("value",out var value)) Number(value,-1_000_000,1_000_000);
                if(p.TryGetProperty("scale",out var scale)) Number(scale,0.4,2);
                if(p.TryGetProperty("angle",out var angleDegrees)) { Number(angleDegrees,0,360); if(angleDegrees.GetDouble()>=360) throw new InvalidDataException("Некорректный угол детали."); }
                foreach(var axis in new[]{"stretchX","stretchY"}) if(p.TryGetProperty(axis,out var stretch)) Number(stretch,0.1,10);
                foreach(var key in new[]{"rotation","quarterTurns"}) if(p.TryGetProperty(key,out var angle) && (!angle.TryGetInt32(out var n)||n<0||n>3)) throw new InvalidDataException("Некорректный поворот.");
                if(p.TryGetProperty("setting",out var setting)) Number(setting,0,1);
                if(p.TryGetProperty("active",out var active)&&active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Некорректное состояние детали.");
                if(p.TryGetProperty("modelId",out var model)&&model.ValueKind!=JsonValueKind.Null) Text(model,100);
                if(p.TryGetProperty("customPins",out var pins)&&pins.ValueKind!=JsonValueKind.Null)
                { if(pins.ValueKind!=JsonValueKind.Array||pins.GetArrayLength()>64||pins.EnumerateArray().Select(v=>Text(v,20)).Distinct().Count()!=pins.GetArrayLength()) throw new InvalidDataException("Некорректные выводы."); }
            }
            var wireIds=new HashSet<string>();
            foreach(var w in wires.EnumerateArray())
            {
                if(!wireIds.Add(Text(w.GetProperty("id"),64))) throw new InvalidDataException("Повтор провода.");
                foreach(var key in new[]{"from","to"}) { var t=w.GetProperty(key); if(!ids.Contains(Text(t.GetProperty("part"),64))) throw new InvalidDataException("Отсутствует компонент провода."); Text(t.GetProperty("pin"),20); }
                if(w.TryGetProperty("color",out var color)&&!Regex.IsMatch(Text(color,7),"^#[0-9a-fA-F]{6}$")) throw new InvalidDataException("Некорректный цвет провода.");
            }
            return(name,parts.GetArrayLength(),wires.GetArrayLength());
        }
        catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Некорректный файл электронной схемы.",e); }
    }
}
