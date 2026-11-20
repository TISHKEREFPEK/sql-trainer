using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Classroom.Desktop.Electronics;

internal sealed class Mpu6050Face : Control
{
    public static readonly Size Body = new(306, 163);
    static readonly IBrush board = Brush.Parse("#265a72"), pad = Brush.Parse("#dabe78"), label = Brush.Parse("#f1f5fb");
    static readonly IBrush chip = Brush.Parse("#151d23");
    static readonly Typeface font = new("Segoe UI");

    public Mpu6050Face()
    {
        Width = Body.Width;
        Height = Body.Height;
        IsHitTestVisible = false;
    }

    public static Point Header(int index) => new(37 + 58 * index, 151);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(board, null, new Rect(1, 1, 304, 161), 8, 8);
        context.DrawRectangle(chip, null, new Rect(109, 31, 88, 73), 4, 4);
        for (var i = 0; i < CircuitMpu6050.Pins.Length; i++)
        {
            var at = Header(i);
            var caption = new FormattedText(CircuitMpu6050.Pins[i], CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, font, 12, label);
            context.DrawEllipse(pad, null, at, 5.5, 5.5);
            context.DrawText(caption, new Point(at.X - caption.Width / 2, 130));
        }
    }

    public static string Svg()
    {
        var markup = new StringBuilder("<rect x=\"1\" y=\"1\" width=\"304\" height=\"161\" rx=\"8\" fill=\"#265a72\"/><text x=\"153\" y=\"69\" fill=\"#f1f5fb\" text-anchor=\"middle\" font-size=\"24\">MPU-6050</text>");
        for (var i = 0; i < CircuitMpu6050.Pins.Length; i++)
        {
            var at = Header(i);
            markup.Append(FormattableString.Invariant($"<circle cx=\"{at.X}\" cy=\"{at.Y}\" r=\"5.5\" fill=\"#dabe78\"/><text x=\"{at.X}\" y=\"141\" fill=\"#f1f5fb\" text-anchor=\"middle\" font-size=\"12\">{CircuitMpu6050.Pins[i]}</text>"));
        }
        return markup.ToString();
    }
}
