using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class Aes7zKeyCacheTests : TestBase
{
    private const string EncryptedArchive = "7Zip.LZMA.Aes.7z";
    private const string Password = "testpassword";

    [Fact]
    public void SevenZip_LZMAAES_ExtractTwice_ProducesIdenticalOutput()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, EncryptedArchive);
        var options = ReaderOptions.ForFilePath with { Password = Password };

        var first = ExtractAllEntryBytes(path, options);
        var second = ExtractAllEntryBytes(path, options);

        Assert.Equal(first.Keys, second.Keys);
        foreach (var key in first.Keys)
        {
            Assert.Equal(first[key], second[key]);
        }
    }

    [Fact]
    public void SevenZip_LZMAAES_WrongPassword_DoesNotContaminateCorrectExtraction()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, EncryptedArchive);
        var correctOptions = ReaderOptions.ForFilePath with { Password = Password };
        var wrongOptions = ReaderOptions.ForFilePath with { Password = "wrongpassword" };

        var expected = ExtractAllEntryBytes(path, correctOptions);
        Assert.Throws<DataErrorException>(() => ExtractAllEntryBytes(path, wrongOptions));
        var actual = ExtractAllEntryBytes(path, correctOptions);

        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var key in expected.Keys)
        {
            Assert.Equal(expected[key], actual[key]);
        }
    }

    [Fact]
    public void DeriveKey_MatchesLegacyAlgorithm()
    {
        const string password = "testpassword";
        const int numCyclesPower = 19;
        var salt = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var expected = DeriveKeyLegacy(password, numCyclesPower, salt);
        var actual = Aes7zKeyCache.DeriveKey(password, numCyclesPower, salt);

        Assert.Equal(expected, actual);

        var cached = Aes7zKeyCache.DeriveKey(password, numCyclesPower, salt);
        Assert.Equal(expected, cached);

        cached[0] ^= 0xFF;
        var again = Aes7zKeyCache.DeriveKey(password, numCyclesPower, salt);
        Assert.Equal(expected, again);
    }

    [Fact]
    public void DeriveKey_DifferentPasswords_DoNotCrossContaminate()
    {
        const int numCyclesPower = 19;
        var salt = new byte[] { 0x0A, 0x0B, 0x0C };

        var keyA = Aes7zKeyCache.DeriveKey("password-a", numCyclesPower, salt);
        var keyB = Aes7zKeyCache.DeriveKey("password-b", numCyclesPower, salt);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void DeriveKey_NoKdfShortCircuit_MatchesLegacyAlgorithm()
    {
        const string password = "testpassword";
        const int numCyclesPower = 0x3F;
        var salt = new byte[] { 0x11, 0x22, 0x33 };

        var expected = DeriveKeyLegacy(password, numCyclesPower, salt);
        var actual = Aes7zKeyCache.DeriveKey(password, numCyclesPower, salt);

        Assert.Equal(expected, actual);
    }

    private static Dictionary<string, byte[]> ExtractAllEntryBytes(
        string path,
        ReaderOptions options
    )
    {
        var results = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = SevenZipArchive.OpenArchive(path, options);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            using var entryStream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            results[entry.Key!] = ms.ToArray();
        }

        return results;
    }

    private static byte[] DeriveKeyLegacy(string password, int numCyclesPower, byte[] salt)
    {
        var pass = Encoding.Unicode.GetBytes(password);
        if (numCyclesPower == 0x3F)
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

        using var sha = SHA256.Create();
        var counter = new byte[8];
        var numRounds = 1L << numCyclesPower;
        for (long round = 0; round < numRounds; round++)
        {
            sha.TransformBlock(salt, 0, salt.Length, null, 0);
            sha.TransformBlock(pass, 0, pass.Length, null, 0);
            sha.TransformBlock(counter, 0, 8, null, 0);

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
}
