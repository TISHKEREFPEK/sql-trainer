using System.Text.Json.Serialization;

namespace Classroom.Contracts;

public sealed record ElectronicsMotion(double Ax, double Ay, double Az, double Gx, double Gy, double Gz, double Temperature)
{
    public static readonly ElectronicsMotion Rest = new(0, 0, 1, 0, 0, 0, 25);

    [JsonIgnore]
    public bool IsValid =>
        Ax is >= -16 and <= 16 && Ay is >= -16 and <= 16 && Az is >= -16 and <= 16 &&
        Gx is >= -2000 and <= 2000 && Gy is >= -2000 and <= 2000 && Gz is >= -2000 and <= 2000 &&
        Temperature is >= -40 and <= 85;
}
