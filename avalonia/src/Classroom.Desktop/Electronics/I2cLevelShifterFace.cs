using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Classroom.Desktop.Electronics;

internal sealed class I2cLevelShifterFace : Control
{
    public static readonly Size Body = new(146, 130);
    const string BoardColor = "#255b48", ContactColor = "#dfbf72", LabelColor = "#f2f5ec", ChipColor = "#1c262a";
    static readonly IBrush board = Brush.Parse(BoardColor), contact = Brush.Parse(ContactColor), label = Brush.Parse(LabelColor), chip = Brush.Parse(ChipColor);
    static readonly Typeface typeface = new("Segoe UI");

    public I2cLevelShifterFace()
    {
        Width = Body.Width;
        Height = Body.Height;
        IsHitTestVisible = false;
    }

    public static Point Header(int index) => new(index < 4 ? 12 : Body.Width - 12, 25 + (index < 4 ? index : index - 3) * 25);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(board, null, new Rect(2, 2, 142, 125), 6, 6);
        context.DrawRectangle(chip, null, new Rect(61, 68, 24, 11), 2, 2);
        context.DrawRectangle(chip, null, new Rect(61, 93, 24, 11), 2, 2);
        DrawLabel("I2C", new(61, 16));
        for (var i = 0; i < I2cLevelShifter.Pins.Length; i++)
        {
            var at = Header(i);
            context.DrawEllipse(contact, null, at, 5, 5);
            DrawLabel(I2cLevelShifter.Pins[i], new(i < 4 ? 22 : 104, at.Y - 7));
        }
        void DrawLabel(string value, Point origin) =>
            context.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, label), origin);
    }

    public static string Svg()
    {
        var markup = new StringBuilder($"<rect x=\"2\" y=\"2\" width=\"142\" height=\"125\" rx=\"6\" fill=\"{BoardColor}\"/><text x=\"61\" y=\"25\" fill=\"{LabelColor}\" font-size=\"11\">I2C</text>");
        for (var i = 0; i < I2cLevelShifter.Pins.Length; i++)
        {
            var at = Header(i);
            markup.Append(FormattableString.Invariant($"<circle cx=\"{at.X}\" cy=\"{at.Y}\" r=\"5\" fill=\"{ContactColor}\"/><text x=\"{(i < 4 ? 22 : 104)}\" y=\"{at.Y + 4}\" fill=\"{LabelColor}\" font-size=\"11\">{I2cLevelShifter.Pins[i]}</text>"));
        }
        return markup.ToString();
    }
}
