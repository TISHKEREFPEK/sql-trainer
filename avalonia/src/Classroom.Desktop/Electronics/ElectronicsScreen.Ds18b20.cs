using Avalonia.Controls;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    readonly Dictionary<string,Ds18b20Frame> ds18Readings=[];
    TextBlock? ds18Reading;
    void UpdateDs18b20Reading()
    {
        if(ds18Reading is null)return;
        ds18Reading.Text=selected is {}id&&ds18Readings.TryGetValue(id,out var f)?$"Показание: {f.Reading:0.####} °C · {f.Resolution} бит"+(f.Busy?" · преобразование":""):"Показание появится после запуска; начальное значение — 85 °C.";
    }
    void ReceiveDs18b20(Ds18b20Frame[]? frames,OneWireMemoryWrite[]? writes)
    {
        if(frames?.Length>8||writes?.Length>8)throw new InvalidDataException("DS18B20: превышен размер состояния.");
        foreach(var w in writes??[])if(!CircuitDs18b20.ValidMemory(w.Memory)||!project.Parts.Any(p=>p.Id==w.Id&&p.Kind==PartKind.Ds18b20))throw new InvalidDataException("DS18B20: некорректная запись памяти.");
        foreach(var w in writes??[]){var i=project.Parts.FindIndex(p=>p.Id==w.Id);project.Parts[i]=project.Parts[i] with{DeviceMemory=w.Memory.ToArray()};RecordHistory("device-memory-write",true);QueueSave();}
        ds18Readings.Clear();foreach(var f in frames??[])ds18Readings[f.Id]=f;UpdateDs18b20Reading();
    }
    void InspectDs18b20(CircuitPart p)
    {
        properties.Children.Add(Ui.Text("DS18B20: GND, DQ, VDD 3–5,5 В. Нужна подтяжка DQ к питанию (обычно 4,7 кОм); для Pico — к 3,3 В. Несколько датчиков могут работать на одной шине. При питании через DQ соедините VDD с GND и обеспечьте сильную подтяжку на время преобразования.",12));
        properties.Children.Add(Ui.Muted("ROM: "+CircuitDs18b20.Rom(p),11));ds18Reading=Ui.Text("",12);properties.Children.Add(ds18Reading);UpdateDs18b20Reading();
        AddInspectorSlider(p, dsTemperature);
        properties.Children.Add(Ui.Muted("Изменение среды не переписывает показание сразу: программа должна запустить и дождаться преобразования. Настройки, записанные в EEPROM кодом, сохраняются вместе со схемой.",12));
    }
}
