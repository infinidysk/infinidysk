using System;
using System.Security.Cryptography;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Common.Rar;

internal class Rar5CryptoInfo
{
    private Rar5CryptoInfo() { }

    public static Rar5CryptoInfo Create(RarBlockBuffer reader, bool readInitV)
    {
        var cryptoInfo = new Rar5CryptoInfo();
        var cryptVersion = reader.ReadRarVIntUInt32();
        if (cryptVersion > EncryptionConstV5.VERSION)
        {
            throw new CryptographicException($"Unsupported crypto version of {cryptVersion}");
        }
        var encryptionFlags = reader.ReadRarVIntUInt32();
        cryptoInfo.UsePswCheck = FlagUtility.HasFlag(
            encryptionFlags,
            EncryptionFlagsV5.CHFL_CRYPT_PSWCHECK
        );
        cryptoInfo.LG2Count = reader.ReadRarVIntByte(1);

        if (cryptoInfo.LG2Count > EncryptionConstV5.CRYPT5_KDF_LG2_COUNT_MAX)
        {
            throw new CryptographicException($"Unsupported LG2 count of {cryptoInfo.LG2Count}.");
        }

        cryptoInfo.Salt = reader.ReadBytes(EncryptionConstV5.SIZE_SALT50);

        if (readInitV) // File header needs to read IV here
        {
            cryptoInfo.ReadInitV(reader);
        }

        if (cryptoInfo.UsePswCheck)
        {
            cryptoInfo.PswCheck = reader.ReadBytes(EncryptionConstV5.SIZE_PSWCHECK);
            var _pswCheckCsm = reader.ReadBytes(EncryptionConstV5.SIZE_PSWCHECK_CSUM);

            cryptoInfo.UsePswCheck = SHA256
                .HashData(cryptoInfo.PswCheck)
                .AsSpan()
                .StartsWith(_pswCheckCsm.AsSpan());
        }
        return cryptoInfo;
    }

    /// <summary>
    /// Builds a crypto-info instance from already-parsed values (used by the public
    /// key-derivation API to reconstruct the KDF inputs from an <c>IRarCryptoInfo</c>).
    /// </summary>
    internal static Rar5CryptoInfo FromValues(
        int lg2Count,
        byte[] salt,
        byte[] initV,
        bool usePswCheck,
        byte[]? pswCheck
    ) =>
        new()
        {
            LG2Count = lg2Count,
            Salt = salt,
            InitV = initV,
            UsePswCheck = usePswCheck,
            PswCheck = pswCheck ?? [],
        };

    public void ReadInitV(RarBlockBuffer reader) =>
        InitV = reader.ReadBytes(EncryptionConstV5.SIZE_INITV);

    public bool UsePswCheck = false;

    public int LG2Count = 0;

    public byte[] InitV = [];

    public byte[] Salt = [];

    public byte[] PswCheck = [];
}
