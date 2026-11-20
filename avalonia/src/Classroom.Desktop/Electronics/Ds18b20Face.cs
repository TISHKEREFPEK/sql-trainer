using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Classroom.Desktop.Electronics;

public sealed class Ds18b20Face : Control
{
    public Ds18b20Face(){Width=100;Height=140;}
    public override void Render(DrawingContext c)
    {
        base.Render(c);var pins=PartLibrary.Pins(PartKind.Ds18b20);
        for(var i=0;i<3;i++){
            c.DrawRectangle(Brush.Parse("#AEB8C0"),null,new Rect(23+i*25,72,4,62));
            using(c.PushTransform(Matrix.CreateRotation(-Math.PI/2)*Matrix.CreateTranslation(20+i*25,133)))c.DrawText(new FormattedText(pins[i],System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),8,Brush.Parse("#253D4A")),new Point(0,0));
        }
        c.DrawRectangle(Brush.Parse("#17232B"),new Pen(Brush.Parse("#485660"),2),new Rect(9,3,82,78),14,14);
        c.DrawText(new FormattedText("DS18B20",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,Brushes.White),new Point(23,26));
        c.DrawText(new FormattedText("1-Wire",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,Brush.Parse("#A6B5C0")),new Point(35,47));
    }
    public static string Svg()
    {
        var b=new System.Text.StringBuilder();for(var i=0;i<3;i++)b.Append(FormattableString.Invariant($"<rect x=\"{23+i*25}\" y=\"72\" width=\"4\" height=\"62\" fill=\"#aeb8c0\"/><text transform=\"translate({20+i*25} 133) rotate(-90)\" font-size=\"8\">{PartLibrary.Pins(PartKind.Ds18b20)[i]}</text>"));
        return b.Append("<rect x=\"9\" y=\"3\" width=\"82\" height=\"78\" rx=\"14\" fill=\"#17232b\"/><text x=\"23\" y=\"38\" fill=\"white\" font-size=\"12\">DS18B20</text><text x=\"35\" y=\"57\" fill=\"#a6b5c0\" font-size=\"10\">1-Wire</text>").ToString();
    }
}
