using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Services.Plex;

public sealed class PlexOwnerAuthenticator(string sharedKey, TimeProvider clock)
{
    public const string HeaderName = "X-InfiniDysk-Plex-Owner";

    public string Validate(string? proof)
    {
        if (string.IsNullOrEmpty(sharedKey) || proof is null || proof.Length > 160)
            throw Denied();
        var parts = proof.Split('.');
        if (parts.Length != 3 || parts[0].Length != 64 || parts[2].Length != 64 ||
            !parts[0].All(Uri.IsHexDigit) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry))
            throw Denied();
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (expiry <= now || expiry > now + 300) throw Denied();
        byte[] signature;
        try { signature = Convert.FromHexString(parts[2]); }
        catch (FormatException) { throw Denied(); }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(sharedKey),
            Encoding.UTF8.GetBytes($"plex-owner-v1\n{parts[0]}\n{parts[1]}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, signature)) throw Denied();
        return parts[0];
    }

    private static UnauthorizedAccessException Denied() => new("A verified administrator session is required for Plex operations.");
}
