using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Text.RegularExpressions;

namespace Classroom.Desktop.Electronics;

public sealed class LcdDisplayFace : Control
{
    static readonly byte[] font=LoadFont();
    static byte[] LoadFont()
    {
        var source=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"electronics","vendor","adafruit-font","glcdfont.c"));
        source=Regex.Replace(source[(source.IndexOf('{')+1)..source.IndexOf('}')],@"//[^\r\n]*","");
        var bytes=Regex.Matches(source,@"0x[0-9a-fA-F]{2}").Select(m=>Convert.ToByte(m.Value[2..],16)).ToArray();
        return bytes.Length==1280?bytes:throw new InvalidDataException("Повреждён шрифт LCD.");
    }
    readonly CircuitPart part;
    readonly LcdFrame? frame;
    public LcdDisplayFace(CircuitPart part,LcdFrame? frame=null)
    {this.part=part;this.frame=frame;var size=PartArtwork.BaseBody(part);Width=size.Width;Height=size.Height;IsHitTestVisible=false;}
    public override void Render(DrawingContext context)
    {
        var size=PartArtwork.BaseBody(part);var (columns,rows)=CircuitI2c.Size(part);var pitch=(size.Width-64)/(columns*6);
        context.DrawRectangle(Brush.Parse("#17664D"),new Pen(Brush.Parse("#104D3A"),2),new Rect(0,0,size.Width,size.Height),6,6);
        foreach(var point in new[]{new Point(11,11),new Point(size.Width-11,11),new Point(11,size.Height-11),new Point(size.Width-11,size.Height-11)})context.DrawEllipse(Brush.Parse("#D7C48A"),new Pen(Brush.Parse("#39463A"),2),point,4,4);
        var backlight=frame?.Backlight==true;
        context.DrawRectangle(Brush.Parse("#182323"),null,new Rect(24,24,size.Width-48,rows*10*pitch+16),3,3);
        context.DrawRectangle(Brush.Parse(backlight?"#173FA2":"#293D48"),null,new Rect(30,30,size.Width-60,rows*10*pitch+4));
        var on=Brush.Parse(backlight?"#DDEDB8":"#83958B");var off=Brush.Parse(backlight?"#1C459F":"#2A4048");
        for(var row=0;row<rows;row++)for(var col=0;col<columns;col++)
        {
            var index=row*columns+col;var code=frame?.Cells.ElementAtOrDefault(index)??32;
            for(var y=0;y<8;y++)for(var x=0;x<5;x++)
            {
                var bit=code<16?(frame?.Cgram.ElementAtOrDefault((code&7)*8+y)??0)>>(4-x)&1:font[(code&255)*5+x]>>y&1;
                var cursor=frame?.Cursor==index&&(y==7&&frame.Underline||frame.Blink);
                var lit=frame?.Display==true&&(bit!=0||cursor);
                context.DrawRectangle(lit?on:off,null,new Rect(34+(col*6+x)*pitch,34+(row*10+y)*pitch,pitch*.85,pitch*.85));
            }
        }
        var label=new FormattedText($"{columns}×{rows}  PCF8574 · 0x{(int)part.Value:X2}",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,Typeface.Default,11,Brushes.White);
        context.DrawText(label,new Point(30,size.Height-25));
    }
}
