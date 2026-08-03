using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.SevenZip;

/// <summary>
/// Characterizes async LZMA / 7z decode paths for issue #24 (odd buffer sizes + AsyncOnlyStream).
/// </summary>
public class SevenZipAsyncStressTests : TestBase
{
    private static readonly int[] BufferSizes = [1, 7, 4096, 81920];

    [Theory]
    [InlineData(4 * 1024 * 1024)]
    [InlineData(8 * 1024 * 1024)]
    public async Task RawLzma_AsyncRoundTrip_OddBuffers(int size)
    {
        var original = CreateDeterministicPayload(size, seed: 24);
        foreach (var bufferSize in BufferSizes)
        {
            await RoundTripRawLzmaAsync(original, bufferSize).ConfigureAwait(false);
        }
    }

    [Fact]
    [Trait("format", "stress")]
    public async Task RawLzma_AsyncRoundTrip_64MB_OddBuffers()
    {
        var original = CreateDeterministicPayload(64 * 1024 * 1024, seed: 24);
        foreach (var bufferSize in BufferSizes)
        {
            await RoundTripRawLzmaAsync(original, bufferSize).ConfigureAwait(false);
        }
    }

    [Theory]
    [InlineData("7Zip.LZMA.7z")]
    [InlineData("7Zip.LZMA2.7z")]
    [InlineData("7Zip.solid.7z")]
    public async Task SevenZip_OpenAsyncArchive_OddBuffers(string archiveName)
    {
        var testArchive = Path.Combine(TEST_ARCHIVES_PATH, archiveName);
        foreach (var bufferSize in BufferSizes)
        {
            CleanScratch();
            await using var stream = File.OpenRead(testArchive);
            await using var archive = await ArchiveFactory.OpenAsyncArchive(
                new AsyncOnlyStream(stream)
            );

            await foreach (var entry in archive.EntriesAsync.Where(e => !e.IsDirectory))
            {
                var targetPath = Path.Combine(SCRATCH_FILES_PATH, entry.Key!);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await using var source = await entry.OpenEntryStreamAsync(CancellationToken.None);
                await using var target = File.Create(targetPath);
                await CopyWithBufferAsync(source, target, bufferSize).ConfigureAwait(false);
            }

            VerifyFiles();
        }
    }

    [Theory]
    [InlineData("7Zip.LZMA.7z")]
    [InlineData("7Zip.LZMA2.7z")]
    [InlineData("7Zip.solid.7z")]
    public async Task SevenZip_ExtractAllEntriesAsync_OddBuffers(string archiveName)
    {
        var testArchive = Path.Combine(TEST_ARCHIVES_PATH, archiveName);
        foreach (var bufferSize in BufferSizes)
        {
            CleanScratch();
            await using var stream = File.OpenRead(testArchive);
            await using var archive = await ArchiveFactory.OpenAsyncArchive(
                new AsyncOnlyStream(stream)
            );
            await using var reader = await archive.ExtractAllEntriesAsync();

            while (await reader.MoveToNextEntryAsync().ConfigureAwait(false))
            {
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                var targetPath = Path.Combine(SCRATCH_FILES_PATH, reader.Entry.Key!);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await using var source = await reader.OpenEntryStreamAsync();
                await using var target = File.Create(targetPath);
                await CopyWithBufferAsync(source, target, bufferSize).ConfigureAwait(false);
            }

            VerifyFiles();
        }
    }

    private static byte[] CreateDeterministicPayload(int size, int seed)
    {
        var data = new byte[size];
        var rng = new Random(seed);
        // Mix compressible runs with incompressible noise so LZMA exercises both paths.
        var i = 0;
        while (i < size)
        {
            if ((i & 0x3FFF) < 0x2000)
            {
                var run = (byte)(i & 0xFF);
                var runLen = Math.Min(64 + (i % 192), size - i);
                data.AsSpan(i, runLen).Fill(run);
                i += runLen;
            }
            else
            {
                var chunk = Math.Min(256, size - i);
                rng.NextBytes(data.AsSpan(i, chunk));
                i += chunk;
            }
        }

        return data;
    }

    private static async Task RoundTripRawLzmaAsync(byte[] original, int bufferSize)
    {
        await using var compressed = new MemoryStream();
        byte[] properties;
        await using (
            var encoder = LzmaStream.Create(
                LzmaEncoderProperties.Default,
                false,
                new AsyncOnlyStream(compressed, disposeStream: false)
            )
        )
        {
            properties = encoder.Properties;
            await using var input = new MemoryStream(original);
            await CopyWithBufferAsync(input, encoder, bufferSize).ConfigureAwait(false);
        }

        compressed.Position = 0;
        await using var decoder = await LzmaStream
            .CreateAsync(
                properties,
                new AsyncOnlyStream(compressed, disposeStream: false),
                compressed.Length,
                original.LongLength
            )
            .ConfigureAwait(false);

        await using var decoded = new MemoryStream(original.Length);
        await CopyWithBufferAsync(decoder, decoded, bufferSize).ConfigureAwait(false);
        Assert.Equal(original, decoded.ToArray());
    }

    private static async Task CopyWithBufferAsync(Stream source, Stream destination, int bufferSize)
    {
        var buffer = new byte[bufferSize];
        int read;
        while (
            (read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false))
            > 0
        )
        {
            await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }
}
