using System;
using System.IO;
using System.Linq;
using SharpCompress.Archives.SevenZip;
using SharpCompress.IO;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.Streams;

public class StreamSpanReadParityTests : TestBase
{
    private static byte[] ReadWithArray(Stream stream)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static byte[] ReadWithSpan(Stream stream)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer.AsSpan())) > 0)
        {
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static void AssertArrayAndSpanReadsMatch(Func<Stream> openStream)
    {
        byte[] arrayResult;
        using (var stream = openStream())
        {
            arrayResult = ReadWithArray(stream);
        }

        byte[] spanResult;
        using (var stream = openStream())
        {
            spanResult = ReadWithSpan(stream);
        }

        Assert.Equal(arrayResult, spanResult);
    }

    [Fact]
    public void RarEntryStream_SpanRead_MatchesArrayRead()
    {
        AssertArrayAndSpanReadsMatch(() =>
        {
            var reader = ReaderFactory.OpenReader(Path.Combine(TEST_ARCHIVES_PATH, "Rar.rar"));
            reader.MoveToNextEntry();
            return reader.OpenEntryStream();
        });
    }

    [Fact]
    public void SevenZipEntryStream_SpanRead_MatchesArrayRead()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "7Zip.LZMA.7z");

        byte[] arrayResult;
        using (var archive = SevenZipArchive.OpenArchive(path))
        {
            var entry = archive.Entries.First(e => !e.IsDirectory);
            using (var stream = entry.OpenEntryStream())
            {
                arrayResult = ReadWithArray(stream);
            }
        }

        byte[] spanResult;
        using (var archive = SevenZipArchive.OpenArchive(path))
        {
            var entry = archive.Entries.First(e => !e.IsDirectory);
            using (var stream = entry.OpenEntryStream())
            {
                spanResult = ReadWithSpan(stream);
            }
        }

        Assert.Equal(arrayResult, spanResult);
    }

    [Fact]
    public void ZipDataDescriptorEntryStream_SpanRead_MatchesArrayRead()
    {
        AssertArrayAndSpanReadsMatch(() =>
        {
            var reader = ReaderFactory.OpenReader(
                Path.Combine(TEST_ARCHIVES_PATH, "Zip.none.datadescriptors.zip")
            );
            reader.MoveToNextEntry();
            return reader.OpenEntryStream();
        });
    }

    [Fact]
    public void BufferedSubStream_SpanRead_MatchesArrayRead()
    {
        var payload = Enumerable.Range(0, 5000).Select(i => (byte)(i & 0xFF)).ToArray();
        using var inner = new MemoryStream(payload);

        byte[] arrayResult;
        using (var stream = new BufferedSubStream(inner, 100, 4000))
        {
            arrayResult = ReadWithArray(stream);
        }

        inner.Position = 0;
        byte[] spanResult;
        using (var stream = new BufferedSubStream(inner, 100, 4000))
        {
            spanResult = ReadWithSpan(stream);
        }

        Assert.Equal(arrayResult, spanResult);
        Assert.Equal(4000, arrayResult.Length);
    }
}
