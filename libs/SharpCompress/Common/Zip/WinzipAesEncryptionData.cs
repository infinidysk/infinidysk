using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace SharpCompress.Common.Zip;

[SuppressMessage(
    "Security",
    "CA5379:Rfc2898DeriveBytes might be using a weak hash algorithm",
    Justification = "WinZip AES specification requires PBKDF2 with SHA-1."
)]
internal class WinzipAesEncryptionData
{
    private const int RFC2898_ITERATIONS = 1000;

    private readonly WinzipAesKeySize _keySize;

    internal WinzipAesEncryptionData(
        WinzipAesKeySize keySize,
        byte[] salt,
        byte[] passwordVerifyValue,
        string password
    )
    {
        _keySize = keySize;

        var derivedKeySize = (KeySizeInBytes * 2) + 2;
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes,
            salt,
            RFC2898_ITERATIONS,
            HashAlgorithmName.SHA1,
            derivedKeySize
        );
        KeyBytes = derivedKey.AsSpan(0, KeySizeInBytes).ToArray();
        IvBytes = derivedKey.AsSpan(KeySizeInBytes, KeySizeInBytes).ToArray();
        var generatedVerifyValue = derivedKey.AsSpan((KeySizeInBytes * 2), 2).ToArray();

        var verify = BinaryPrimitives.ReadInt16LittleEndian(passwordVerifyValue);
        var generated = BinaryPrimitives.ReadInt16LittleEndian(generatedVerifyValue);
        if (verify != generated)
        {
            throw new InvalidFormatException("bad password");
        }
    }

    internal byte[] IvBytes { get; set; }

    internal byte[] KeyBytes { get; set; }

    private int KeySizeInBytes => KeyLengthInBytes(_keySize);

    internal static int KeyLengthInBytes(WinzipAesKeySize keySize) =>
        keySize switch
        {
            WinzipAesKeySize.KeySize128 => 16,
            WinzipAesKeySize.KeySize192 => 24,
            WinzipAesKeySize.KeySize256 => 32,
            _ => throw new ArchiveOperationException(),
        };
}
