namespace Classroom.Desktop.Electronics;

// Nine-pin ILI9341 module: regulated supply and level-tolerant inputs;
// its MISO output is 3.3 V. This is not a bare ILI9341 chip or a touch/SD board.
public static class CircuitSpi
{
    public const string ModelId="tft-ili9341-spi";
    public static bool IsDevice(CircuitPart part)=>part.Kind==PartKind.TftSpi;
    public static SpiDisplayDevice[] Devices(CircuitProject project,CircuitResult? result=null)
    {
        var board=project.Parts.SingleOrDefault(CircuitBoards.IsBoard);
        if(board is null||!project.Parts.Any(IsDevice))return [];
        var nets=new CircuitConnections(project);result??=CircuitSolver.Solve(project);
        var ground=new Terminal(board.Id,CircuitBoards.Ground(board));
        var ports=Enumerable.Range(0,CircuitBoards.PinCount(board)).Where(i=>CircuitBoards.Pin(board,i) is not null).ToArray();
        var rails=PartLibrary.Pins(board).Where(pin=>!ports.Any(i=>CircuitBoards.Pin(board,i)==pin)).Select(pin=>new Terminal(board.Id,pin)).ToArray();
        var sources=project.Parts.Where(p=>p.Kind is PartKind.Battery or PartKind.AnalogSensor).SelectMany(p=>new[]{new Terminal(p.Id,"+"),new Terminal(p.Id,"−")}).ToArray();
        int? Pin(Terminal t){var found=ports.Where(i=>nets.Same(t,new(board.Id,CircuitBoards.Pin(board,i)!))).ToArray();return found.Length==1&&!rails.Concat(sources).Any(r=>nets.Same(t,r))?found[0]:null;}
        return project.Parts.Where(IsDevice).Select(part=>{
            Terminal T(string pin)=>new(part.Id,pin);
            SpiSignal? Signal(string name){var t=T(name);if(Pin(t) is {}pin)return new(pin);if(nets.Same(t,ground))return new(Level:false);
                // Only a physical source net is a constant; a floating solver zero is not GND.
                if(nets.Same(t,T("VCC")))return new(Level:true);
                if(rails.Concat(sources).Any(r=>nets.Same(t,r))){
                    var volts=result.Voltages.GetValueOrDefault(t)-result.Voltages.GetValueOrDefault(ground);if(volts is >=0 and <=5.5)return new(Level:volts>1.65);
                }
                return null;
            }
            var mosi=Pin(T("MOSI"));var sck=Pin(T("SCK"));var miso=Pin(T("MISO"));
            var cs=Signal("CS");var dc=Signal("D/C");var rst=Signal("RST");var led=Signal("LED");
            var data=new[]{"MOSI","SCK","MISO"};var critical=new[]{"VCC","GND","CS","D/C","RST","LED"};
            var shorts=data.Any(a=>data.Concat(critical).Any(b=>a!=b&&nets.Same(T(a),T(b))))||nets.Same(T("CS"),T("D/C"));
            var connected=mosi is not null&&sck is not null&&mosi!=sck&&!shorts&&cs is not null&&dc is not null&&rst is not null;
            var volts=result.Voltages.GetValueOrDefault(T("VCC"))-result.Voltages.GetValueOrDefault(T("GND"));
            var powered=nets.Same(T("GND"),ground)&&volts is >=3 and <=5.5;
            var pico=board.Kind==PartKind.Pico;var defaultMosi=pico?19:board.Kind==PartKind.Mega?51:11;var defaultSck=pico?18:board.Kind==PartKind.Mega?52:13;
            // Invalid/shorted nets are disconnected, never malformed worker requests.
            return new SpiDisplayDevice(part.Id,pico&&sck is {}clock?(clock>>3)&1:0,connected?mosi!.Value:defaultMosi,connected?sck!.Value:defaultSck,miso,cs,dc,rst,led,powered,connected);
        }).ToArray();
    }
    public static bool Hardware(CircuitPart board,SpiDisplayDevice device)=>board.Kind==PartKind.Pico
        ?device.Sck%4==2&&device.Mosi%4==3&&((device.Mosi>>3)&1)==device.Controller
        :device.Sck==(board.Kind==PartKind.Mega?52:13)&&device.Mosi==(board.Kind==PartKind.Mega?51:11);
    public static void ValidateRun(CircuitProject project)
    {
        if(project.Parts.Count(IsDevice)>2)throw new InvalidDataException("Одновременно доступны два TFT-дисплея. Остальные можно сохранить в схеме.");
    }
}
