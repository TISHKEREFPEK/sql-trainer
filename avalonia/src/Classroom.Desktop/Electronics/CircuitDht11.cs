namespace Classroom.Desktop.Electronics;

public sealed record Dht11Device(string Id,int Pin,bool Powered,bool Connected,int Temperature,int Humidity,bool Pullup);
public static class CircuitDht11
{
    public const string ModelId="dht11-single-wire";
    public static int Humidity(CircuitPart p)=>(int)Math.Round(20+70*p.Setting,MidpointRounding.AwayFromZero);
    public static Dht11Device[] Devices(CircuitProject project,CircuitResult? result=null)
    {
        var board=project.Parts.SingleOrDefault(CircuitBoards.IsBoard);if(board is null)return [];
        var nets=new CircuitConnections(project);result??=CircuitSolver.Solve(project);
        var ground=new Terminal(board.Id,CircuitBoards.Ground(board));
        var ports=Enumerable.Range(0,CircuitBoards.PinCount(board)).Where(i=>CircuitBoards.Pin(board,i) is not null).ToArray();
        var rails=PartLibrary.Pins(board).Where(p=>!ports.Any(i=>CircuitBoards.Pin(board,i)==p)).Select(p=>new Terminal(board.Id,p));
        var sources=project.Parts.Where(p=>p.Kind is PartKind.Battery or PartKind.AnalogSensor).SelectMany(p=>new[]{new Terminal(p.Id,"+"),new Terminal(p.Id,"−")});
        return project.Parts.Where(p=>p.Kind==PartKind.Dht11).Select(p=>{
            Terminal T(string pin)=>new(p.Id,pin);var data=T("DATA");
            var pins=ports.Where(i=>nets.Same(data,new(board.Id,CircuitBoards.Pin(board,i)!))).ToArray();
            var connected=pins.Length==1&&!rails.Concat(sources).Concat([T("VDD"),T("GND"),T("NC")]).Any(t=>nets.Same(data,t));
            var volts=result.Voltages.GetValueOrDefault(T("VDD"))-result.Voltages.GetValueOrDefault(T("GND"));
            var powered=nets.Same(T("GND"),ground)&&volts is >=3 and <=5.5;
            var pullup=project.Parts.Where(r=>r.Kind==PartKind.Resistor&&r.Value is >=1000 and <=100000).Any(r=>{
                var a=new Terminal(r.Id,"1");var b=new Terminal(r.Id,"2");var supply=nets.Same(data,a)?b:nets.Same(data,b)?a:null;
                if(supply is null)return false;
                var v=result.Voltages.GetValueOrDefault(supply)-result.Voltages.GetValueOrDefault(ground);
                return rails.Concat(sources).Append(T("VDD")).Any(t=>nets.Same(supply,t))&&v is >=3 and <=5.5;
            });
            return new Dht11Device(p.Id,connected?pins[0]:0,powered,connected,(int)p.Value,Humidity(p),pullup);
        }).ToArray();
    }
    public static void ValidateRun(CircuitProject project)
    {
        var devices=Devices(project);if(devices.Length>8)throw new InvalidDataException("Одновременно доступны восемь DHT11. Остальные можно сохранить в схеме.");
        if(devices.Where(d=>d.Connected).GroupBy(d=>d.Pin).Any(g=>g.Count()>1))throw new InvalidDataException("DHT11: каждому датчику нужен отдельный вывод DATA.");
    }
}
