using System;
using System.IO;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;
using SharpCompress.IO;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Xz;

public class XzStreamAsyncTests : XzTestsBase
{
    [Fact]
    public async ValueTask CanReadEmptyStreamAsync()
    {
        using var xz = new XZStream(CompressedEmptyStream);
        using var sr = new StreamReader(new AsyncOnlyStream(xz));
        var uncompressed = await sr.ReadToEndAsync().ConfigureAwait(false);
        Assert.Equal(OriginalEmpty, uncompressed);
    }

    [Fact]
    public async ValueTask CanReadStreamAsync()
    {
        using var xz = new XZStream(CompressedStream);
        using var sr = new StreamReader(new AsyncOnlyStream(xz));
        var uncompressed = await sr.ReadToEndAsync().ConfigureAwait(false);
        Assert.Equal(Original, uncompressed);
    }

    [Fact]
    public async ValueTask CanReadIndexedStreamAsync()
    {
        using var xz = new XZStream(CompressedIndexedStream);
        using var sr = new StreamReader(new AsyncOnlyStream(xz));
        var uncompressed = await sr.ReadToEndAsync().ConfigureAwait(false);
        Assert.Equal(OriginalIndexed, uncompressed);
    }

    [Fact]
    public async ValueTask CanReadNonSeekableStreamAsync()
    {
        var nonSeekable = new ForwardOnlyStream(new MemoryStream(Compressed));
        var xz = new XZStream(SharpCompressStream.Create(nonSeekable));
        using var sr = new StreamReader(new AsyncOnlyStream(xz));
        var uncompressed = await sr.ReadToEndAsync().ConfigureAwait(false);
        Assert.Equal(Original, uncompressed);
    }

    [Fact]
    public async ValueTask CanReadNonSeekableEmptyStreamAsync()
    {
        var nonSeekable = new ForwardOnlyStream(new MemoryStream(CompressedEmpty));
        var xz = new XZStream(SharpCompressStream.Create(nonSeekable));
        using var sr = new StreamReader(new AsyncOnlyStream(xz));
        var uncompressed = await sr.ReadToEndAsync().ConfigureAwait(false);
        Assert.Equal(OriginalEmpty, uncompressed);
    }

    [Fact]
    public async ValueTask Throws_On_Corrupt_Index_Crc_Async()
    {
        var compressed = (byte[])Compressed.Clone();
        compressed[compressed.Length - 13] ^= 1;
        using var xz = new XZStream(new MemoryStream(compressed));
        using var output = new MemoryStream();

        await Assert
            .ThrowsAsync<InvalidFormatException>(async () =>
                await xz.CopyToAsync(output).ConfigureAwait(false)
            )
            .ConfigureAwait(false);
    }

    [Fact]
    public async ValueTask Throws_On_Corrupt_Index_Record_With_Valid_Crc_Async()
    {
        var compressed = (byte[])Compressed.Clone();
        var indexStart = compressed.Length - 24;
        compressed[indexStart + 2] ^= 1;
        RewriteIndexCrc(compressed, indexStart);

        using var xz = new XZStream(new MemoryStream(compressed));
        using var output = new MemoryStream();

        await Assert
            .ThrowsAsync<InvalidFormatException>(async () =>
                await xz.CopyToAsync(output).ConfigureAwait(false)
            )
            .ConfigureAwait(false);
    }

    [Fact]
    public async ValueTask Throws_On_Corrupt_Footer_Backward_Size_Async()
    {
        var compressed = (byte[])Compressed.Clone();
        var footerStart = compressed.Length - 12;
        compressed[footerStart + 4] ^= 1;
        RewriteFooterCrc(compressed, footerStart);

        using var xz = new XZStream(new MemoryStream(compressed));
        using var output = new MemoryStream();

        await Assert
            .ThrowsAsync<InvalidFormatException>(async () =>
                await xz.CopyToAsync(output).ConfigureAwait(false)
            )
            .ConfigureAwait(false);
    }

    private static void RewriteIndexCrc(byte[] compressed, int indexStart)
    {
        var indexBodyLength = 8;
        var crc = Crc32.Compute(compressed.AsSpan(indexStart, indexBodyLength).ToArray());
        BitConverter.TryWriteBytes(compressed.AsSpan(indexStart + indexBodyLength, 4), crc);
    }

    private static void RewriteFooterCrc(byte[] compressed, int footerStart)
    {
        var crc = Crc32.Compute(compressed.AsSpan(footerStart + 4, 6).ToArray());
        BitConverter.TryWriteBytes(compressed.AsSpan(footerStart, 4), crc);
    }
}
