using Classroom.Contracts;
using Xunit;

namespace Classroom.Tests;

public sealed class PairingTests
{
    [Fact]
    public void CodeBindsCertificatePortAndServerRunAndAcceptsBoardFormatting()
    {
        var announcement = new PairingAnnouncement(47831, new string('A', 64), new string('B', 64));
        var code = PairingCode.Create(announcement);
        Assert.Equal(12, code.Length);
        Assert.Equal(code, PairingCode.Normalize(PairingCode.Format(code).ToLowerInvariant()));
        Assert.NotEqual(code, PairingCode.Create(announcement with { Fingerprint = new string('C', 64) }));
        Assert.NotEqual(code, PairingCode.Create(announcement with { Port = 47833 }));
        Assert.NotEqual(code, PairingCode.Create(announcement with { Nonce = new string('D', 64) }));
        Assert.Throws<InvalidDataException>(() => PairingCode.Normalize("1234"));
        Assert.Throws<InvalidDataException>(() => PairingCode.Create(announcement with { Nonce = null! }));
    }
}
