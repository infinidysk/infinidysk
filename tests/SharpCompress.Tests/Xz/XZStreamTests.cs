using System;
using System.IO;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;
using SharpCompress.IO;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Xz;

public class XzStreamTests : XzTestsBase
{
    [Fact]
    public void CanReadEmptyStream()
    {
        var xz = new XZStream(CompressedEmptyStream);
        using var sr = new StreamReader(xz);
        var uncompressed = sr.ReadToEnd();
        Assert.Equal(OriginalEmpty, uncompressed);
    }

    [Fact]
    public void CanReadStream()
    {
        var xz = new XZStream(CompressedStream);
        using var sr = new StreamReader(xz);
        var uncompressed = sr.ReadToEnd();
        Assert.Equal(Original, uncompressed);
    }

    [Fact]
    public void CanReadIndexedStream()
    {
        var xz = new XZStream(CompressedIndexedStream);
        using var sr = new StreamReader(xz);
        var uncompressed = sr.ReadToEnd();
        Assert.Equal(OriginalIndexed, uncompressed);
    }

    [Fact]
    public void CanReadNonSeekableStream()
    {
        var nonSeekable = new ForwardOnlyStream(new MemoryStream(Compressed));
        var xz = new XZStream(SharpCompressStream.Create(nonSeekable));
        using var sr = new StreamReader(xz);
        var uncompressed = sr.ReadToEnd();
        Assert.Equal(Original, uncompressed);
    }

    [Fact]
    public void CanReadNonSeekableEmptyStream()
    {
        var nonSeekable = new ForwardOnlyStream(new MemoryStream(CompressedEmpty));
        var xz = new XZStream(SharpCompressStream.Create(nonSeekable));
        using var sr = new StreamReader(xz);
        var uncompressed = sr.ReadToEnd();
        Assert.Equal(OriginalEmpty, uncompressed);
    }

    [Fact]
    public void Throws_On_Corrupt_Block_Check()
    {
        var compressed = (byte[])Compressed.Clone();
        compressed[compressed.Length - 29] ^= 1;
        using var xz = new XZStream(new MemoryStream(compressed));
        using var output = new MemoryStream();

        Assert.Throws<InvalidFormatException>(() => xz.CopyTo(output));
    }

    [Fact]
    public void Throws_On_Corrupt_Index_Crc()
    {
        var compressed = (byte[])Compressed.Clone();
        // Index CRC is the 4 bytes immediately before the 12-byte footer.
        compressed[compressed.Length - 13] ^= 1;
        using var xz = new XZStream(new MemoryStream(compressed));
        using var output = new MemoryStream();

        Assert.Throws<InvalidFormatException>(() => xz.CopyTo(output));
    }

    [Fact]
    public void Throws_On_Corrupt_Index_Record_With_Valid_Crc()
    {
        var compressed = (byte[])Compressed.Clone();
        var indexStart = compressed.Length - 24;
        // Corrupt the unpadded-size vint (first byte after marker + record count).
        compressed[indexStart + 2] ^= 1;
        RewriteIndexCrc(compressed, indexStart);

        using var xz = new XZStream(new MemoryStream(compressed));
        using var output = new MemoryStream();

        Assert.Throws<InvalidFormatException>(() => xz.CopyTo(output));
    }

    [Fact]
    public void Throws_On_Corrupt_Footer_Backward_Size()
    {
        var compressed = (byte[])Compressed.Clone();
        var footerStart = compressed.Length - 12;
        // Corrupt Backward Size (bytes 4..7 of the footer) and recompute the footer CRC.
        compressed[footerStart + 4] ^= 1;
        RewriteFooterCrc(compressed, footerStart);

        using var xz = new XZStream(new MemoryStream(compressed));
        using var output = new MemoryStream();

        Assert.Throws<InvalidFormatException>(() => xz.CopyTo(output));
    }

    private static void RewriteIndexCrc(byte[] compressed, int indexStart)
    {
        var indexBodyLength = 8; // marker + records + padding, excluding CRC
        var crc = Crc32.Compute(compressed.AsSpan(indexStart, indexBodyLength).ToArray());
        BitConverter.TryWriteBytes(compressed.AsSpan(indexStart + indexBodyLength, 4), crc);
    }

    private static void RewriteFooterCrc(byte[] compressed, int footerStart)
    {
        var crc = Crc32.Compute(compressed.AsSpan(footerStart + 4, 6).ToArray());
        BitConverter.TryWriteBytes(compressed.AsSpan(footerStart, 4), crc);
    }
}
