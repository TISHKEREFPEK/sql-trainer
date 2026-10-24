using System.Security.Cryptography;
using System.Text;

namespace Classroom.Contracts;

// The displayed code binds a LAN announcement to a certificate and this server run.
// It selects a server, never authenticates a pupil or grants a role.
public record PairingAnnouncement(int Port, string Fingerprint, string Nonce);

public static class PairingCode
{
    public const int DiscoveryPort = 47832;
    public const string MulticastAddress = "239.255.47.83";
    public const string Protocol = "SQL-Classroom/1 ";
    const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public static string Normalize(string input)
    {
        var value = input.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        if (value.Length != 12 || value.Any(c => !Alphabet.Contains(c)))
            throw new InvalidDataException("Введите код преподавателя: три группы по четыре символа.");
        return value;
    }

    public static string Create(PairingAnnouncement announcement)
    {
        if (announcement.Port is < 1024 or > 65535 || announcement.Fingerprint is not { Length: 64 } ||
            !announcement.Fingerprint.All(Uri.IsHexDigit) || announcement.Nonce is not { Length: 64 } || !announcement.Nonce.All(Uri.IsHexDigit))
            throw new InvalidDataException("Некорректные данные подключения.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Protocol}{announcement.Port}:{announcement.Fingerprint.ToUpperInvariant()}:{announcement.Nonce.ToUpperInvariant()}"));
        // 60 bits; no ambiguous 0, 1, I, O. Each character encodes five bits.
        var bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash);
        return new string(Enumerable.Range(0, 12).Select(i => Alphabet[(int)((bits >> (59 - i * 5)) & 31)]).ToArray());
    }

    public static string Format(string code) => $"{code[..4]}-{code[4..8]}-{code[8..]}";
}

// The active stand is supplied after successful authentication. Task routing is reserved.
public record LessonDestination(string StandId, string? TaskId = null);
