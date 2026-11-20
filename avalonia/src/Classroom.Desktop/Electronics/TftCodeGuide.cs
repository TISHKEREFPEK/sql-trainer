namespace Classroom.Desktop.Electronics;

internal sealed record TftGuide(bool Ready,string[] Notes,string[] Declarations,string[] Setup,int[] Pins);
internal static class TftCodeGuide
{
    public const string Includes="#include <SPI.h>\n#include <Adafruit_GFX.h>\n#include <Adafruit_ILI9341.h>\n";
    public static TftGuide Build(CircuitProject project,CircuitPart board)
    {
        var notes=new List<string>();var globals=new List<string>();var setup=new List<string>();var pins=new HashSet<int>();var ready=true;
        var buses=new Dictionary<string,(int Mosi,int Sck,int? Miso)>();var roles=new Dictionary<int,string>();var methods=new Dictionary<int,bool>();var resetOwners=new HashSet<int>();
        var devices=CircuitSpi.Devices(project);
        if(devices.Length>2){notes.Add("В симуляции доступны два TFT. Остальные можно сохранить в схеме.");ready=false;}
        // Select neither panel while the first library object is initialising.
        foreach(var cs in devices.Select(d=>d.Cs?.Pin).OfType<int>().Distinct())setup.Add($"pinMode({cs}, OUTPUT); digitalWrite({cs}, HIGH);");
        foreach(var d in devices){
            notes.Add("TFT ILI9341: VCC 3–5,5 В, общая GND; LED — подсветка. Модуль имеет согласование входных уровней, MISO — 3,3 В. Adafruit ILI9341/GFX установлены локально. Touch и SD у этой девятиконтактной модели отсутствуют.");
            if(!d.Powered||!d.Connected||d.Dc?.Pin is null||d.Cs?.Level==true||d.Rst?.Level==false||d.Led is null||d.Led.Level==false){
                notes.Add("Для шаблона TFT проверьте питание, CS/D/C/RST/LED и отдельные GPIO MOSI/SCK; линии данных не должны замыкаться друг с другом или питанием. MISO нужен для чтения регистра, но не для рисования.");ready=false;continue;
            }
            var hardware=CircuitSpi.Hardware(board,d);var key=hardware?"hw:"+d.Controller:$"gpio:{d.Mosi}:{d.Sck}:{d.Miso}";
            var pair=(d.Mosi,d.Sck,d.Miso);var busPins=new[]{d.Mosi,d.Sck}.Concat(d.Miso is {}rx?[rx]:Array.Empty<int>()).ToArray();
            var ownControls=new[]{d.Cs?.Pin,d.Dc?.Pin,d.Rst?.Pin,d.Led?.Pin}.Where(p=>p is not null).Select(p=>p!.Value).ToArray();
            var requested=new List<(int Pin,string Role)>{(d.Mosi,"MOSI"),(d.Sck,"SCK")};if(d.Miso is {}inputPin)requested.Add((inputPin,"MISO"));
            foreach(var (signal,role) in new[]{(d.Cs,"CS"),(d.Dc,"DC"),(d.Rst,"RST"),(d.Led,"LED")})if(signal?.Pin is {}pin)requested.Add((pin,role));
            if(buses.TryGetValue(key,out var existing)&&existing!=pair||ownControls.Distinct().Count()!=ownControls.Length||ownControls.Any(busPins.Contains)||
                requested.Any(r=>roles.TryGetValue(r.Pin,out var previous)&&(previous!=r.Role||r.Role=="CS"))||busPins.Any(p=>methods.TryGetValue(p,out var method)&&method!=hardware)){
                notes.Add("TFT: для независимой работы нужны отдельные CS. D/C, RST и подсветку можно объединять только с одноимёнными сигналами; разные способы SPI на общих data GPIO настройте вручную.");ready=false;continue;
            }
            var spi=d.Controller==0?"SPI":"SPI1";
            if(!buses.ContainsKey(key)){
                buses[key]=pair;
                if(hardware&&board.Kind==PartKind.Pico)setup.Add($"{spi}.setSCK({d.Sck}); {spi}.setTX({d.Mosi});"+(d.Miso is {}miso?$" {spi}.setRX({miso});":""));
            }
            foreach(var p in busPins){pins.Add(p);methods[p]=hardware;}foreach(var p in ownControls)pins.Add(p);foreach(var r in requested)roles[r.Pin]=r.Role;
            var name="tft"+globals.Count;
            var reset=d.Rst?.Pin??-1;if(reset>=0&&!resetOwners.Add(reset)){reset=-1;notes.Add("Общий RST: начальный сброс выполняет первый дисплей, остальные сбрасываются программной командой только при своём CS.");}
            globals.Add(hardware?$"Adafruit_ILI9341 {name}(&{spi}, {d.Dc!.Pin}, {d.Cs?.Pin??-1}, {reset});":$"Adafruit_ILI9341 {name}({d.Cs?.Pin??-1}, {d.Dc!.Pin}, {d.Mosi}, {d.Sck}, {reset}, {d.Miso??-1});");
            if(d.Led!.Pin is {}led)setup.Add($"pinMode({led}, OUTPUT); digitalWrite({led}, HIGH);");
            setup.Add($"{name}.begin(4000000); {name}.setRotation(0);");
            setup.Add($"{name}.fillRect(0,0,48,32,ILI9341_BLUE); {name}.fillRect(8,8,16,12,ILI9341_RED);");
            setup.Add($"{name}.setTextColor(ILI9341_WHITE); {name}.setTextSize(1); {name}.setCursor(4,40); {name}.print(\"TFT ready\");");
            notes.Add($"TFT: {(hardware?$"SPI{d.Controller}":"программный SPI")}, MOSI {d.Mosi}, SCK {d.Sck}"+(d.Miso is {}input?$", MISO {input}":", MISO не подключён")+$"; CS {d.Cs?.Pin?.ToString()??"GND"}, D/C {d.Dc.Pin}, RST {d.Rst?.Pin?.ToString()??"VCC"}. "+(hardware?"Частота шаблона — 4 МГц.":"Скорость определяется переключениями GPIO в программе."));
        }
        return new(ready,notes.ToArray(),globals.ToArray(),setup.ToArray(),pins.ToArray());
    }
}
