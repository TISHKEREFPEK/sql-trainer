using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Classroom.Desktop.Electronics;

// The original generated PNGs remain intact. CroppedBitmap selects a sprite at
// render time; logical terminals are rendered separately from illustrative pins.
public static class PartArtwork
{
    static readonly string[] FileNames = ["components-core.png", "components-modules.png", "components-controls.png", "components-sensors.png"];
    record Sprite(int Sheet, Rect Region);
    static readonly Bitmap[] Sheets = FileNames.Select(Load).ToArray();
    static Bitmap Load(string name) => new(Path.Combine(AppContext.BaseDirectory, "electronics", "assets", name));
    static readonly Dictionary<string, Sprite> Regions = Build();
    static readonly Dictionary<string, IImage> Cache = new(StringComparer.Ordinal);
    static Dictionary<string, Sprite> Build()
    {
        var d = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        void Add(int sheet, string key, double x, double y, double w, double h) => d.Add(key, new(sheet, new(x, y, w, h)));
        Add(0,"uno",10,45,328,255); Add(0,"mega",343,65,398,228); Add(0,"esp32",752,30,185,290); Add(0,"pico",1077,60,124,250);
        Add(0,"breadboard",12,352,330,276); Add(0,"resistor",355,431,330,95); Add(0,"led",744,354,130,286); Add(0,"button",1010,374,200,242);
        Add(0,"pot",70,637,210,274); Add(0,"electrolytic",418,636,132,276); Add(0,"ceramic",700,650,175,263); Add(0,"diode",900,726,350,100);
        Add(0,"servo",8,920,348,296); Add(0,"motor",365,984,335,234); Add(0,"buzzer",700,951,194,260); Add(0,"lcd",901,965,360,215);
        Add(1,"ultrasonic",10,64,360,242); Add(1,"dht22",405,6,175,309); Add(1,"ldr",715,25,190,280); Add(1,"ntc",1050,20,125,290);
        Add(1,"relay",34,375,310,205); Add(1,"oled",380,321,240,280); Add(1,"seven-segment",717,340,184,271); Add(1,"keypad",995,322,244,311);
        Add(1,"mosfet",100,620,159,320); Add(1,"transistor",430,634,125,301); Add(1,"dip16",669,695,282,185); Add(1,"dip8",1025,695,183,187);
        Add(1,"buck",32,972,316,213); Add(1,"jack",382,944,251,275); Add(1,"battery",708,913,210,320); Add(1,"slide",997,947,228,280);
        var keys = new[] { "rgb-led","toggle","dip-switch","four-digit","stepper","l298n","a4988","joystick","dht11","pir","flame","ldr-module","mpu","rtc","breadboard-power","matrix" };
        for (var i = 0; i < keys.Length; i++) Add(2, keys[i], i % 4 * 320 + 4, i / 4 * 320 + 4, 312, 312);
        Add(3,"led-red",115,10,145,292); Add(3,"led-green",415,10,140,292); Add(3,"led-blue",715,10,140,292); Add(3,"led-yellow",1010,8,150,292);
        Add(3,"lm35",115,302,130,298); Add(3,"ir-receiver",407,303,155,295); Add(3,"phototransistor",714,300,154,290); Add(3,"regulator",1013,298,167,302);
        Add(3,"mq2",58,602,247,303); Add(3,"soil",403,588,173,322); Add(3,"water",699,587,179,325); Add(3,"bmp",984,638,210,271);
        Add(3,"rfid",35,900,309,350); Add(3,"mq135",382,910,225,332); Add(3,"dip16-accurate",694,916,174,320); Add(3,"eink",920,898,330,353);
        return d;
    }
    public static IImage? Image(string key)
    {
        if (!Regions.TryGetValue(key, out var sprite)) return null;
        if (Cache.TryGetValue(key, out var existing)) return existing;
        var source = Sheets[sprite.Sheet]; var r = sprite.Region;
        var x = (int)(r.X / 1280 * source.PixelSize.Width); var y = (int)(r.Y / 1280 * source.PixelSize.Height);
        var w = Math.Min(source.PixelSize.Width - x, (int)(r.Width / 1280 * source.PixelSize.Width));
        var h = Math.Min(source.PixelSize.Height - y, (int)(r.Height / 1280 * source.PixelSize.Height));
        return Cache[key] = new CroppedBitmap { Source = source, SourceRect = new PixelRect(x, y, w, h) };
    }
    public static Control Thumbnail(string key, double width = 90, double height = 70) => Image(key) is { } art
        ? new Image { Source = art, Width = width, Height = height, Stretch = Stretch.Uniform }
        : new TextBlock { Text = "⏚", FontSize = 36, Width = width, Height = height, TextAlignment = TextAlignment.Center };
    public static Size BaseBody(CircuitPart p)
    {
        if (p.Kind == PartKind.Uno) return new(260, 195);
        if (p.Kind == PartKind.Breadboard) return new(290, 430);
        return PartCatalog.For(p).Art switch
        {
            "mega" => new(300, 180), "pico" => new(220,400), "esp32" => new(130, 240), "resistor" or "diode" => new(160, 55),
            "led" or "led-red" or "led-green" or "led-blue" or "led-yellow" or "rgb-led" => new(65, 130), "breadboard" => new(290, 430), "lcd" => new(240, 125),
            "motor" => new(150, 100), "servo" => new(150, 155), "battery" => new(100, 160), "dip16" or "dip8" => new(150, 90),
            "seven-segment" => new(90, 135), "four-digit" => new(220, 100), "ultrasonic" => new(180, 110),
            "ground" => new(55, 65), _ => new(140, 145)
        };
    }
    public static Size Body(CircuitPart p)
    {
        var size = BaseBody(p); var (c,s) = Direction(p);
        var w=size.Width*p.Scale*p.StretchX; var h=size.Height*p.Scale*p.StretchY;
        return new(Math.Abs(c)*w+Math.Abs(s)*h,Math.Abs(s)*w+Math.Abs(c)*h);
    }
    public static double Degrees(CircuitPart p) => (p.QuarterTurns*90+p.Angle)%360;
    static (double C,double S) Direction(CircuitPart p)
    {
        var radians=Degrees(p)*Math.PI/180; var c=Math.Cos(radians);var s=Math.Sin(radians);
        return (Math.Abs(c)<1e-12?0:c,Math.Abs(s)<1e-12?0:s);
    }
    public static Point LocalPoint(CircuitPart p, Point point)
    {
        return point.Transform(TransformMatrix(p));
    }
    public static Matrix TransformMatrix(CircuitPart p, double zoom = 1)
    {
        var size=BaseBody(p); var(c,s)=Direction(p);var sx=p.Scale*p.StretchX;var sy=p.Scale*p.StretchY;
        var w=size.Width*sx;var h=size.Height*sy;
        var x=-Math.Min(0,c*w)-Math.Min(0,-s*h);var y=-Math.Min(0,s*w)-Math.Min(0,c*h);
        if(p.Rotation%2==1){x+=c*w;y+=s*w;sx=-sx;}
        static double Clean(double value)=>Math.Abs(value)<1e-12?0:value;
        return new(Clean(c*sx*zoom),Clean(s*sx*zoom),Clean(-s*sy*zoom),Clean(c*sy*zoom),Clean(x*zoom),Clean(y*zoom));
    }
    // Frame corners follow the physical outline; mirroring changes artwork only.
    public static Point[] Corners(CircuitPart p)
    {
        var size=BaseBody(p); var frame=p with {Rotation=0};
        return new[]{new Point(0,0),new Point(size.Width,0),new Point(size.Width,size.Height),new Point(0,size.Height)}
            .Select(v=>{var q=LocalPoint(frame,v);return new Point(p.X+q.X,p.Y+q.Y);}).ToArray();
    }
    public static CircuitPart WithAngle(CircuitPart p,double angle)
    {
        var before=Body(p);var next=p with {Angle=(angle%360+360)%360};var after=Body(next);
        return next with {X=Math.Clamp(p.X+(before.Width-after.Width)/2,0,4000),Y=Math.Clamp(p.Y+(before.Height-after.Height)/2,0,3000)};
    }
    public static CircuitPart ResizeCorner(CircuitPart p,int corner,Point pointer,bool keepAspect=false)
    {
        var anchor=Corners(p)[(corner+2)%4];var(c,s)=Direction(p);var dx=pointer.X-anchor.X;var dy=pointer.Y-anchor.Y;
        var size=BaseBody(p);var signX=corner is 1 or 2?1:-1;var signY=corner>=2?1:-1;
        var sx=Math.Clamp((c*dx+s*dy)*signX/(size.Width*p.Scale),0.1,10);
        var sy=Math.Clamp((-s*dx+c*dy)*signY/(size.Height*p.Scale),0.1,10);
        if(keepAspect){var factor=Math.Max(sx/p.StretchX,sy/p.StretchY);factor=Math.Clamp(factor,Math.Max(0.1/p.StretchX,0.1/p.StretchY),Math.Min(10/p.StretchX,10/p.StretchY));sx=p.StretchX*factor;sy=p.StretchY*factor;}
        var next=p with {StretchX=sx,StretchY=sy};var shifted=Corners(next)[(corner+2)%4];
        return next with {X=Math.Clamp(p.X+anchor.X-shifted.X,0,4000),Y=Math.Clamp(p.Y+anchor.Y-shifted.Y,0,3000)};
    }
    public static CircuitPart WithLayout(CircuitPart p, double scale, int quarterTurns)
    {
        var before = Body(p); var next = p with { Scale = scale, QuarterTurns = quarterTurns }; var after = Body(next);
        return next with { X = Math.Clamp(p.X + (before.Width-after.Width)/2,0,4000), Y = Math.Clamp(p.Y + (before.Height-after.Height)/2,0,3000) };
    }
    public static Point Pin(CircuitPart p, string pin)
    {
        var size = BaseBody(p); var pins = PartLibrary.Pins(p); var i = Array.IndexOf(pins, pin);
        double x, y;
        if (p.Kind == PartKind.Uno)
        {
            if (i < 14) { x = 246 - i * 13.4; y = 9; }
            else if (i < 20) { x = 161 + (i - 14) * 14.4; y = 185; }
            else { x = i == 20 ? 109 : 124; y = 185; }
        }
        else if (PartCatalog.For(p).ReferenceName == "Raspberry Pi Pico" && pins.Length == 40)
        {
            x = i < 20 ? 20 : size.Width - 20;
            y = 70 + (i < 20 ? i : 39 - i) * 15;
        }
        else if (p.Kind == PartKind.Breadboard)
        {
            if (pin.StartsWith('L') || pin.StartsWith('R'))
            {
                var row = int.Parse(pin[2..], System.Globalization.CultureInfo.InvariantCulture);
                x = pin[0] == 'L' ? pin[1] == '+' ? 15 : 30 : pin[1] == '+' ? 260 : 275;
                y = 40 + (row - 1) * 14;
            }
            else
            {
                var row = int.Parse(pin[1..], System.Globalization.CultureInfo.InvariantCulture); var col = pin[0] - 'a';
                x = col < 5 ? 65 + col * 15 : 165 + (col - 5) * 15; y = 40 + (row - 1) * 12;
            }
        }
        else if (PartCatalog.For(p).Art is "resistor" or "diode") { x = i == 0 ? 3 : size.Width - 3; y = size.Height / 2; }
        else { x = size.Width * (i + 1) / (pins.Length + 1); y = size.Height - 5; }
        var point = LocalPoint(p, new(x,y));
        return new(p.X + point.X, p.Y + point.Y);
    }
    public static string SvgDefinitions()
    {
        var b = new System.Text.StringBuilder("<defs>");
        for (var i = 0; i < FileNames.Length; i++)
        {
            var data = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "electronics", "assets", FileNames[i])));
            b.Append($"<image id=\"atlas-{i}\" width=\"1280\" height=\"1280\" preserveAspectRatio=\"none\" href=\"data:image/png;base64,{data}\"/>");
        }
        return b.Append("</defs>").ToString();
    }
    public static string SvgPart(CircuitPart p)
    {
        var size = BaseBody(p); var m = TransformMatrix(p);
        var prefix = FormattableString.Invariant($"<g transform=\"translate({p.X} {p.Y}) matrix({m.M11} {m.M12} {m.M21} {m.M22} {m.M31} {m.M32})\">");
        if (p.Kind == PartKind.Breadboard) return prefix + FormattableString.Invariant($"<rect width=\"{size.Width}\" height=\"{size.Height}\" rx=\"8\" fill=\"#fafafa\" stroke=\"#b8c0c8\"/>") + "</g>";
        if (!Regions.TryGetValue(PartCatalog.For(p).Art, out var sprite)) return prefix + "<text x=\"15\" y=\"35\" font-size=\"36\">&#x23DA;</text></g>";
        var r = sprite.Region;
        return prefix + FormattableString.Invariant($"<svg width=\"{size.Width}\" height=\"{size.Height}\" viewBox=\"{r.X} {r.Y} {r.Width} {r.Height}\"><use href=\"#atlas-{sprite.Sheet}\"/></svg></g>");
    }
}

