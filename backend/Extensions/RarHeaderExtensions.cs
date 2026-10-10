using NzbWebDAV.Models;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Crypto;

namespace NzbWebDAV.Extensions;

public static class RarHeaderExtensions
{
    public static AesParams? GetAesParams(this IRarFileHeader header, string? password)
    {
        if (password is null || header.CryptoInfo is null) return null;

        var derived = RarKeyDerivation.DeriveKey(header.CryptoInfo, password);
        return new AesParams
        {
            Key = derived.Key,
            Iv = derived.Iv,
            // Never persist SharpCompress's unknown-size unpack sentinel as a real length.
            DecodedSize = header.IsUncompressedSizeUnknown ? 0 : header.UncompressedSize,
        };
    }

    /// <summary>
    /// True only when the supplied password has actually been checked against the
    /// archive: RAR5 headers that carry a password-check value, for which
    /// <see cref="GetAesParams"/> throws on a mismatch. RAR3/RAR4 have no check
    /// value, so a derived key there proves nothing about the password.
    /// </summary>
    public static bool IsPasswordVerified(this IRarFileHeader header, string? password) =>
        header.IsEncrypted
        && password is not null
        && header.CryptoInfo is { IsRar5: true, UsePasswordCheck: true };
}
