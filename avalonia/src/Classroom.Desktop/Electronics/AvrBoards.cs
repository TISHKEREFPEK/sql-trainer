using System.Text.Json;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed record AvrBoard(string Id,string Name,string Fqbn,int FlashBytes,int RamStart,int RamBytes,int EepromBytes,int ClockHz,int DigitalCount,int AnalogCount,string[] PinMap)
{
    public string[] PinNames => Enumerable.Range(0,DigitalCount).Select(i=>"D"+i).Concat(Enumerable.Range(0,AnalogCount).Select(i=>"A"+i)).ToArray();
}
public static class AvrBoards
{
    static readonly AvrBoard[] boards = Load();
    static AvrBoard[] Load()
    {
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"electronics","avr-boards.json")));
        if(json.RootElement.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("Неизвестная версия профилей AVR.");
        return Wire.Read<AvrBoard[]>(json.RootElement.GetProperty("boards").GetRawText());
    }
    public static AvrBoard Get(string id)=>boards.SingleOrDefault(b=>b.Id==id)??throw new InvalidDataException("Неизвестная AVR-плата: "+id);
}
