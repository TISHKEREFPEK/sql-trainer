namespace Classroom.Desktop.Electronics;

public sealed record Hcsr04Echo(int Pin,bool Low,bool High);
public sealed record Hcsr04Device(string Id,int Trig,Hcsr04Echo[] Echoes,bool Powered,bool Connected,double Distance,bool Target);
public static class CircuitHcsr04
{
    public const string ModelId="hcsr04-5v";
    public static Hcsr04Device[] Devices(CircuitProject project,CircuitResult? result=null)
    {
        var parts=project.Parts.Where(p=>p.Kind==PartKind.Hcsr04).ToArray();if(parts.Length==0)return [];
        var board=project.Parts.SingleOrDefault(CircuitBoards.IsBoard);if(board is null)return [];
        var nets=new CircuitConnections(project);result??=CircuitSolver.Solve(project);var baseline=CircuitSolver.Solve(project);
        var ground=new Terminal(board.Id,CircuitBoards.Ground(board));
        var ports=Enumerable.Range(0,CircuitBoards.PinCount(board)).Where(i=>CircuitBoards.Pin(board,i) is not null).ToArray();
        var rails=PartLibrary.Pins(board).Where(p=>!ports.Any(i=>CircuitBoards.Pin(board,i)==p)).Select(p=>new Terminal(board.Id,p));
        var sources=project.Parts.Where(p=>p.Kind is PartKind.Battery or PartKind.AnalogSensor).SelectMany(p=>new[]{new Terminal(p.Id,"+"),new Terminal(p.Id,"−")});
        var threshold=board.Kind==PartKind.Pico?1.65:2.5;var maximum=board.Kind==PartKind.Pico?3.45:5.5;
        return parts.Select(p=>{
            Terminal T(string pin)=>new(p.Id,pin);
            var trigs=ports.Where(i=>nets.Same(T("TRIG"),new(board.Id,CircuitBoards.Pin(board,i)!))).ToArray();
            var volts=result.Voltages.GetValueOrDefault(T("VCC"))-result.Voltages.GetValueOrDefault(T("GND"));
            var powered=nets.Same(T("GND"),ground)&&volts is >=4.5 and <=5.5;
            // Solve both output states through actual resistors/breadboard strips.
            // A direct GPIO match would incorrectly reject a safe Pico divider.
            var high=CircuitSolver.Solve(project,signals:new Dictionary<Terminal,double>{{T("ECHO"),powered?volts:5}});
            var echoes=new List<Hcsr04Echo>();
            foreach(var i in ports){
                var t=new Terminal(board.Id,CircuitBoards.Pin(board,i)!);var low=baseline.Voltages.GetValueOrDefault(t);var hi=high.Voltages.GetValueOrDefault(t);
                if(Math.Abs(hi-low)<.1)continue;
                if(powered&&(hi>maximum||low>maximum||hi<-.1||low<-.1))throw new InvalidDataException($"HC-SR04 → {CircuitBoards.Pin(board,i)}: Echo до {hi:F2} В. "+(board.Kind==PartKind.Pico?"Для GPIO 3,3 В нужен делитель напряжения или согласование уровней.":"Проверьте напряжение и соединения Echo."));
                echoes.Add(new(i,low>threshold,hi>threshold));
            }
            var connected=trigs.Length==1&&echoes.Count>0&&!echoes.Any(e=>e.Pin==trigs[0])&&!rails.Concat(sources).Append(T("ECHO")).Any(t=>nets.Same(T("TRIG"),t));
            return new Hcsr04Device(p.Id,connected?trigs[0]:0,connected?echoes.ToArray():[],powered,connected,p.Value,p.Active);
        }).ToArray();
    }
    public static void ValidateRun(CircuitProject project)
    {
        var devices=Devices(project);if(devices.Length>4)throw new InvalidDataException("Одновременно доступны четыре HC-SR04. Остальные можно сохранить в схеме.");
        var pins=devices.Where(d=>d.Connected).SelectMany(d=>d.Echoes.Select(e=>e.Pin)).ToArray();
        if(pins.Distinct().Count()!=pins.Length||devices.Any(d=>d.Connected&&pins.Contains(d.Trig)))throw new InvalidDataException("HC-SR04: конфликт GPIO Trig/Echo между датчиками.");
    }
}
