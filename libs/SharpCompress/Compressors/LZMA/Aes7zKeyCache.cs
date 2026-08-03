using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SharpCompress.Compressors.LZMA;

/// <summary>
/// Caches derived AES keys for 7z folder decryption. Cached material is the derived AES key,
/// which is unavoidable to hold while decrypting; the cache does not extend lifetime beyond
/// what a single extraction session already requires. Passwords are not cached—only keys
/// derived from passwords already retained elsewhere (e.g. ReaderOptions).
/// </summary>
internal static class Aes7zKeyCache
{
    private const int MaxCacheEntries = 32;

    private static readonly ConcurrentDictionary<CacheKey, byte[]> Cache = new();

    public static byte[] DeriveKey(string password, int numCyclesPower, byte[] salt)
    {
        if (numCyclesPower == 0x3F)
        {
            return DeriveKeyNoKdf(salt, Encoding.Unicode.GetBytes(password));
        }

        var cacheKey = new CacheKey(password, numCyclesPower, salt);
        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return (byte[])cached.Clone();
        }

        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var derived = DeriveKeyExpensive(numCyclesPower, salt, passwordBytes);

        if (Cache.Count > MaxCacheEntries)
        {
            Cache.Clear();
        }

        return (byte[])Cache.GetOrAdd(cacheKey, derived).Clone();
    }

    private static byte[] DeriveKeyNoKdf(byte[] salt, byte[] pass)
    {
        var key = new byte[32];

        int pos;
        for (pos = 0; pos < salt.Length; pos++)
        {
            key[pos] = salt[pos];
        }

        for (var i = 0; i < pass.Length && pos < 32; i++)
        {
            key[pos++] = pass[i];
        }

        return key;
    }

    private static byte[] DeriveKeyExpensive(int numCyclesPower, byte[] salt, byte[] pass)
    {
        using var sha = SHA256.Create();
        var counter = new byte[8];
        var numRounds = 1L << numCyclesPower;
        for (long round = 0; round < numRounds; round++)
        {
            sha.TransformBlock(salt, 0, salt.Length, null, 0);
            sha.TransformBlock(pass, 0, pass.Length, null, 0);
            sha.TransformBlock(counter, 0, 8, null, 0);

            // This mirrors the counter so we don't have to convert long to byte[] each round.
            // (It also ensures the counter is little endian, which BitConverter does not.)
            for (var i = 0; i < 8; i++)
            {
                if (++counter[i] != 0)
                {
                    break;
                }
            }
        }

        sha.TransformFinalBlock(counter, 0, 0);
        return sha.Hash!;
    }

    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        private readonly string _password;
        private readonly int _numCyclesPower;
        private readonly byte[] _salt;

        public CacheKey(string password, int numCyclesPower, byte[] salt)
        {
            _password = password;
            _numCyclesPower = numCyclesPower;
            _salt = (byte[])salt.Clone();
        }

        public bool Equals(CacheKey other)
        {
            if (_numCyclesPower != other._numCyclesPower)
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
            hash.Add(_numCyclesPower);
            hash.AddBytes(_salt);
            return hash.ToHashCode();
        }
    }
}
