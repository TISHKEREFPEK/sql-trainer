namespace Classroom.Contracts;

// Bounded diagnostics, not a complete SPI waveform/pixel archive. Times below
// are the simulation clock; ElectronicsChange.At remains the calendar time.
public sealed record ElectronicsSpiIo(string DeviceId,int Controller,bool Software,int Mosi,int Sck,int? Miso,
    long Words,long CompletedWords,long Bytes,long Commands,long DataBytes,long IgnoredWords,long AbortedWords,
    int Mode,int Bits,double ClockHz,bool LsbFirst,long ParameterChanges,double StartedMs,double EndedMs,
    int[] TxPrefix,int[] RxPrefix,int[] CommandPrefix,uint TxCrc32,uint RxCrc32,string[] Issues);

internal static class ElectronicsSpiValidation
{
    public static void Validate(string json,string kind,ElectronicsSpiIo[]? data)
    {
        if(data is null)return;
        if(kind!="spi-transfer"||data.Length is <1 or >2||data.Any(t=>t is null))throw new InvalidDataException("Некорректная сводка SPI в истории.");
        using var doc=System.Text.Json.JsonDocument.Parse(json);
        var parts=doc.RootElement.GetProperty("parts").EnumerateArray().ToArray();
        var board=parts.Where(p=>p.GetProperty("kind").GetString() is "Uno" or "Mega" or "Pico").ToArray();
        if(board.Length!=1)throw new InvalidDataException("Сводка SPI требует одну исполняемую плату.");
        var boardKind=board[0].GetProperty("kind").GetString();var limit=boardKind=="Pico"?30:boardKind=="Mega"?70:20;
        bool Prefix(int[]? values,int length)=>values is not null&&values.Length==length&&values.All(b=>b is >=0 and <=255);
        foreach(var t in data){
            if(string.IsNullOrEmpty(t.DeviceId)||t.DeviceId.Length>64||!parts.Any(p=>p.GetProperty("id").GetString()==t.DeviceId&&p.GetProperty("kind").GetString()=="TftSpi")||
                t.Controller<0||t.Controller>(boardKind=="Pico"?1:0)||t.Mosi<0||t.Mosi>=limit||t.Sck<0||t.Sck>=limit||t.Mosi==t.Sck||t.Miso is {}miso&&(miso<0||miso>=limit)||
                t.Mode is <0 or >3||t.Bits is <4 or >16||!double.IsFinite(t.ClockHz)||t.ClockHz<=0||t.ClockHz>1_000_000_000||
                !double.IsFinite(t.StartedMs)||!double.IsFinite(t.EndedMs)||t.StartedMs<0||t.EndedMs<t.StartedMs||t.EndedMs>9_000_000_000_000||
                new[]{t.Words,t.CompletedWords,t.Bytes,t.Commands,t.DataBytes,t.IgnoredWords,t.AbortedWords,t.ParameterChanges}.Any(n=>n is <0 or >1_000_000)||t.Words<1||
                t.Words!=t.CompletedWords+t.IgnoredWords+t.AbortedWords||t.Bytes<t.CompletedWords||t.Bytes>t.CompletedWords*2||t.Commands+t.DataBytes!=t.Bytes||t.ParameterChanges>=t.Words||
                !Prefix(t.TxPrefix,(int)Math.Min(t.Bytes,32))||!Prefix(t.RxPrefix,(int)Math.Min(t.Bytes,32))||!Prefix(t.CommandPrefix,(int)Math.Min(t.Commands,16))||
                t.Issues is null||t.Issues.Length>8||t.Issues.Distinct().Count()!=t.Issues.Length||t.Issues.Any(i=>i is not ("wiring" or "power" or "reset" or "not-selected" or "interrupted" or "no-miso")))
                throw new InvalidDataException("Повреждены счётчики, выводы или фрагмент SPI в истории.");
        }
        if(data.Select(t=>t.DeviceId).Distinct().Count()!=data.Length)throw new InvalidDataException("Повтор устройства в сводке SPI.");
    }
}
