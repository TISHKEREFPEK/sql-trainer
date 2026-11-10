using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public static class SpiDiagnostics
{
    static string Hex(int[] bytes)=>string.Join(" ",bytes.Select(b=>b.ToString("X2")));
    static string Issue(string value)=>value switch{
        "wiring"=>"неподходящая или оборванная проводка","power"=>"нет питания","reset"=>"дисплей удерживается в сбросе",
        "not-selected"=>"CS не выбран","interrupted"=>"незавершённая передача","no-miso"=>"MISO не подключён к приёмнику",_=>value};
    public static string Describe(IEnumerable<ElectronicsSpiIo> entries)=>string.Join("\n\n",entries.Select(t=>
        $"TFT ILI9341 · {(t.Software?"программный SPI":$"SPI{t.Controller}")} · MOSI {t.Mosi}, SCK {t.Sck}"+(t.Miso is {}rx?$", MISO {rx}":", без MISO")+
        $"\nСводка с фрагментом данных — не полный поток пикселей.\nПолучено байтов: {t.Bytes} (команд {t.Commands}, данных {t.DataBytes}). Слов: {t.CompletedWords}; без выбора/связи: {t.IgnoredWords}; прервано: {t.AbortedWords}."+
        $"\nНачальные параметры: mode {t.Mode}, {t.Bits} бит, {t.ClockHz/1e6:F3} МГц, {(t.LsbFirst?"LSB first":"MSB first")}. Слов с другими параметрами: {t.ParameterChanges}."+
        $"\nВремя модели: {t.StartedMs:F3}–{t.EndedMs:F3} мс. Календарное время — в записи истории."+
        $"\nНачало команд (hex, {t.CommandPrefix.Length} из {t.Commands}): {Hex(t.CommandPrefix)}"+
        $"\nНачало TX (hex, {t.TxPrefix.Length} из {t.Bytes}): {Hex(t.TxPrefix)}\nНачало RX (hex, {t.RxPrefix.Length} из {t.Bytes}): {Hex(t.RxPrefix)}"+
        $"\nCRC32 полного принятого TX/RX: {t.TxCrc32:X8} / {t.RxCrc32:X8}. Это сводка с фрагментом данных, не полный поток пикселей."+
        (t.Issues.Length==0?"":"\nНаблюдения: "+string.Join("; ",t.Issues.Select(Issue)))));
}
