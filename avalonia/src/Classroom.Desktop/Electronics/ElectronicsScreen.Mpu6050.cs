using Avalonia;
using Avalonia.Controls;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    static readonly InputRange[] mpuAxes =
    [
        MotionRange("Ax", "g", 16, 0.25, m => m.Ax, (m, value) => m with { Ax = value }),
        MotionRange("Ay", "g", 16, 0.25, m => m.Ay, (m, value) => m with { Ay = value }),
        MotionRange("Az", "g", 16, 0.25, m => m.Az, (m, value) => m with { Az = value }),
        MotionRange("Gx", "°/с", 2000, 5, m => m.Gx, (m, value) => m with { Gx = value }),
        MotionRange("Gy", "°/с", 2000, 5, m => m.Gy, (m, value) => m with { Gy = value }),
        MotionRange("Gz", "°/с", 2000, 5, m => m.Gz, (m, value) => m with { Gz = value })
    ];
    static readonly InputRange mpuTemperature = new("Температура", "°C", -40, 85, 0.5,
        part => CircuitMpu6050.Motion(part).Temperature,
        (part, value) => part with { Motion = CircuitMpu6050.Motion(part) with { Temperature = value } });

    static InputRange MotionRange(string label, string unit, double limit, double step,
        Func<ElectronicsMotion, double> read, Func<ElectronicsMotion, double, ElectronicsMotion> write) =>
        new(label, unit, -limit, limit, step, part => read(CircuitMpu6050.Motion(part)),
            (part, value) => part with { Motion = write(CircuitMpu6050.Motion(part), value) });

    void DrawMpu6050Inputs(Canvas face, CircuitPart part, Size size)
    {
        for (var i = 0; i < mpuAxes.Length; i++)
            DrawSlider(face, part, mpuAxes[i], size, top: 14 + i % 3 * 35,
                left: i < 3 ? 8 : 157, sliderWidth: 141);
    }

    void InspectMpu6050(CircuitPart part)
    {
        properties.Children.Add(Ui.Muted("VDD — 3,3 В, GND — общая земля. AD0 на GND или без провода — 0x68, на 3,3 В — 0x69. Для Uno и Mega подключите SDA/SCL через преобразователь 5 → 3,3 В.", 12));
        var circuit = result ?? CircuitSolver.Solve(project);
        var address = CircuitMpu6050.Address(part, circuit);
        var supply = circuit.Voltages[new(part.Id, "VDD")] - circuit.Voltages[new(part.Id, "GND")];
        properties.Children.Add(Ui.Muted(supply is < 3.0 or > 3.45 ? "Проверьте питание VDD."
            : address < 0 ? "Проверьте уровень AD0." : $"Адрес I²C — 0x{address:X2}.", 12));
        foreach (var axis in mpuAxes) AddInspectorSlider(part, axis);
        AddInspectorSlider(part, mpuTemperature);
    }
}
