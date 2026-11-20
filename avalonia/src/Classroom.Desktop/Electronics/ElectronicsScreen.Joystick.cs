using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    static readonly InputRange joystickX = new("Ось X", "%", 0, 100, 1,
        p => (p.Joystick ?? ElectronicsJoystick.Center).X * 100,
        (p, value) => p with { Joystick = (p.Joystick ?? ElectronicsJoystick.Center) with { X = value / 100 } });
    static readonly InputRange joystickY = new("Ось Y", "%", 0, 100, 1,
        p => (p.Joystick ?? ElectronicsJoystick.Center).Y * 100,
        (p, value) => p with { Joystick = (p.Joystick ?? ElectronicsJoystick.Center) with { Y = value / 100 } });

    void DrawJoystickInputs(Canvas face, CircuitPart part, Size body)
    {
        const double side = 102;
        const double inset = 12;
        var left = (body.Width - side) / 2;
        const double top = 28;
        var track = new Rect(left + inset, top + inset, side - inset * 2, side - inset * 2);
        var position = part.Joystick ?? ElectronicsJoystick.Center;
        var art = new Canvas { IsHitTestVisible = false };
        var guides = new[]
        {
            new Line { StartPoint = new(inset, side / 2), EndPoint = new(side - inset, side / 2) },
            new Line { StartPoint = new(side / 2, inset), EndPoint = new(side / 2, side - inset) }
        };
        foreach (var guide in guides)
        {
            guide.Stroke = inputRail;
            guide.StrokeThickness = 1;
            art.Children.Add(guide);
        }
        var thumb = new Ellipse { Width = 16, Height = 16, Fill = inputThumb, Stroke = Brushes.White, StrokeThickness = 1.5 };
        Canvas.SetLeft(thumb, inset + position.X * track.Width - thumb.Width / 2);
        Canvas.SetTop(thumb, inset + (1 - position.Y) * track.Height - thumb.Height / 2);
        art.Children.Add(thumb);
        var stick = new Border
        {
            Name = "CircuitJoystickAxes", Width = side, Height = side, Child = art,
            Background = inputBackground, CornerRadius = new CornerRadius(12), Cursor = inputCursor
        };
        AutomationProperties.SetName(stick, $"Джойстик · X {position.X:P0}, Y {position.Y:P0}");
        ToolTip.SetTip(stick, "Перетащите рукоятку. ПКМ — свойства.");
        Canvas.SetLeft(stick, left);
        Canvas.SetTop(stick, top);
        face.Children.Add(stick);
        BindInput(stick, part.Id, (current, local) => current with
        {
            Joystick = new(Quantize(joystickX, (local.X - track.Left) / track.Width * 100) / 100,
                Quantize(joystickY, (track.Bottom - local.Y) / track.Height * 100) / 100)
        });

        var button = new Border
        {
            Name = "CircuitJoystickButton", Width = 96, Height = 28, CornerRadius = new CornerRadius(6),
            Background = part.Active ? inputActiveBackground : inputBackground,
            Cursor = inputCursor, Child = InputCaption(part.Active ? "SW нажат" : "Удерживать SW")
        };
        button.Child!.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        AutomationProperties.SetName(button, part.Active ? "SW нажат" : "Удерживать SW");
        ToolTip.SetTip(button, "Держите ЛКМ, чтобы замкнуть кнопку на землю. ПКМ — свойства.");
        Canvas.SetLeft(button, (body.Width - button.Width) / 2);
        Canvas.SetTop(button, body.Height - 75);
        face.Children.Add(button);
        BindInput(button, part.Id, (current, _) => current with { Active = true }, current => current with { Active = false });
    }

    void InspectJoystick(CircuitPart part)
    {
        properties.Children.Add(Ui.Muted("VRX и VRY — аналоговые выходы. Подайте на VCC напряжение платы и соедините GND. SW замыкается на GND. Для SW включите INPUT_PULLUP в коде.", 12));
        AddInspectorSlider(part, joystickX);
        AddInspectorSlider(part, joystickY);
        properties.Children.Add(Ui.Button("Вернуть в центр", () => Perform(() =>
            ChangeInput(part.Id, current => current with { Joystick = ElectronicsJoystick.Center }, remember: true))));
    }
}
