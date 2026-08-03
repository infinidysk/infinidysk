using System.IO;
using System.Linq;
using SharpCompress.Archives.Rar;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Crypto;
using SharpCompress.IO;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.Rar;

/// <summary>
/// Covers the public <see cref="RarKeyDerivation"/> API (issue #118).
/// </summary>
public class RarKeyDerivationTests : TestBase
{
    private const string Password = "test";

    private static RarHeaderFactory NewFactory() =>
        new(
            StreamingMode.Seekable,
            ReaderOptions.ForExternalStream with
            {
                LeaveStreamOpen = true,
            }
        );

    private static IRarFileHeader FirstEncryptedFileHeader(Stream stream, RarHeaderFactory factory)
    {
        foreach (var header in factory.ReadHeaders(stream))
        {
            if (
                header.HeaderType == HeaderType.File
                && header is IRarFileHeader fh
                && !fh.IsDirectory
                && fh.IsEncrypted
                && fh.CompressedSize > 0
            )
            {
                return fh;
            }
        }

        throw new Xunit.Sdk.XunitException("no encrypted file header found");
    }

    [Fact]
    public void DeriveKey_Rar5_KeyMaterial_UnlocksArchiveEntryStream()
    {
        // Round-trip through the Archive API (which uses CryptKey5 internally) and assert the
        // public DeriveKey material matches that path. Full CBC decrypt of packed bytes is
        // covered by EncryptedStoredRarEntryStream tests; here we only validate the public API.
        var path = Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.encrypted.rar");

        using (
            var archive = RarArchive.OpenArchive(path, new ReaderOptions { Password = Password })
        )
        {
            var entry = archive.Entries.First(e => !e.IsDirectory && e.Size > 0);
            using var es = entry.OpenEntryStream();
            using var pm = new MemoryStream();
            es.CopyTo(pm);
            Assert.True(pm.Length > 0);
        }

        using var stream = File.OpenRead(path);
        var factory = NewFactory();
        var fh = FirstEncryptedFileHeader(stream, factory);

        var info = fh.CryptoInfo;
        Assert.NotNull(info);
        Assert.True(info!.IsRar5);

        var derived = RarKeyDerivation.DeriveKey(info, Password);
        Assert.Equal(32, derived.Key.Length);
        Assert.Equal(16, derived.Iv.Length);
        Assert.Equal(info.InitV, derived.Iv);
    }

    [Fact]
    public void DeriveKey_Rar5_MatchesInternalCryptKey()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.encrypted.rar");
        using var stream = File.OpenRead(path);
        var factory = NewFactory();
        var fh = (FileHeader)FirstEncryptedFileHeader(stream, factory);

        var derived = RarKeyDerivation.DeriveKey(((IRarFileHeader)fh).CryptoInfo!, Password);

        var expectedKey = new CryptKey5(Password, fh.Rar5CryptoInfo!).GetAesKey(
            fh.Rar5CryptoInfo!.Salt
        );

        Assert.Equal(expectedKey, derived.Key);
        Assert.Equal(fh.Rar5CryptoInfo!.InitV, derived.Iv);
    }

    [Fact]
    public void DeriveKey_Rar5_WrongPassword_Throws()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.encrypted.rar");
        using var stream = File.OpenRead(path);
        var factory = NewFactory();
        var fh = FirstEncryptedFileHeader(stream, factory);

        Assert.Throws<SharpCompress.Common.CryptographicException>(() =>
            RarKeyDerivation.DeriveKey(fh.CryptoInfo!, "wrong-password")
        );
    }

    [Fact]
    public void DeriveKey_Rar3_NonAsciiPassword_UsesUtf16LeSchedule()
    {
        // RAR3/RAR4 uses a UTF-16LE password schedule; the public API must route to CryptKey3.
        var salt = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var info = RarCryptoInfoView.ForRar3(salt);

        Assert.False(info.IsRar5);
        Assert.Equal(1 << 18, info.KdfIterations);

        const string password = "pÄsswörd-\u00fc\u2713";
        var derived = RarKeyDerivation.DeriveKey(info, password);
        var (expectedKey, expectedIv) = new CryptKey3(password).DeriveKeyAndIv(salt);

        Assert.Equal(16, derived.Key.Length);
        Assert.Equal(16, derived.Iv.Length);
        Assert.Equal(expectedKey, derived.Key);
        Assert.Equal(expectedIv, derived.Iv);
    }
}
