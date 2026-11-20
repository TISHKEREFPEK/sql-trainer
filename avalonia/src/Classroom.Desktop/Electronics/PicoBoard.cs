namespace Classroom.Desktop.Electronics;

// Raspberry Pi Pico RP2040, not Pico W/Pico 2. Header numbering stays explicit;
// internal GPIO23/24/25/29 are deliberately not external terminals.
public static class PicoBoard
{
    public const string ModelId="pico-rp2040",Ground="3:GND",V3="36:3V3",Vbus="40:VBUS",Vsys="39:VSYS";
    public static readonly string[] Pins=["1:GP0","2:GP1","3:GND","4:GP2","5:GP3","6:GP4","7:GP5","8:GND","9:GP6","10:GP7","11:GP8","12:GP9","13:GND","14:GP10","15:GP11","16:GP12","17:GP13","18:GND","19:GP14","20:GP15","21:GP16","22:GP17","23:GND","24:GP18","25:GP19","26:GP20","27:GP21","28:GND","29:GP22","30:RUN","31:GP26/ADC0","32:GP27/ADC1","33:AGND","34:GP28/ADC2","35:ADC_VREF","36:3V3","37:3V3_EN","38:GND","39:VSYS","40:VBUS"];
    static readonly string?[] gpioPins=Enumerable.Range(0,30).Select(i=>Pins.FirstOrDefault(p=>p.Split(':')[1].Split('/')[0]=="GP"+i)).ToArray();
    public static string? Gpio(int number)=>number is >=0 and <30?gpioPins[number]:null;
    public static bool IsGround(string pin)=>pin.EndsWith(":GND",StringComparison.Ordinal)||pin.EndsWith(":AGND",StringComparison.Ordinal);
    public static void ValidateRun(CircuitProject project)
    {
        var board=project.Parts.SingleOrDefault(p=>p.Kind==PartKind.Pico);if(board is null)return;
        if(project.Wires.Any(w=>new[]{w.From,w.To}.Any(t=>t.Part==board.Id&&t.Pin is "30:RUN" or "37:3V3_EN" or "35:ADC_VREF")))
            throw new InvalidDataException("Внешнее управление RUN, 3V3_EN и ADC_VREF Pico ещё не подключено к симулятору. Исходная схема сохранена.");
        var nets=new CircuitConnections(project);
        if(project.Parts.Where(p=>p.Kind is PartKind.Battery or PartKind.AnalogSensor).Any(source=>new[]{"+","−"}.Any(pin=>new[]{Vbus,Vsys,V3}.Any(rail=>nets.Same(new(source.Id,pin),new(board.Id,rail))))))
            throw new InvalidDataException("Внешнее питание Pico через VBUS/VSYS/3V3 пока не моделируется. Сейчас плата питается от виртуального USB; исходная схема сохранена.");
    }
}

public static class CircuitBoards
{
    public static bool IsBoard(CircuitPart part) => IsBoardKind(part.Kind);
    internal static bool IsBoardKind(PartKind kind) => kind is PartKind.Uno or PartKind.Mega or PartKind.Pico or PartKind.Esp32S3;
    public static string Name(CircuitPart part) => part.Kind switch
    {
        PartKind.Pico => "Raspberry Pi Pico",
        PartKind.Esp32S3 => Esp32S3Board.Name,
        _ => AvrBoards.For(part)?.Name ?? throw new InvalidDataException("Неизвестная исполняемая плата.")
    };
    public static string Ground(CircuitPart part) => part.Kind switch
    {
        PartKind.Pico => PicoBoard.Ground,
        PartKind.Esp32S3 => Esp32S3Board.Ground,
        _ => "GND"
    };
    public static string? Pin(CircuitPart part, int number) => part.Kind switch
    {
        PartKind.Pico => PicoBoard.Gpio(number),
        PartKind.Esp32S3 => Esp32S3Board.Gpio(number),
        _ => AvrBoards.For(part)!.PinNames.ElementAtOrDefault(number)
    };
    public static int PinCount(CircuitPart part) => part.Kind switch
    {
        PartKind.Pico => 30,
        PartKind.Esp32S3 => Esp32S3Board.GpioCount,
        _ => AvrBoards.For(part)!.PinNames.Length
    };
    internal static void JoinHeaderAliases(CircuitPart part, Action<Terminal, Terminal> join)
    {
        if (part.Kind == PartKind.Pico)
            foreach (var pin in PicoBoard.Pins.Where(PicoBoard.IsGround)) join(new(part.Id, PicoBoard.Ground), new(part.Id, pin));
        if (part.Kind == PartKind.Esp32S3)
            foreach (var (root, contact) in Esp32S3Board.Aliases) join(new(part.Id, root), new(part.Id, contact));
    }
}
