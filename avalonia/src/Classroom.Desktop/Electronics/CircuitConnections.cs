namespace Classroom.Desktop.Electronics;

// Physical wires/strips only; resistors and semiconductor models are not wires.
public sealed class CircuitConnections
{
    readonly Dictionary<Terminal,Terminal> parents;
    public CircuitConnections(CircuitProject project)
    {
        parents=project.Parts.SelectMany(p=>PartLibrary.Pins(p).Select(pin=>new Terminal(p.Id,pin))).ToDictionary(t=>t,t=>t);
        foreach(var wire in project.Wires)Join(wire.From,wire.To);
        foreach(var board in project.Parts.Where(p=>p.Kind==PartKind.Breadboard)){
            for(var row=1;row<=30;row++)foreach(var group in new[]{"abcde","fghij"})foreach(var c in group.Skip(1))Join(new(board.Id,group[0]+row.ToString(System.Globalization.CultureInfo.InvariantCulture)),new(board.Id,c+row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            foreach(var rail in new[]{"L+","L−","R+","R−"})for(var row=2;row<=25;row++)Join(new(board.Id,rail+"1"),new(board.Id,rail+row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        foreach(var pico in project.Parts.Where(p=>p.Kind==PartKind.Pico))foreach(var pin in PicoBoard.Pins.Where(PicoBoard.IsGround))Join(new(pico.Id,PicoBoard.Ground),new(pico.Id,pin));
        var grounds=project.Parts.Where(p=>p.Kind==PartKind.Ground||CircuitBoards.IsBoard(p)).Select(p=>new Terminal(p.Id,CircuitBoards.IsBoard(p)?CircuitBoards.Ground(p):"GND")).ToArray();
        foreach(var ground in grounds.Skip(1))Join(grounds[0],ground);
    }
    public Terminal Root(Terminal pin){while(parents[pin]!=pin)pin=parents[pin];return pin;}
    void Join(Terminal a,Terminal b)=>parents[Root(a)]=Root(b);
    public bool Same(Terminal a,Terminal b)=>Root(a)==Root(b);
}

public static class CircuitPico
{
    public static PicoI2cDevice[] Devices(CircuitProject project,CircuitResult? result=null)
    {
        var board=project.Parts.SingleOrDefault(p=>p.Kind==PartKind.Pico);if(board is null)return [];
        var nets=new CircuitConnections(project);result??=CircuitSolver.Solve(project);
        var grounds=new Terminal(board.Id,PicoBoard.Ground);
        var ports=Enumerable.Range(0,30).Where(i=>PicoBoard.Gpio(i) is not null).ToArray();
        return project.Parts.Where(CircuitI2c.IsDevice).Select(device=>{
            var data=new Terminal(device.Id,"SDA");var clock=new Terminal(device.Id,"SCL");
            var sda=ports.Where(i=>nets.Same(data,new(board.Id,PicoBoard.Gpio(i)!))).ToArray();
            var scl=ports.Where(i=>nets.Same(clock,new(board.Id,PicoBoard.Gpio(i)!))).ToArray();
            var connected=sda.Length==1&&scl.Length==1&&!nets.Same(data,clock)&&!new[]{PicoBoard.Ground,PicoBoard.V3,PicoBoard.Vbus,PicoBoard.Vsys}.Any(pin=>nets.Same(data,new(board.Id,pin))||nets.Same(clock,new(board.Id,pin)));
            var voltage=result.Voltages.GetValueOrDefault(new(device.Id,CircuitI2c.PowerPin(device)))-result.Voltages.GetValueOrDefault(new(device.Id,"GND"));
            var oled=device.Kind==PartKind.OledI2c;var size=CircuitI2c.Size(device);
            return new PicoI2cDevice(device.Id,(int)device.Value,size.Columns,size.Rows,connected,nets.Same(new(device.Id,"GND"),grounds)&&voltage>=(oled?3.0:4.5)&&voltage<=5.5,oled?"ssd1306":"lcd",connected?sda[0]%4/2:0,connected?sda[0]:4,connected?scl[0]:5);
        }).ToArray();
    }
    public static (bool?[] Digital,double[] Analog) Inputs(CircuitProject project,CircuitResult result,PicoI2cDevice[] devices,IReadOnlyList<SpiDisplayDevice>? spiDevices=null,IReadOnlyList<Dht11Device>? dht11Devices=null,IReadOnlyList<Hcsr04Device>? hcsr04Devices=null,IReadOnlyList<Ds18b20Device>? ds18b20Devices=null)
    {
        var board=project.Parts.Single(p=>p.Kind==PartKind.Pico);var digital=new bool?[30];var nets=new CircuitConnections(project);
        for(var i=0;i<30;i++)if(PicoBoard.Gpio(i) is {}pin){
            var terminal=new Terminal(board.Id,pin);var value=result.Voltages.GetValueOrDefault(terminal);
            if(!double.IsFinite(value)||value<-.1||value>3.45)throw new InvalidDataException($"Pico {pin}: {value:F2} В. GPIO работают на 3,3 В; проверьте питание и согласование уровней.");
            if(ds18b20Devices?.Any(d=>d.Connected&&d.Pin==i)==true)continue;
            if(hcsr04Devices?.Any(d=>d.Connected&&d.Echoes.Any(e=>e.Pin==i))==true)continue;
            if(dht11Devices?.Any(d=>d.Connected&&d.Pin==i)==true)continue;
            if(devices.Any(d=>d.Connected&&(d.Sda==i||d.Scl==i)))continue;
            if(spiDevices?.Any(d=>d.Connected&&(d.Mosi==i||d.Sck==i||d.Miso==i))==true)continue;
            if(project.Wires.Any(w=>nets.Same(w.From,terminal)||nets.Same(w.To,terminal)))digital[i]=value>1.65;
        }
        digital[24]=true; // onboard VBUS sense in the virtual-USB power profile
        var analog=Enumerable.Range(0,3).Select(i=>Math.Clamp(result.Voltages.GetValueOrDefault(new(board.Id,PicoBoard.Gpio(26+i)!)),0,3.3)).Append(result.Voltages.GetValueOrDefault(new(board.Id,PicoBoard.Vsys))/3).ToArray();
        return(digital,analog);
    }
}
