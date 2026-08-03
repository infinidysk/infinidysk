using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Readers;

public class EntryStreamDisposeTests : TestBase
{
    private static void MoveToFirstFileEntry(IReader reader)
    {
        reader.MoveToNextEntry().Should().BeTrue();
        while (reader.Entry.IsDirectory)
        {
            reader.MoveToNextEntry().Should().BeTrue();
        }
    }

    private static async Task MoveToFirstFileEntryAsync(IAsyncReader reader)
    {
        (await reader.MoveToNextEntryAsync()).Should().BeTrue();
        while (reader.Entry.IsDirectory)
        {
            (await reader.MoveToNextEntryAsync()).Should().BeTrue();
        }
    }

    private static string ReadEntryFully(Stream entryStream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[256];
        int read;
        while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task<string> ReadEntryFullyAsync(Stream entryStream)
    {
        await using var memory = new MemoryStream();
        var buffer = new byte[256];
        int read;
        while ((read = await entryStream.ReadAsync(buffer)) > 0)
        {
            await memory.WriteAsync(buffer.AsMemory(0, read));
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    [Fact]
    public void ZeroLengthRead_DoesNotMarkEntryCompleted()
    {
        var archiveBytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        string expectedContent;
        using (
            var reader = ReaderFactory.OpenReader(
                new MemoryStream(archiveBytes),
                ReaderOptions.ForExternalStream
            )
        )
        {
            MoveToFirstFileEntry(reader);
            expectedContent = ReadEntryFully(reader.OpenEntryStream());
        }

        using (
            var reader = ReaderFactory.OpenReader(
                new MemoryStream(archiveBytes),
                ReaderOptions.ForExternalStream
            )
        )
        {
            MoveToFirstFileEntry(reader);
            using (var entryStream = reader.OpenEntryStream())
            {
                entryStream.Read(Array.Empty<byte>(), 0, 0).Should().Be(0);
                entryStream.Read(new byte[16], 0, 0).Should().Be(0);
                ReadEntryFully(entryStream).Should().Be(expectedContent);
            }

            reader.MoveToNextEntry().Should().BeTrue();
        }
    }

    [Fact]
    public void ZeroLengthSpanRead_DoesNotMarkEntryCompleted()
    {
        var archiveBytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        string expectedContent;
        using (
            var reader = ReaderFactory.OpenReader(
                new MemoryStream(archiveBytes),
                ReaderOptions.ForExternalStream
            )
        )
        {
            MoveToFirstFileEntry(reader);
            expectedContent = ReadEntryFully(reader.OpenEntryStream());
        }

        using (
            var reader = ReaderFactory.OpenReader(
                new MemoryStream(archiveBytes),
                ReaderOptions.ForExternalStream
            )
        )
        {
            MoveToFirstFileEntry(reader);
            using (var entryStream = reader.OpenEntryStream())
            {
                entryStream.Read(Span<byte>.Empty).Should().Be(0);
                ReadEntryFully(entryStream).Should().Be(expectedContent);
            }

            reader.MoveToNextEntry().Should().BeTrue();
        }
    }

    [Fact]
    public async Task ZeroLengthReadAsync_DoesNotMarkEntryCompleted()
    {
        var archiveBytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        string expectedContent;
        await using (
            var reader = await ReaderFactory.OpenAsyncReader(
                new MemoryStream(archiveBytes),
                ReaderOptions.ForExternalStream
            )
        )
        {
            await MoveToFirstFileEntryAsync(reader);
            expectedContent = await ReadEntryFullyAsync(await reader.OpenEntryStreamAsync());
        }

        await using (
            var reader = await ReaderFactory.OpenAsyncReader(
                new MemoryStream(archiveBytes),
                ReaderOptions.ForExternalStream
            )
        )
        {
            await MoveToFirstFileEntryAsync(reader);
            await using (var entryStream = await reader.OpenEntryStreamAsync())
            {
                (await entryStream.ReadAsync(Array.Empty<byte>(), 0, 0)).Should().Be(0);
                (await entryStream.ReadAsync(Memory<byte>.Empty)).Should().Be(0);
                (await ReadEntryFullyAsync(entryStream)).Should().Be(expectedContent);
            }

            (await reader.MoveToNextEntryAsync()).Should().BeTrue();
        }
    }

    [Fact]
    public void Dispose_Default_DrainsRemainingEntryBytes()
    {
        var archiveBytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        var counting = new CountingReadStream(
            new ForwardOnlyStream(new MemoryStream(archiveBytes)),
            leaveOpen: false
        );
        using var reader = ReaderFactory.OpenReader(counting, ReaderOptions.ForExternalStream);

        reader.MoveToNextEntry().Should().BeTrue();
        while (reader.Entry.IsDirectory)
        {
            reader.MoveToNextEntry().Should().BeTrue();
        }

        var afterOpen = counting.BytesRead;
        using (var entryStream = reader.OpenEntryStream())
        {
            var buffer = new byte[16];
            var read = entryStream.Read(buffer, 0, buffer.Length);
            read.Should().BeGreaterThan(0);
        }

        counting.BytesRead.Should().BeGreaterThan(afterOpen + 16);
        reader.Cancelled.Should().BeFalse();
        reader.MoveToNextEntry().Should().BeTrue();
    }

    [Fact]
    public void Dispose_Default_SeekableSourceSeeksPastRemainingEntryBytes()
    {
        var archiveBytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        var counting = new CountingReadStream(new MemoryStream(archiveBytes), leaveOpen: false);
        using var reader = ReaderFactory.OpenReader(counting, ReaderOptions.ForExternalStream);

        reader.MoveToNextEntry().Should().BeTrue();
        while (reader.Entry.IsDirectory)
        {
            reader.MoveToNextEntry().Should().BeTrue();
        }

        long bytesAfterPartialRead;
        using (var entryStream = reader.OpenEntryStream())
        {
            var buffer = new byte[16];
            entryStream.Read(buffer, 0, buffer.Length).Should().BeGreaterThan(0);
            bytesAfterPartialRead = counting.BytesRead;
        }

        counting.BytesRead.Should().Be(bytesAfterPartialRead);
        reader.Cancelled.Should().BeFalse();
        reader.MoveToNextEntry().Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_Default_SeekableSourceSeeksPastRemainingEntryBytes()
    {
        var archiveBytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        var counting = new CountingReadStream(new MemoryStream(archiveBytes), leaveOpen: false);
        await using var reader = await ReaderFactory.OpenAsyncReader(
            counting,
            ReaderOptions.ForExternalStream
        );

        (await reader.MoveToNextEntryAsync()).Should().BeTrue();
        while (reader.Entry.IsDirectory)
        {
            (await reader.MoveToNextEntryAsync()).Should().BeTrue();
        }

        long bytesAfterPartialRead;
        await using (var entryStream = await reader.OpenEntryStreamAsync())
        {
            var buffer = new byte[16];
            (await entryStream.ReadAsync(buffer)).Should().BeGreaterThan(0);
            bytesAfterPartialRead = counting.BytesRead;
        }

        counting.BytesRead.Should().Be(bytesAfterPartialRead);
        reader.Cancelled.Should().BeFalse();
        (await reader.MoveToNextEntryAsync()).Should().BeTrue();
    }

    [Fact]
    public void Dispose_CancelOnEntryStreamDispose_DoesNotDrain()
    {
        var archiveBytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        var counting = new CountingReadStream(new MemoryStream(archiveBytes), leaveOpen: false);
        using var reader = ReaderFactory.OpenReader(
            counting,
            ReaderOptions.ForExternalStream.WithCancelOnEntryStreamDispose(true)
        );

        reader.MoveToNextEntry().Should().BeTrue();
        while (reader.Entry.IsDirectory)
        {
            reader.MoveToNextEntry().Should().BeTrue();
        }

        long bytesAfterPartialRead;
        using (var entryStream = reader.OpenEntryStream())
        {
            var buffer = new byte[16];
            var read = entryStream.Read(buffer, 0, buffer.Length);
            read.Should().BeGreaterThan(0);
            bytesAfterPartialRead = counting.BytesRead;
        }

        counting.BytesRead.Should().Be(bytesAfterPartialRead);
        reader.Cancelled.Should().BeTrue();
        Assert.Throws<ReaderCancelledException>(() => reader.MoveToNextEntry());
    }

    [Fact]
    public async Task DisposeAsync_CancelOnEntryStreamDispose_DoesNotDrain()
    {
        var archiveBytes = File.ReadAllBytes(Path.Combine(TEST_ARCHIVES_PATH, "Tar.tar"));
        var counting = new CountingReadStream(new MemoryStream(archiveBytes), leaveOpen: false);
        await using var reader = await ReaderFactory.OpenAsyncReader(
            counting,
            ReaderOptions.ForExternalStream.WithCancelOnEntryStreamDispose(true)
        );

        (await reader.MoveToNextEntryAsync()).Should().BeTrue();
        while (reader.Entry.IsDirectory)
        {
            (await reader.MoveToNextEntryAsync()).Should().BeTrue();
        }

        long bytesAfterPartialRead;
        await using (var entryStream = await reader.OpenEntryStreamAsync())
        {
            var buffer = new byte[16];
            var read = await entryStream.ReadAsync(buffer);
            read.Should().BeGreaterThan(0);
            bytesAfterPartialRead = counting.BytesRead;
        }

        counting.BytesRead.Should().Be(bytesAfterPartialRead);
        reader.Cancelled.Should().BeTrue();
        await Assert.ThrowsAsync<ReaderCancelledException>(async () =>
            await reader.MoveToNextEntryAsync()
        );
    }
}
