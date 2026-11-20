using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Classroom.Desktop.Electronics;

// Original vector drawing of a four-pin ultrasonic module.
public sealed class Hcsr04Face : Control
{
    public Hcsr04Face(){Width=260;Height=140;}
    public override void Render(DrawingContext c)
    {
        base.Render(c);c.DrawRectangle(Brush.Parse("#176B98"),new Pen(Brush.Parse("#16445F"),2),new Rect(2,2,256,105),4,4);
        foreach(var x in new[]{61,199}){
            c.DrawEllipse(Brush.Parse("#BEC7CE"),new Pen(Brush.Parse("#657783"),3),new Point(x,51),43,43);
            c.DrawEllipse(Brush.Parse("#334752"),new Pen(Brush.Parse("#EEF3F6"),3),new Point(x,51),34,34);
            for(var i=-24;i<=24;i+=8){var length=Math.Sqrt(30*30-i*i);c.DrawLine(new Pen(Brush.Parse("#7D8F99"),1),new Point(x+i,51-length),new Point(x+i,51+length));}
        }
        c.DrawText(new FormattedText("HC-SR04",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,Brushes.White),new Point(102,89));
        for(var i=0;i<4;i++){
            c.DrawRectangle(Brush.Parse("#B8BEC5"),null,new Rect(100+i*20,107,4,29));
            using(c.PushTransform(Matrix.CreateRotation(-Math.PI/2)*Matrix.CreateTranslation(97+i*20,138)))c.DrawText(new FormattedText(PartLibrary.Pins(PartKind.Hcsr04)[i],System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),8,Brush.Parse("#17415B")),new Point(0,0));
        }
    }
    public static string Svg()
    {
        var b=new System.Text.StringBuilder("<rect x=\"2\" y=\"2\" width=\"256\" height=\"105\" rx=\"4\" fill=\"#176b98\"/>");
        foreach(var x in new[]{61,199}){
            b.Append(FormattableString.Invariant($"<circle cx=\"{x}\" cy=\"51\" r=\"43\" fill=\"#bec7ce\" stroke=\"#657783\"/><circle cx=\"{x}\" cy=\"51\" r=\"34\" fill=\"#334752\" stroke=\"#eef3f6\"/>"));
            for(var i=-24;i<=24;i+=8){var length=Math.Sqrt(30*30-i*i);b.Append(FormattableString.Invariant($"<line x1=\"{x+i}\" y1=\"{51-length}\" x2=\"{x+i}\" y2=\"{51+length}\" stroke=\"#7d8f99\"/>"));}
        }
        for(var i=0;i<4;i++)b.Append(FormattableString.Invariant($"<rect x=\"{100+i*20}\" y=\"107\" width=\"4\" height=\"29\" fill=\"#b8bec5\"/><text transform=\"translate({97+i*20} 138) rotate(-90)\" font-size=\"8\">{PartLibrary.Pins(PartKind.Hcsr04)[i]}</text>"));
        return b.Append("<text x=\"102\" y=\"101\" fill=\"white\" font-size=\"12\">HC-SR04</text>").ToString();
    }
}
