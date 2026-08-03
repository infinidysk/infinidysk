using System.Numerics;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Crypto;

/// <summary>
/// Derived AES key material for a RAR entry.
/// </summary>
public sealed class RarDerivedKey
{
    public RarDerivedKey(byte[] key, byte[] iv)
    {
        Key = key;
        Iv = iv;
    }

    /// <summary>AES key (16 bytes for RAR3/RAR4, 32 bytes for RAR5).</summary>
    public byte[] Key { get; }

    /// <summary>AES initialization vector (16 bytes).</summary>
    public byte[] Iv { get; }
}

/// <summary>
/// Public entry point for deriving RAR AES keys from a password without extracting the archive.
/// Reuses the same key schedules as the internal decryptors.
/// </summary>
public static class RarKeyDerivation
{
    /// <summary>
    /// Derives the AES key and IV for an encrypted RAR entry.
    /// <para>
    /// RAR5: PBKDF2-HMAC-SHA256 (UTF-8 password); validates <see cref="IRarCryptoInfo.PasswordCheck"/>
    /// when <see cref="IRarCryptoInfo.UsePasswordCheck"/> is set (throws
    /// <see cref="CryptographicException"/> on mismatch). The IV is
    /// <see cref="IRarCryptoInfo.InitV"/>.
    /// </para>
    /// <para>
    /// RAR3/RAR4: SHA-1 key schedule (UTF-16LE password); the IV is derived by the schedule.
    /// </para>
    /// </summary>
    /// <param name="cryptoInfo">Encryption parameters, typically from <see cref="IRarFileHeader.CryptoInfo"/>.</param>
    /// <param name="password">Archive password.</param>
    /// <returns>The derived <see cref="RarDerivedKey"/>.</returns>
    public static RarDerivedKey DeriveKey(IRarCryptoInfo cryptoInfo, string password)
    {
        if (cryptoInfo.IsRar5)
        {
            var lg2Count = BitOperations.Log2((uint)cryptoInfo.KdfIterations);
            var info = Rar5CryptoInfo.FromValues(
                lg2Count,
                cryptoInfo.Salt,
                cryptoInfo.InitV ?? [],
                cryptoInfo.UsePasswordCheck,
                cryptoInfo.PasswordCheck
            );

            var key = new CryptKey5(password, info);
            // GetAesKey validates the RAR5 password check when present.
            var aesKey = (byte[])key.GetAesKey(info.Salt).Clone();
            return new RarDerivedKey(aesKey, (byte[])info.InitV.Clone());
        }

        var (rar3Key, rar3Iv) = new CryptKey3(password).DeriveKeyAndIv(cryptoInfo.Salt);
        return new RarDerivedKey(rar3Key, rar3Iv);
    }
}
