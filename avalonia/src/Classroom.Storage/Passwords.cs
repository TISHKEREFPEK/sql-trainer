using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;

namespace Classroom.Storage;
public static class Passwords
{
    public static string Hash(string password)
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        return salt + ":" + Convert.ToHexStringLower(Derive(password, salt));
    }

    // Node crypto.scryptSync uses UTF-8 bytes of the hexadecimal salt string, not decoded hex.
    static byte[] Derive(string password, string salt) => SCrypt.Generate(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), 16384, 8, 1, 32);
    public static bool ValidHash(string hash)
    {
        var parts = hash.Split(':');
        return parts.Length == 2 && parts[0].Length == 48 && parts[1].Length == 64 && parts.All(p => p.All(Uri.IsHexDigit));
    }

    public static bool Verify(string password, string hash)
    {
        if (!ValidHash(hash) || password.Length > 1024)
            return false;
        var parts = hash.Split(':');
        return CryptographicOperations.FixedTimeEquals(Derive(password, parts[0]), Convert.FromHexString(parts[1]));
    }
}
