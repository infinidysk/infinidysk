using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Common.Rar;

internal class CryptKey5 : ICryptKey
{
    const int AES_256 = 256;
    const int SHA256_DIGEST_SIZE = 32;

    // Cache holds passwords already retained by ReaderOptions; bound like unrar's 4-entry KDF cache.
    private const int MaxCacheEntries = 16;

    private readonly string _password;
    private readonly Rar5CryptoInfo _cryptoInfo;
    private byte[] _pswCheck = [];
    private byte[] _hashKey = [];

    private static readonly ConcurrentDictionary<CacheKey, DerivedKeys> s_derivedKeyCache = new();

    public CryptKey5(string? password, Rar5CryptoInfo rar5CryptoInfo)
    {
        _password = password ?? "";
        _cryptoInfo = rar5CryptoInfo;
    }

    public byte[] PswCheck => _pswCheck;

    public byte[] HashKey => _hashKey;

    private readonly struct DerivedKeys
    {
        public readonly byte[] AesKey;
        public readonly byte[] HashKey;
        public readonly byte[] PswCheckValue;

        public DerivedKeys(byte[] aesKey, byte[] hashKey, byte[] pswCheckValue)
        {
            AesKey = aesKey;
            HashKey = hashKey;
            PswCheckValue = pswCheckValue;
        }
    }

    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        private readonly string _password;
        private readonly int _lg2Count;
        private readonly byte[] _salt;

        public CacheKey(string password, int lg2Count, byte[] salt)
        {
            _password = password;
            _lg2Count = lg2Count;
            _salt = (byte[])salt.Clone();
        }

        public bool Equals(CacheKey other)
        {
            if (_lg2Count != other._lg2Count)
            {
                return false;
            }

            if (!string.Equals(_password, other._password, StringComparison.Ordinal))
            {
                return false;
            }

            return _salt.AsSpan().SequenceEqual(other._salt);
        }

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_password, StringComparer.Ordinal);
            hash.Add(_lg2Count);
            hash.AddBytes(_salt);
            return hash.ToHashCode();
        }
    }

    private static DerivedKeys DeriveKeys(
        string password,
        ReadOnlySpan<byte> saltRar5,
        int lg2Count
    )
    {
        var iterations = 1 << lg2Count;
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var block = HMACSHA256.HashData(passwordBytes, saltRar5);
        var finalHash = (byte[])block.Clone();

        Span<int> loop = [iterations, 17, 17];
        var aesKey = new byte[SHA256_DIGEST_SIZE];
        var hashKey = new byte[SHA256_DIGEST_SIZE];
        var pswCheckValue = new byte[SHA256_DIGEST_SIZE];

        try
        {
            for (var x = 0; x < 3; x++)
            {
                for (var i = 1; i < loop[x]; i++)
                {
                    var nextBlock = HMACSHA256.HashData(passwordBytes, block);
                    CryptographicOperations.ZeroMemory(block);
                    block = nextBlock;
                    for (var j = 0; j < finalHash.Length; j++)
                    {
                        finalHash[j] ^= block[j];
                    }
                }

                var target = x switch
                {
                    0 => aesKey,
                    1 => hashKey,
                    _ => pswCheckValue,
                };
                finalHash.AsSpan().CopyTo(target);
            }

            return new DerivedKeys(aesKey, hashKey, pswCheckValue);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(block);
            CryptographicOperations.ZeroMemory(finalHash);
        }
    }

    private static DerivedKeys GetOrDeriveKeys(string password, byte[] salt, int lg2Count)
    {
        var key = new CacheKey(password, lg2Count, salt);
        if (s_derivedKeyCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Span<byte> saltRar5 = stackalloc byte[salt.Length + 4];
        salt.CopyTo(saltRar5);
        saltRar5[salt.Length + 3] = 1;

        var derived = DeriveKeys(password, saltRar5, lg2Count);

        // Evict all when bound exceeded; archives rarely use more than a handful of salts.
        if (s_derivedKeyCache.Count >= MaxCacheEntries)
        {
            s_derivedKeyCache.Clear();
        }

        return s_derivedKeyCache.GetOrAdd(key, derived);
    }

    public ICryptoTransform Transformer(byte[] salt)
    {
        var derived = GetOrDeriveKeys(_password, salt, _cryptoInfo.LG2Count);
        ValidatePasswordCheck(derived.PswCheckValue);
        return CreateDecryptor(derived.AesKey, _cryptoInfo.InitV);
    }

    /// <summary>
    /// Returns the derived AES-256 key after password-check validation.
    /// Used by seekable stored-entry decryption where IV varies per block/part.
    /// </summary>
    internal byte[] GetAesKey(byte[] salt)
    {
        var derived = GetOrDeriveKeys(_password, salt, _cryptoInfo.LG2Count);
        ValidatePasswordCheck(derived.PswCheckValue);
        _hashKey = derived.HashKey;
        return derived.AesKey;
    }

    private void ValidatePasswordCheck(byte[] pswCheckValue)
    {
        _pswCheck = new byte[EncryptionConstV5.SIZE_PSWCHECK];
        for (var i = 0; i < SHA256_DIGEST_SIZE; i++)
        {
            _pswCheck[i % EncryptionConstV5.SIZE_PSWCHECK] ^= pswCheckValue[i];
        }

        if (
            _cryptoInfo.UsePswCheck
            && !CryptographicOperations.FixedTimeEquals(_cryptoInfo.PswCheck, _pswCheck)
        )
        {
            throw new CryptographicException("The password did not match.");
        }
    }

    private static ICryptoTransform CreateDecryptor(byte[] aesKey, byte[] iv)
    {
        var aes = Aes.Create();
        aes.KeySize = AES_256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = aesKey;
        aes.IV = iv;
        return aes.CreateDecryptor();
    }
}
