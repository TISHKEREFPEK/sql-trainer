namespace Classroom.Desktop.Electronics;

public sealed record I2cDevice(string Id,int Address,int Columns,int Rows,bool Connected,bool Powered,string Kind="lcd");
public sealed record OledFrame(string Id,int Width,int Height,bool Powered,bool Display,int Contrast,string Pixels,string Memory);
public sealed record LcdFrame(string Id,int Columns,int Rows,int[] Cells,int[] Cgram,bool Powered,bool Display,bool Backlight,int Cursor,bool Blink,bool Underline);
public static class CircuitI2c
{
    public static bool IsDevice(CircuitPart part)=>part.Kind is PartKind.LcdI2c or PartKind.OledI2c;
    public static string PowerPin(CircuitPart part)=>part.Kind==PartKind.OledI2c?"VDD":"VCC";
    public static (int Columns,int Rows) Size(CircuitPart part)=>part.Kind==PartKind.OledI2c?(128,64):part.ModelId=="lcd-i2c-20x4"?(20,4):(16,2);
    // Only wires and breadboard strips establish protocol connectivity. A
    // resistor/source does not make two data nets the same physical bus.
    public static I2cDevice[] Devices(CircuitProject project,CircuitResult? result=null)
    {
        if(!project.Parts.Any(IsDevice))return [];
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
        return project.Parts.Where(IsDevice).Select(p=>
        {
            var size=Size(p);var gnd=new Terminal(p.Id,"GND");var vcc=new Terminal(p.Id,PowerPin(p));var oled=p.Kind==PartKind.OledI2c;
            var voltage=result.Voltages.GetValueOrDefault(vcc)-result.Voltages.GetValueOrDefault(gnd);
            return new I2cDevice(p.Id,(int)p.Value,size.Columns,size.Rows,sda!=scl&&Root(new(p.Id,"SDA"))==sda&&Root(new(p.Id,"SCL"))==scl&&result.Voltages.GetValueOrDefault(new(p.Id,"SDA"))>voltage*.7&&result.Voltages.GetValueOrDefault(new(p.Id,"SCL"))>voltage*.7,
                Root(gnd)==Root(ground)&&voltage>=(oled?3.0:4.5)&&voltage<=5.5,oled?"ssd1306":"lcd");
        }).ToArray();
    }
}
