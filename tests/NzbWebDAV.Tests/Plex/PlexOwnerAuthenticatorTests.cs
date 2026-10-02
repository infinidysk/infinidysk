using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Plex;

public sealed class PlexOwnerAuthenticatorTests
{
    [Fact]
    public void OwnerProof_RequiresSignatureAndShortExpiry()
    {
        var clock = new PlexTestClock();
        var authenticator = new PlexOwnerAuthenticator("shared-key", clock);
        var owner = new string('a', 64);
        var expiry = clock.Now.AddMinutes(2).ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("shared-key"),
            Encoding.UTF8.GetBytes($"plex-owner-v1\n{owner}\n{expiry}")));
        var proof = $"{owner}.{expiry}.{signature}";
        Assert.Equal(owner, authenticator.Validate(proof));
        Assert.Throws<UnauthorizedAccessException>(() => authenticator.Validate(proof + "x"));
        Assert.Throws<UnauthorizedAccessException>(() => authenticator.Validate("browser-owner"));
        clock.Now = clock.Now.AddMinutes(3);
        Assert.Throws<UnauthorizedAccessException>(() => authenticator.Validate(proof));
    }
}
