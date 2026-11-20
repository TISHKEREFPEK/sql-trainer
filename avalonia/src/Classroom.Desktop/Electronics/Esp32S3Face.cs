using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Classroom.Desktop.Electronics;

// Original schematic illustration of the hardware layout.
public sealed class Esp32S3Face : Control
{
    public const string Art = "esp32s3-devkitc";
    public static readonly Size Body = new(218, 462);
    const string BoardColor = "#202a34", MetalColor = "#b8c4cc", HeaderColor = "#c8a560", LabelColor = "#e6eef5";
    static readonly IBrush board = Brush.Parse(BoardColor), metal = Brush.Parse(MetalColor),
        header = Brush.Parse(HeaderColor), label = Brush.Parse(LabelColor), chip = Brush.Parse("#0e151b");
    static readonly Typeface typeface = new("Segoe UI");

    public Esp32S3Face()
    {
        Width = Body.Width;
        Height = Body.Height;
    }

    public static Point Header(int index) => new(index < Esp32S3Board.HeaderLength ? 17 : Body.Width - 17,
        33 + index % Esp32S3Board.HeaderLength * 17.4);

    static string PinLabel(int index) => Esp32S3Board.Pins[index].Split(':', 2)[1];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(board, null, new Rect(4, 1, Body.Width - 8, 438), 8, 8);
        context.DrawRectangle(chip, null, new Rect(64, 9, 90, 143), 3, 3);
        context.DrawRectangle(metal, null, new Rect(66, 48, 86, 102), 3, 3);
        context.DrawRectangle(header, null, new Rect(73, 26, 70, 3));
        context.DrawRectangle(chip, null, new Rect(88, 241, 42, 40), 3, 3);
        context.DrawRectangle(chip, null, new Rect(71, 358, 22, 18), 2, 2);
        context.DrawRectangle(chip, null, new Rect(124, 358, 22, 18), 2, 2);
        context.DrawRectangle(metal, null, new Rect(64, 413, 39, 35), 4, 4);
        context.DrawRectangle(metal, null, new Rect(115, 413, 39, 35), 4, 4);
        for (var i = 0; i < Esp32S3Board.Pins.Length; i++)
        {
            var at = Header(i);
            context.DrawEllipse(header, null, at, 5.5, 5.5);
            var text = new FormattedText(PinLabel(i), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10.2, label);
            context.DrawText(text, new Point(i < Esp32S3Board.HeaderLength ? at.X + 10 : at.X - 10 - text.Width, at.Y - 7));
        }
    }

    public static string Svg()
    {
        var svg = new StringBuilder(FormattableString.Invariant(
            $"<rect x=\"4\" y=\"1\" width=\"210\" height=\"438\" rx=\"8\" fill=\"{BoardColor}\"/><rect x=\"66\" y=\"48\" width=\"86\" height=\"102\" rx=\"3\" fill=\"{MetalColor}\"/><rect x=\"64\" y=\"413\" width=\"39\" height=\"35\" rx=\"4\" fill=\"{MetalColor}\"/><rect x=\"115\" y=\"413\" width=\"39\" height=\"35\" rx=\"4\" fill=\"{MetalColor}\"/>"));
        for (var i = 0; i < Esp32S3Board.Pins.Length; i++)
        {
            var at = Header(i);
            svg.Append(FormattableString.Invariant($"<circle cx=\"{at.X}\" cy=\"{at.Y}\" r=\"5.5\" fill=\"{HeaderColor}\"/>"));
        }
        return svg.ToString();
    }
}
