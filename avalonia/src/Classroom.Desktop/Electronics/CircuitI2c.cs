namespace Classroom.Desktop.Electronics;

public sealed record LcdDevice(string Id,int Address,int Columns,int Rows,bool Connected,bool Powered);
public sealed record LcdFrame(string Id,int Columns,int Rows,int[] Cells,int[] Cgram,bool Powered,bool Display,bool Backlight,int Cursor,bool Blink,bool Underline);
public static class CircuitI2c
{
    public static (int Columns,int Rows) Size(CircuitPart part)=>part.ModelId=="lcd-i2c-20x4"?(20,4):(16,2);
    // Only wires and breadboard strips establish protocol connectivity. A
    // resistor/source does not make two data nets the same physical bus.
    public static LcdDevice[] Devices(CircuitProject project,CircuitResult? result=null)
    {
        if(!project.Parts.Any(p=>p.Kind==PartKind.LcdI2c))return [];
        var controller=project.Parts.FirstOrDefault(p=>AvrBoards.For(p) is not null);if(controller is null)return [];
        var parents=project.Parts.SelectMany(p=>PartLibrary.Pins(p).Select(pin=>new Terminal(p.Id,pin))).ToDictionary(t=>t,t=>t);
        Terminal Root(Terminal t){while(parents[t]!=t)t=parents[t];return t;}
        void Join(Terminal a,Terminal b)=>parents[Root(a)]=Root(b);
        foreach(var w in project.Wires)Join(w.From,w.To);
        foreach(var board in project.Parts.Where(p=>p.Kind==PartKind.Breadboard))
        {
            for(var row=1;row<=30;row++)foreach(var group in new[]{"abcde","fghij"})foreach(var c in group.Skip(1))Join(new(board.Id,group[0]+row.ToString(System.Globalization.CultureInfo.InvariantCulture)),new(board.Id,c+row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            foreach(var rail in new[]{"L+","L−","R+","R−"})for(var row=2;row<=25;row++)Join(new(board.Id,rail+"1"),new(board.Id,rail+row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        var ground=new Terminal(controller.Id,"GND");foreach(var p in project.Parts.Where(p=>p.Kind==PartKind.Ground))Join(ground,new(p.Id,"GND"));
        result??=CircuitSolver.Solve(project);
        var sda=Root(new(controller.Id,controller.Kind==PartKind.Mega?"D20":"A4"));var scl=Root(new(controller.Id,controller.Kind==PartKind.Mega?"D21":"A5"));
        return project.Parts.Where(p=>p.Kind==PartKind.LcdI2c).Select(p=>
        {
            var size=Size(p);var gnd=new Terminal(p.Id,"GND");var vcc=new Terminal(p.Id,"VCC");
            var voltage=result.Voltages.GetValueOrDefault(vcc)-result.Voltages.GetValueOrDefault(gnd);
            return new LcdDevice(p.Id,(int)p.Value,size.Columns,size.Rows,sda!=scl&&Root(new(p.Id,"SDA"))==sda&&Root(new(p.Id,"SCL"))==scl&&result.Voltages.GetValueOrDefault(new(p.Id,"SDA"))>3.5&&result.Voltages.GetValueOrDefault(new(p.Id,"SCL"))>3.5,
                Root(gnd)==Root(ground)&&voltage is >=4.5 and <=5.5);
        }).ToArray();
    }
}
