using System.Text.Json.Serialization;

namespace Classroom.Contracts;

public sealed record ElectronicsJoystick(double X, double Y)
{
    public static readonly ElectronicsJoystick Center = new(0.5, 0.5);

    [JsonIgnore]
    public bool IsValid => X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}
