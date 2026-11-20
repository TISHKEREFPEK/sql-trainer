using System.Security.Cryptography;
using System.Text;

namespace Classroom.Desktop.Electronics;

public sealed record Ds18b20Device(string Id,int Pin,bool Powered,bool Connected,double Temperature,bool Parasite,bool Pullup,bool StrongPullup,string Rom,int[] Memory);
public sealed record Ds18b20Frame(string Id,string Rom,double Reading,int Resolution,bool Busy,int[] Memory);
public sealed record OneWireMemoryWrite(string Id,int[] Memory,double Milliseconds);
public static class CircuitDs18b20
{
    public const string ModelId="ds18b20-to92";
    public static int[] Memory(CircuitPart p)=>p.DeviceMemory??[75,70,127];
    public static bool ValidMemory(int[] m)=>m.Length==3&&m.All(b=>b is >=0 and <=255)&&(m[2]&0x9f)==0x1f;
    public static string Rom(CircuitPart p)
    {
        var bytes=new byte[8];bytes[0]=0x28;SHA256.HashData(Encoding.UTF8.GetBytes("SQL-Classroom/DS18B20/"+p.Id)).AsSpan(0,6).CopyTo(bytes.AsSpan(1));
        byte crc=0;foreach(var b in bytes.AsSpan(0,7)){var value=b;for(var bit=0;bit<8;bit++){var mix=(crc^value)&1;crc>>=1;if(mix!=0)crc^=0x8c;value>>=1;}}bytes[7]=crc;
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    public static Ds18b20Device[] Devices(CircuitProject project,CircuitResult? result=null)
    {
        if(!project.Parts.Any(p=>p.Kind==PartKind.Ds18b20))return [];
        var board=project.Parts.SingleOrDefault(CircuitBoards.IsBoard);if(board is null)return [];
        var nets=new CircuitConnections(project);result??=CircuitSolver.Solve(project);var ground=new Terminal(board.Id,CircuitBoards.Ground(board));
        var ports=Enumerable.Range(0,CircuitBoards.PinCount(board)).Where(i=>CircuitBoards.Pin(board,i) is not null).ToArray();
        var rails=PartLibrary.Pins(board).Where(p=>!ports.Any(i=>CircuitBoards.Pin(board,i)==p)).Select(p=>new Terminal(board.Id,p));
        var sources=project.Parts.Where(p=>p.Kind is PartKind.Battery or PartKind.AnalogSensor).SelectMany(p=>new[]{new Terminal(p.Id,"+"),new Terminal(p.Id,"−")});
        return project.Parts.Where(p=>p.Kind==PartKind.Ds18b20).Select(p=>{
            Terminal T(string pin)=>new(p.Id,pin);var dq=T("DQ");var pins=ports.Where(i=>nets.Same(dq,new(board.Id,CircuitBoards.Pin(board,i)!))).ToArray();
            var connected=pins.Length==1&&!rails.Concat(sources).Concat([T("VDD"),T("GND")]).Any(t=>nets.Same(dq,t));
            var parasite=nets.Same(T("VDD"),ground);var supply=parasite?new Terminal(board.Id,board.Kind==PartKind.Pico?PicoBoard.V3:"5V"):T("VDD");
            var volts=result.Voltages.GetValueOrDefault(supply)-result.Voltages.GetValueOrDefault(T("GND"));var powered=nets.Same(T("GND"),ground)&&volts is >=3 and <=5.5;
            var pullup=project.Parts.Where(r=>r.Kind==PartKind.Resistor&&r.Value is >=1000 and <=100000).Any(r=>{
                var a=new Terminal(r.Id,"1");var b=new Terminal(r.Id,"2");var supply=nets.Same(dq,a)?b:nets.Same(dq,b)?a:null;if(supply is null)return false;
                var v=result.Voltages.GetValueOrDefault(supply)-result.Voltages.GetValueOrDefault(ground);return rails.Concat(sources).Append(T("VDD")).Any(t=>nets.Same(supply,t))&&v is >=3 and <=5.5;
            });
            return new Ds18b20Device(p.Id,connected?pins[0]:0,powered,connected,p.Value,parasite,pullup,false,Rom(p),Memory(p));
        }).ToArray();
    }
    public static void ValidateRun(CircuitProject project){if(project.Parts.Count(p=>p.Kind==PartKind.Ds18b20)>8)throw new InvalidDataException("Одновременно доступны восемь DS18B20. Остальные можно сохранить в схеме.");}
}
