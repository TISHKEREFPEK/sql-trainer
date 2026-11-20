using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

internal static class CircuitMpu6050
{
    public const string ModelId = "mpu6050-i2c-3v3";
    public const string Name = "MPU-6050 · ускорение и вращение";
    public static readonly string[] Pins = ["VDD", "GND", "SCL", "SDA", "AD0"];
    public static ElectronicsMotion Motion(CircuitPart part) => part.Motion ?? ElectronicsMotion.Rest;

    public static int Address(CircuitPart part, CircuitResult circuit)
    {
        var rail = circuit.Voltages[new(part.Id, "VDD")];
        var ad0 = circuit.Voltages[new(part.Id, "AD0")];
        if (ad0 is < -0.1 or > 3.6 || ad0 > rail + 0.15) return -1;
        if (ad0 <= 0.3 * rail) return 0x68;
        return ad0 >= 0.7 * rail ? 0x69 : -1;
    }

    public static bool SignalIsValid(CircuitPart part, Terminal pin, CircuitConnections nets, CircuitResult circuit,
        Terminal ground, IReadOnlySet<Terminal> supplies) =>
        !nets.Same(pin, ground) && !supplies.Contains(nets.Root(pin)) &&
        circuit.Voltages[pin] is >= 2.475 and <= 3.6 && !nets.Same(pin, new(part.Id, "VDD"));
}
