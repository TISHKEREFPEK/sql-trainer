using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    sealed record InputRange(string Label, string Unit, double Minimum, double Maximum, double Step,
        Func<CircuitPart, double> Read, Func<CircuitPart, double, CircuitPart> Write)
    {
        public string Display(double value) => $"{Label} · {value:0.####} {Unit}";
    }

    static readonly InputRange wiper = new("Положение", "%", 0, 100, 1,
        p => p.Setting * 100, (p, value) => p with { Setting = value / 100 });
    static readonly InputRange resistance = new("Сопротивление", "Ом", 1, 100_000, 1,
        p => p.Value, (p, value) => p with { Value = value });
    static readonly InputRange voltage = new("Напряжение", "В", 0, 5, 0.01,
        p => p.Value, (p, value) => p with { Value = value });
    static readonly InputRange dhtTemperature = new("Температура", "°C", 0, 50, 1,
        p => p.Value, (p, value) => p with { Value = value });
    static readonly InputRange humidity = new("Влажность", "%", 20, 90, 1,
        p => CircuitDht11.Humidity(p), (p, value) => p with { Setting = (value - 20) / 70 });
    static readonly InputRange distance = new("Расстояние до цели", "см", 2, 400, 1,
        p => p.Value, (p, value) => p with { Value = value });
    static readonly InputRange dsTemperature = new("Температура среды", "°C", -55, 125, 0.0625,
        p => p.Value, (p, value) => p with { Value = value });
    static readonly IBrush inputBackground = Brush.Parse("#25354A");
    static readonly IBrush inputActiveBackground = Brush.Parse("#235F4B");
    static readonly IBrush inputRail = Brush.Parse("#9AB7D4");
    static readonly IBrush inputThumb = Brush.Parse("#9AD3FF");
    static readonly Cursor inputCursor = new(StandardCursorType.Hand);

    sealed class InputDrag(string partId, InputRange range, Rect track, IPointer pointer)
    {
        public string PartId { get; } = partId;
        public InputRange Range { get; } = range;
        public Rect Track { get; } = track;
        public IPointer Pointer { get; } = pointer;
        public string GestureId { get; } = Guid.NewGuid().ToString("N");
        public bool Changed { get; set; }
    }

    readonly List<Action<CircuitPart>> updateInspectorInputs = [];
    bool settingInspectorInput;
    InputDrag? inputDrag;

    static InputRange? PrimaryInput(PartKind kind) => kind switch
    {
        PartKind.Pot3 => wiper,
        PartKind.Potentiometer => resistance,
        PartKind.AnalogSensor => voltage,
        PartKind.Dht11 => dhtTemperature,
        PartKind.Hcsr04 => distance,
        PartKind.Ds18b20 => dsTemperature,
        _ => null
    };

    void InitializePartInputs()
    {
        viewport.ContextClicked = OpenPartSettings;
        canvas.PointerMoved += (_, e) =>
        {
            if (inputDrag?.Pointer != e.Pointer) return;
            if (IsEffectivelyEnabled) MoveInput(e.GetPosition(canvas));
            else EndInput();
            e.Handled = true;
        };
        canvas.PointerReleased += (_, e) =>
        {
            if (inputDrag?.Pointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) return;
            if (IsEffectivelyEnabled) MoveInput(e.GetPosition(canvas));
            EndInput();
            e.Handled = true;
        };
        canvas.PointerCaptureLost += (_, e) =>
        {
            if (inputDrag?.Pointer == e.Pointer) EndInput();
        };
    }

    void OpenPartSettings(Control hit)
    {
        if (disposed || !IsEffectivelyEnabled) return;
        foreach (var control in hit.GetSelfAndVisualAncestors().OfType<Control>())
        {
            switch (control.Tag)
            {
                case CircuitPart part:
                    selected = part.Id;
                    selectedWire = null;
                    break;
                case WireLink wire:
                    selected = null;
                    selectedWire = wire.Id;
                    break;
                default:
                    continue;
            }
            inspectorShown = true;
            if (workGrid.Bounds.Width is > 0 and < 1100) partsShown = false;
            Inspect();
            DrawSelection();
            return;
        }
    }

    void DrawPartInputs(Canvas face, CircuitPart part, Size body)
    {
        if (part.Kind == PartKind.Mpu6050) DrawMpu6050Inputs(face, part, body);
        if (PrimaryInput(part.Kind) is { } input)
            DrawSlider(face, part, input, body, part.Kind == PartKind.Dht11 ? 64 : null);
        if (part.Kind == PartKind.Dht11) DrawSlider(face, part, humidity, body);
        if (part.Kind is PartKind.Switch or PartKind.Hcsr04 or PartKind.Ds1307)
        {
            var isSwitch = part.Kind == PartKind.Switch;
            var label = SwitchLabel(part);
            var button = new Border
            {
                Name = "CircuitInputToggle", Width = isSwitch ? 96 : 108, Height = 27,
                CornerRadius = new CornerRadius(6), Cursor = inputCursor,
                Background = part.Active ? inputActiveBackground : inputBackground,
                Child = InputCaption(label)
            };
            button.Child!.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            AutomationProperties.SetName(button, label);
            ToolTip.SetTip(button, "ЛКМ — переключить. ПКМ — свойства.");
            Canvas.SetLeft(button, part.Kind == PartKind.Ds1307 ? body.Width - button.Width - 14 : (body.Width - button.Width) / 2);
            Canvas.SetTop(button, isSwitch ? 57 : 12);
            face.Children.Add(button);
            button.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(button).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
                e.Handled = true;
                _ = Perform(() => ToggleInput(part.Id));
            };
        }
    }

    static TextBlock InputCaption(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
        IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis
    };

    void DrawSlider(Canvas face, CircuitPart part, InputRange range, Size body, double? top = null,
        double? left = null, double? sliderWidth = null)
    {
        const double margin = 10;
        const double height = 29;
        var width = sliderWidth ?? body.Width - 16;
        var x = left ?? (body.Width - width) / 2;
        var y = top ?? body.Height - 54;
        var track = new Rect(x + margin, y, width - margin * 2, height);
        var value = range.Read(part);
        var fraction = Math.Clamp((value - range.Minimum) / (range.Maximum - range.Minimum), 0, 1);
        var caption = range.Display(value);
        var art = new Canvas { IsHitTestVisible = false };
        var label = InputCaption(part.Kind == PartKind.Ds18b20 ? $"{value:0.####} °C" : caption);
        label.Width = width;
        art.Children.Add(label);
        var rail = new Border { Width = track.Width, Height = 3, Background = inputRail, CornerRadius = new CornerRadius(2) };
        Canvas.SetLeft(rail, margin);
        Canvas.SetTop(rail, 21);
        art.Children.Add(rail);
        var thumb = new Ellipse { Width = 10, Height = 10, Fill = inputThumb, Stroke = Brushes.White, StrokeThickness = 1 };
        Canvas.SetLeft(thumb, margin + fraction * track.Width - thumb.Width / 2);
        Canvas.SetTop(thumb, 17.5);
        art.Children.Add(thumb);
        var control = new Border
        {
            Name = "CircuitInputSlider", Width = width, Height = height, Child = art,
            Background = inputBackground, CornerRadius = new CornerRadius(5), Cursor = inputCursor
        };
        AutomationProperties.SetName(control, caption);
        ToolTip.SetTip(control, "Перетащите ползунок. ПКМ — свойства.");
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        face.Children.Add(control);
        control.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(control).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || inputDrag is not null) return;
            e.Handled = true;
            inputDrag = new(part.Id, range, track, e.Pointer);
            // The canvas survives repainting during both dragging and firmware ticks.
            e.Pointer.Capture(canvas);
            MoveInput(e.GetPosition(canvas));
        };
    }

    void MoveInput(Point at)
    {
        if (inputDrag is not { } drag) return;
        _ = Perform(() => ChangeInput(drag.PartId, part =>
        {
            var local = (at - new Vector(part.X * zoom, part.Y * zoom)).Transform(PartArtwork.TransformMatrix(part, zoom).Invert());
            var fraction = Math.Clamp((local.X - drag.Track.X) / drag.Track.Width, 0, 1);
            var value = drag.Range.Minimum + fraction * (drag.Range.Maximum - drag.Range.Minimum);
            return drag.Range.Write(part, Quantize(drag.Range, value));
        }, drag));
    }

    void EndInput()
    {
        if (inputDrag is not { } drag) return;
        inputDrag = null;
        if (drag.Pointer.Captured == canvas) drag.Pointer.Capture(null);
        if (drag.Changed) _ = Perform(() => Save("control-change", gestureId: drag.GestureId));
    }

    static double Quantize(InputRange range, double value) =>
        Math.Clamp(Math.Round(value / range.Step) * range.Step, range.Minimum, range.Maximum);

    void ChangeInput(string id, Func<CircuitPart, CircuitPart> update, InputDrag? drag = null, bool remember = false)
    {
        if (disposed || !IsEffectivelyEnabled) return;
        var index = project.Parts.FindIndex(p => p.Id == id);
        if (index < 0) return;
        var before = project.Parts[index];
        var after = update(before);
        if (after == before) return;
        if (remember || drag is { Changed: false }) CaptureUndo();
        if (drag is not null) drag.Changed = true;
        project.Parts[index] = after;
        RecordHistory("control-change", gestureId: drag?.GestureId);
        result = CircuitSolver.Solve(project, outputs);
        Draw();
        QueueSave();
        if (selected != id) return;
        settingInspectorInput = true;
        try { foreach (var refresh in updateInspectorInputs) refresh(after); }
        finally { settingInspectorInput = false; }
    }

    void AddInspectorSlider(CircuitPart part, InputRange range)
    {
        var label = Ui.Text(range.Display(range.Read(part)), 12);
        var slider = new Slider
        {
            Minimum = range.Minimum, Maximum = range.Maximum, Value = range.Read(part),
            TickFrequency = range.Step, SmallChange = range.Step, LargeChange = (range.Maximum - range.Minimum) / 10
        };
        AutomationProperties.SetName(slider, range.Label);
        properties.Children.Add(label);
        properties.Children.Add(slider);
        updateInspectorInputs.Add(p => { label.Text = range.Display(range.Read(p)); slider.Value = range.Read(p); });
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || settingInspectorInput) return;
            _ = Perform(() => ChangeInput(part.Id, p => range.Write(p, Quantize(range, slider.Value))));
        };
    }

    static string SwitchLabel(CircuitPart part) => part.Kind switch
    {
        PartKind.Hcsr04 => part.Active ? "Цель на месте" : "Цели нет",
        PartKind.Ds1307 => part.Active ? "CR2032 на месте" : "Батареи нет",
        _ => part.Active ? "Замкнута" : "Разомкнута"
    };

    void ToggleInput(string id) => ChangeInput(id, p => p with { Active = !p.Active }, remember: true);

    void AddInspectorToggle(CircuitPart part)
    {
        static string ActionLabel(CircuitPart p) => p.Kind switch
        {
            PartKind.Hcsr04 => p.Active ? "Убрать цель" : "Вернуть цель",
            PartKind.Ds1307 => p.Active ? "Извлечь CR2032" : "Установить CR2032",
            _ => p.Active ? "Разомкнуть" : "Замкнуть"
        };
        var toggle = Ui.Button(ActionLabel(part), () => Perform(() => ToggleInput(part.Id)));
        updateInspectorInputs.Add(p => toggle.Content = ActionLabel(p));
        properties.Children.Add(toggle);
    }
}
