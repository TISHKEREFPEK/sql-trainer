using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Classroom.Desktop.Electronics;

// Original vector artwork; no Cirkit illustration is included.
public sealed class Dht11Face : Control
{
    public Dht11Face(){Width=120;Height=160;}
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        for(var i=0;i<4;i++){
            context.DrawRectangle(Brush.Parse("#B8BEC5"),null,new Rect(30+i*20,122,4,35));
            using(context.PushTransform(Matrix.CreateRotation(-Math.PI/2)*Matrix.CreateTranslation(27+i*20,155)))
                context.DrawText(new FormattedText(PartLibrary.Pins(PartKind.Dht11)[i],System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),8,Brush.Parse("#17415B")),new Point(0,0));
        }
        context.DrawRectangle(Brush.Parse("#2089C7"),new Pen(Brush.Parse("#135482"),2),new Rect(12,2,96,128),5,5);
        for(var row=0;row<7;row++)for(var col=0;col<4;col++)context.DrawRectangle(Brush.Parse("#145B87"),null,new Rect(23+col*20,12+row*12,13,6),2,2);
        context.DrawText(new FormattedText("DHT11",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),15,Brushes.White),new Point(35,102));
    }
    public static string Svg()
    {
        var b=new System.Text.StringBuilder();
        for(var i=0;i<4;i++)b.Append(FormattableString.Invariant($"<rect x=\"{30+i*20}\" y=\"122\" width=\"4\" height=\"35\" fill=\"#b8bec5\"/><text transform=\"translate({27+i*20} 155) rotate(-90)\" fill=\"#17415b\" font-size=\"8\">{PartLibrary.Pins(PartKind.Dht11)[i]}</text>"));
        b.Append("<rect x=\"12\" y=\"2\" width=\"96\" height=\"128\" rx=\"5\" fill=\"#2089c7\" stroke=\"#135482\"/>");
        for(var row=0;row<7;row++)for(var col=0;col<4;col++)b.Append(FormattableString.Invariant($"<rect x=\"{23+col*20}\" y=\"{12+row*12}\" width=\"13\" height=\"6\" rx=\"2\" fill=\"#145b87\"/>"));
        return b.Append("<text x=\"35\" y=\"118\" fill=\"white\" font-size=\"15\">DHT11</text>").ToString();
    }
}
