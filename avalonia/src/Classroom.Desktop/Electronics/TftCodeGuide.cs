namespace Classroom.Desktop.Electronics;

internal sealed record TftGuide(bool Ready,string[] Notes,string[] Declarations,string[] Setup,int[] Pins);
internal static class TftCodeGuide
{
    public const string Includes="#include <SPI.h>\n#include <Adafruit_GFX.h>\n#include <Adafruit_ILI9341.h>\n";
    public static TftGuide Build(CircuitProject project,CircuitPart board)
    {
        var notes=new List<string>();var globals=new List<string>();var setup=new List<string>();var pins=new HashSet<int>();var ready=true;
        var buses=new Dictionary<string,(int Mosi,int Sck,int? Miso)>();var controls=new HashSet<int>();
        var devices=CircuitSpi.Devices(project);
        if(devices.Length>2){notes.Add("В симуляции доступны два TFT. Остальные можно сохранить в схеме.");ready=false;}
        foreach(var d in devices){
            notes.Add("TFT ILI9341: VCC 3–5,5 В, общая GND; LED — подсветка. Модуль имеет согласование входных уровней, MISO — 3,3 В. Adafruit ILI9341/GFX установлены локально. Touch и SD у этой девятиконтактной модели отсутствуют.");
            if(!d.Powered||!d.Connected||d.Dc?.Pin is null||d.Cs?.Level==true||d.Rst?.Level==false||d.Led is null||d.Led.Level==false){
                notes.Add("Для шаблона TFT проверьте питание, CS/D/C/RST/LED и отдельные GPIO MOSI/SCK; линии данных не должны замыкаться друг с другом или питанием. MISO нужен для чтения регистра, но не для рисования.");ready=false;continue;
            }
            var hardware=CircuitSpi.Hardware(board,d);var key=hardware?"hw:"+d.Controller:$"gpio:{d.Mosi}:{d.Sck}:{d.Miso}";
            var pair=(d.Mosi,d.Sck,d.Miso);var busPins=new[]{d.Mosi,d.Sck}.Concat(d.Miso is {}rx?[rx]:Array.Empty<int>()).ToArray();
            var ownControls=new[]{d.Cs?.Pin,d.Dc?.Pin,d.Rst?.Pin,d.Led?.Pin}.Where(p=>p is not null).Select(p=>p!.Value).ToArray();
            if(buses.TryGetValue(key,out var existing)&&existing!=pair||busPins.Any(controls.Contains)||ownControls.Distinct().Count()!=ownControls.Length||ownControls.Any(p=>pins.Contains(p)||busPins.Contains(p))){notes.Add("TFT: сигналы управления заняты или аппаратные выводы одной SPI-шины различаются; настройте совместное использование вручную.");ready=false;continue;}
            var spi=d.Controller==0?"SPI":"SPI1";
            if(!buses.ContainsKey(key)){
                buses[key]=pair;
                if(hardware&&board.Kind==PartKind.Pico)setup.Add($"{spi}.setSCK({d.Sck}); {spi}.setTX({d.Mosi});"+(d.Miso is {}miso?$" {spi}.setRX({miso});":""));
            }
            foreach(var p in busPins)pins.Add(p);foreach(var p in ownControls){controls.Add(p);pins.Add(p);}
            var name="tft"+globals.Count;
            globals.Add(hardware?$"Adafruit_ILI9341 {name}(&{spi}, {d.Dc!.Pin}, {d.Cs?.Pin??-1}, {d.Rst?.Pin??-1});":$"Adafruit_ILI9341 {name}({d.Cs?.Pin??-1}, {d.Dc!.Pin}, {d.Mosi}, {d.Sck}, {d.Rst?.Pin??-1}, {d.Miso??-1});");
            if(d.Led!.Pin is {}led)setup.Add($"pinMode({led}, OUTPUT); digitalWrite({led}, HIGH);");
            setup.Add($"{name}.begin(4000000); {name}.setRotation(0);");
            setup.Add($"{name}.fillRect(0,0,48,32,ILI9341_BLUE); {name}.fillRect(8,8,16,12,ILI9341_RED);");
            setup.Add($"{name}.setTextColor(ILI9341_WHITE); {name}.setTextSize(1); {name}.setCursor(4,40); {name}.print(\"TFT ready\");");
            notes.Add($"TFT: {(hardware?$"SPI{d.Controller}":"программный SPI")}, MOSI {d.Mosi}, SCK {d.Sck}"+(d.Miso is {}input?$", MISO {input}":", MISO не подключён")+$"; CS {d.Cs?.Pin?.ToString()??"GND"}, D/C {d.Dc.Pin}, RST {d.Rst?.Pin?.ToString()??"VCC"}. "+(hardware?"Частота шаблона — 4 МГц.":"Скорость определяется переключениями GPIO в программе."));
        }
        return new(ready,notes.ToArray(),globals.ToArray(),setup.ToArray(),pins.ToArray());
    }
}
