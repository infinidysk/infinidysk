using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Tar;
using SharpCompress.Archives.Zip;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test;

public class StreamContractTests : TestBase
{
    public static TheoryData<string> ArchiveCases =>
        new() { "Zip.deflate", "Rar4", "Rar5.stored", "Rar5.compressed", "7z" };

    public static TheoryData<string> ReaderCases => new() { "Tar.gz" };

    [Theory]
    [MemberData(nameof(ReaderCases))]
    public void ReaderEntryStream_Contract(string caseName)
    {
        var path = GetArchivePath(caseName);
        using var reader = ReaderFactory.OpenReader(path);
        Assert.True(reader.MoveToNextEntry());
        using var expectedStream = new MemoryStream();
        using (var entryStream = reader.OpenEntryStream())
        {
            entryStream.CopyTo(expectedStream);
        }

        using var reader2 = ReaderFactory.OpenReader(path);
        Assert.True(reader2.MoveToNextEntry());
        var replay = reader2.OpenEntryStream();
        AssertStreamContract(replay, expectedStream.ToArray());
    }

    [Theory]
    [MemberData(nameof(ArchiveCases))]
    public void EntryStream_Contract(string caseName)
    {
        var path = GetArchivePath(caseName);
        var expected = ReadExpectedBytes(caseName, path);
        using var entryStream = OpenFirstEntryStream(caseName, path);
        AssertStreamContract(entryStream, expected);
    }

    [Fact]
    public void MultiVolumeReadOnlyStream_Contract()
    {
        var paths = new[]
        {
            Path.Combine(TEST_ARCHIVES_PATH, "Rar5.multi.none.part01.rar"),
            Path.Combine(TEST_ARCHIVES_PATH, "Rar5.multi.none.part02.rar"),
            Path.Combine(TEST_ARCHIVES_PATH, "Rar5.multi.none.part03.rar"),
        };
        var streams = paths.Select(p => (Stream)File.OpenRead(p)).ToArray();
        try
        {
            using var archive = RarArchive.OpenArchive(streams);
            var entry = archive.Entries.First(e => !e.IsDirectory);
            using var entryStream = entry.OpenEntryStream();
            using var expectedStream = new MemoryStream();
            entryStream.CopyTo(expectedStream);

            using var replay = entry.OpenEntryStream();
            AssertStreamContract(replay, expectedStream.ToArray());
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    [Fact]
    public void SharpCompressStream_NonSeekableReader_Contract()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.rar");
        var bytes = File.ReadAllBytes(path);
        using var nonSeekable = new NonSeekableStream(new MemoryStream(bytes));
        using var reader = ReaderFactory.OpenReader(nonSeekable);
        Assert.True(reader.MoveToNextEntry());
        using var entryStream = reader.OpenEntryStream();
        using var expected = new MemoryStream();
        entryStream.CopyTo(expected);

        using var nonSeekable2 = new NonSeekableStream(new MemoryStream(bytes));
        using var reader2 = ReaderFactory.OpenReader(nonSeekable2);
        Assert.True(reader2.MoveToNextEntry());
        using var replay = reader2.OpenEntryStream();
        AssertStreamContract(replay, expected.ToArray());
    }

    [Fact]
    public async Task EntryStream_DisposeAsync_AfterDispose_DoesNotThrow()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.rar");
        await using var archive = (RarArchive)await RarArchive.OpenAsyncArchive(path);
        var entry = archive.Entries.First(e => !e.IsDirectory);
        var entryStream = await entry.OpenEntryStreamAsync();
        await entryStream.DisposeAsync();
        await entryStream.DisposeAsync();
    }

    private static ArchiveBoundEntryStream OpenFirstEntryStream(string caseName, string path) =>
        caseName switch
        {
            "Zip.deflate" => OpenFirstEntry(() => ZipArchive.OpenArchive(path)),
            "Rar4" or "Rar5.stored" or "Rar5.compressed" => OpenFirstEntry(() =>
                RarArchive.OpenArchive(path)
            ),
            "7z" => OpenFirstEntry(() => SevenZipArchive.OpenArchive(path)),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

    private static ArchiveBoundEntryStream OpenFirstEntry<TArchive>(Func<TArchive> openArchive)
        where TArchive : IDisposable
    {
        var archive = openArchive();
        var entryStream = archive switch
        {
            ZipArchive zip => zip.Entries.First(e => !e.IsDirectory).OpenEntryStream(),
            RarArchive rar => rar.Entries.First(e => !e.IsDirectory).OpenEntryStream(),
            SevenZipArchive sevenZip => sevenZip
                .Entries.First(e => !e.IsDirectory)
                .OpenEntryStream(),
            TarArchive tar => tar.Entries.First(e => !e.IsDirectory).OpenEntryStream(),
            _ => throw new InvalidOperationException(),
        };

        return new ArchiveBoundEntryStream(archive, entryStream);
    }

    private static string GetArchivePath(string caseName) =>
        caseName switch
        {
            "Zip.deflate" => Path.Combine(TEST_ARCHIVES_PATH, "Zip.deflate.zip"),
            "Rar4" => Path.Combine(TEST_ARCHIVES_PATH, "Rar.rar"),
            "Rar5.stored" => Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.rar"),
            "Rar5.compressed" => Path.Combine(TEST_ARCHIVES_PATH, "Rar5.rar"),
            "7z" => Path.Combine(TEST_ARCHIVES_PATH, "7Zip.nonsolid.7z"),
            "Tar.gz" => Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar.gz"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

    private static byte[] ReadExpectedBytes(string caseName, string path)
    {
        using var stream = OpenFirstEntryStream(caseName, path);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void AssertStreamContract(Stream stream, byte[] expectedContent)
    {
        Assert.True(stream.CanRead);

        var empty = Array.Empty<byte>();
        Assert.Equal(0, stream.Read(empty, 0, 0));
        Assert.Equal(0, stream.Read(new byte[16], 0, 0));

        if (stream.CanSeek)
        {
            Assert.Equal(expectedContent.Length, stream.Length);
            Assert.Equal(0, stream.Position);
        }

        var buffer = new byte[expectedContent.Length + 32];
        var read = stream.Read(buffer, 0, buffer.Length);
        Assert.Equal(expectedContent.Length, read);
        Assert.Equal(expectedContent, buffer.AsSpan(0, read).ToArray());

        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(-1, stream.ReadByte());
        Assert.Equal(-1, stream.ReadByte());

        if (stream.CanSeek)
        {
            Assert.Equal(expectedContent.Length, stream.Position);
            stream.Seek(0, SeekOrigin.Begin);
            Assert.Equal(0, stream.Position);
            var reread = new byte[expectedContent.Length];
            Assert.Equal(expectedContent.Length, stream.Read(reread, 0, reread.Length));
            Assert.Equal(expectedContent, reread);
        }

        stream.Dispose();
        stream.Dispose();
    }

    private sealed class ArchiveBoundEntryStream(IDisposable archive, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int ReadByte() => inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                archive.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
