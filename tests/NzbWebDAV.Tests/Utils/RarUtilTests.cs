using System.Buffers.Binary;
using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Utils;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Tests.Utils;

public class RarUtilTests
{
    [Fact]
    public void TryMapHeaderParseFailure_WrapsSeekPastEndAsCorruptRarException()
    {
        using var stream = new MemoryStream(new byte[100]);
        var seekPastEnd = new ArgumentOutOfRangeException(
            "offset",
            52223980L,
            "Seek position is outside stream bounds.");

        Assert.True(RarUtil.TryMapHeaderParseFailure(seekPastEnd, stream, out var mapped));
        var ex = Assert.IsType<RarSeekPastEndException>(mapped);
        Assert.IsAssignableFrom<CorruptRarException>(mapped);
        Assert.Contains("seek past stream end", ex.Message);
        Assert.Contains("52223980", ex.Message);
        Assert.Contains("stream length 100", ex.Message);
        Assert.True(ex.IsNonRetryableDownloadException());
    }

    [Fact]
    public void TryMapHeaderParseFailure_MapsTruncatedRarHeaderSeekPastEnd()
    {
        using var stream = new MemoryStream(new byte[50]);
        var truncated = new RarHeaderReadException(
            "Failed to skip RAR packed data: seek past stream end at offset 100",
            truncated: true);

        Assert.True(RarUtil.TryMapHeaderParseFailure(truncated, stream, out var mapped));
        var ex = Assert.IsType<RarSeekPastEndException>(mapped);
        Assert.Contains("seek past stream end", ex.Message);
        Assert.Contains("stream length 50", ex.Message);
        Assert.True(ex.IsNonRetryableDownloadException());
    }

    [Fact]
    public void TryMapHeaderParseFailure_MapsIncompleteArchiveAsCorruptRar()
    {
        using var stream = new MemoryStream(new byte[10]);
        var incomplete = new IncompleteArchiveException("unexpected EOF");

        Assert.True(RarUtil.TryMapHeaderParseFailure(incomplete, stream, out var mapped));
        var ex = Assert.IsType<CorruptRarException>(mapped);
        Assert.Contains("unexpected end of stream", ex.Message);
        Assert.True(ex.IsNonRetryableDownloadException());
    }

    [Fact]
    public async Task FindFirstFileHeaderAsync_WrapsInvalidFormatAsCorruptRarException()
    {
        await using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = await Assert.ThrowsAsync<CorruptRarException>(async () =>
            await RarUtil.FindFirstFileHeaderAsync(
                stream,
                password: null,
                _ => true,
                CancellationToken.None));

        Assert.StartsWith("Failed to parse RAR volume headers:", ex.Message);
        Assert.True(ex.IsNonRetryableDownloadException());
    }

    [Fact]
    public void KnownDownloadClassification_TreatsCorruptRarAndInvalidFormatAsNonRetryable()
    {
        var corrupt = new CorruptRarException(
            "Failed to parse RAR volume headers (seek past stream end at offset 1; stream length 0)");
        Assert.True(corrupt.IsNonRetryableDownloadException());

        var wrappedInvalidFormat = new Exception(
            "wrapper",
            new InvalidFormatException("bad rar"));
        Assert.True(
            wrappedInvalidFormat.TryGetCausingException<InvalidFormatException>(out _));
        Assert.True(IsKnownDownloadStyle(wrappedInvalidFormat, out var reason));
        Assert.Equal("bad rar", reason);

        Assert.True(new IncompleteArchiveException("eof").IsNonRetryableDownloadException());
        Assert.True(new RarHeaderReadException("bad crc", truncated: false)
            .IsNonRetryableDownloadException());
    }

    [Fact]
    public async Task GetRarHeadersAsync_ReturnsArchiveAndStoredFileHeaders()
    {
        var payload = "hello-rar"u8.ToArray();
        var rarBytes = BuildStoredRar4(("movie.mkv", payload));
        await using var stream = new MemoryStream(rarBytes);

        var headers = await RarUtil.GetRarHeadersAsync(stream, password: null, CancellationToken.None);

        Assert.Contains(headers, header => header.HeaderType == HeaderType.Archive);
        var file = Assert.Single(headers.OfType<IRarFileHeader>());
        Assert.Equal("movie.mkv", file.FileName);
        Assert.Equal(payload.Length, file.UncompressedSize);
        Assert.True(file.IsStored);
    }

    [Fact]
    public async Task FindFirstFileHeaderAsync_StopsAfterMatchWithoutSkippingPackedData()
    {
        var rarBytes = BuildStoredRar4(
            ("first.bin", "aaaa"u8.ToArray()),
            ("second.bin", "bbbbbbbb"u8.ToArray()));
        var counting = new SeekCountingStream(new MemoryStream(rarBytes));

        var match = await RarUtil.FindFirstFileHeaderAsync(
            counting,
            password: null,
            header => header.FileName == "first.bin",
            CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("first.bin", match.FileName);
        Assert.DoesNotContain(
            match.DataStartPosition + match.CompressedSize,
            counting.SeekTargets);
    }

    [Fact]
    public async Task GetRarHeadersAsync_CancellationAfterSignatureThrowsOperationCanceledException()
    {
        var rarBytes = BuildStoredRar4(("movie.mkv", "payload"u8.ToArray()));
        using var cts = new CancellationTokenSource();
        using var enteredGate = new ManualResetEventSlim(false);
        await using var stream = new GateAfterReadsStream(
            new MemoryStream(rarBytes),
            passThroughReads: 7,
            enteredGate);

        var parseTask = RarUtil.GetRarHeadersAsync(stream, password: null, cts.Token);
        Assert.True(enteredGate.Wait(TimeSpan.FromSeconds(5)));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parseTask);
    }

    [Fact]
    public async Task GetRarHeadersAsync_ParallelFakeNntpStreamsCompleteWithoutError()
    {
        var rarBytes = BuildStoredRar4(("movie.mkv", "payload"u8.ToArray()));
        const int parallelism = 8;
        var segments = Enumerable.Range(0, parallelism)
            .ToDictionary(i => $"seg-{i}", _ => rarBytes);
        using var client = new FakeNntpClient(
            segments,
            useCachedYencStreams: true,
            decodedStreamFactory: (_, bytes) => new DelayedReadStream(
                new MemoryStream(bytes, writable: false),
                TimeSpan.FromMilliseconds(5)));

        var tasks = Enumerable.Range(0, parallelism).Select(async i =>
        {
            var nzbFile = new NzbFile
            {
                Subject = $"\"vol{i}.rar\" yEnc",
                Segments =
                {
                    new NzbSegment
                    {
                        MessageId = $"seg-{i}",
                        Bytes = rarBytes.Length,
                        ByteRange = LongRange.FromStartAndSize(0, rarBytes.Length),
                    }
                },
            };
            await using var stream = client.GetFileStream(
                nzbFile, rarBytes.Length, articleBufferSize: 0, usePipelinedBodyRequests: false);
            var headers = await RarUtil.GetRarHeadersAsync(
                stream, password: null, CancellationToken.None);
            Assert.Contains(headers, header => header.HeaderType == HeaderType.File);
        });

        await Task.WhenAll(tasks);
    }

    // Mirrors ExceptionMiddleware.IsKnownDownloadException chain walk.
    private static bool IsKnownDownloadStyle(Exception e, out string message)
    {
        for (var current = e; current != null; current = current.InnerException)
        {
            if (current.IsRetryableDownloadException() || current.IsNonRetryableDownloadException())
            {
                message = current.Message;
                return true;
            }
        }

        message = string.Empty;
        return false;
    }

    private static byte[] BuildStoredRar4(params (string FileName, byte[] Payload)[] files)
    {
        using var ms = new MemoryStream();
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);

        {
            Span<byte> body = stackalloc byte[11];
            body[0] = 0x73;
            BinaryPrimitives.WriteUInt16LittleEndian(body[1..], 0x0000);
            BinaryPrimitives.WriteUInt16LittleEndian(body[3..], 13);
            BinaryPrimitives.WriteUInt16LittleEndian(body[5..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(body[7..], 0);
            WriteRar4Header(ms, body);
        }

        foreach (var (fileName, payload) in files)
        {
            var nameBytes = Encoding.ASCII.GetBytes(fileName);
            var headSize = (ushort)(32 + nameBytes.Length);
            var body = new byte[headSize - 2];
            var o = 0;
            body[o++] = 0x74;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), 0x8000); // HAS_DATA
            o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), headSize);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)payload.Length);
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), (uint)payload.Length);
            o += 4;
            body[o++] = 2; // HostOS Unix
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0);
            o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0);
            o += 4;
            body[o++] = 20; // UnpVer
            body[o++] = 0x30; // store
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o), (ushort)nameBytes.Length);
            o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(o), 0);
            o += 4;
            nameBytes.CopyTo(body.AsSpan(o));
            WriteRar4Header(ms, body);
            ms.Write(payload);
        }

        return ms.ToArray();
    }

    private static void WriteRar4Header(Stream stream, ReadOnlySpan<byte> bodyWithoutCrc)
    {
        var crc = RarCrc16(bodyWithoutCrc);
        Span<byte> hdr = stackalloc byte[bodyWithoutCrc.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr, crc);
        bodyWithoutCrc.CopyTo(hdr[2..]);
        stream.Write(hdr);
    }

    private static ushort RarCrc16(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return (ushort)(~crc);
    }

    private sealed class SeekCountingStream(Stream inner) : Stream
    {
        public List<long> SeekTargets { get; } = [];

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set
            {
                SeekTargets.Add(value);
                inner.Position = value;
            }
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            var result = inner.Seek(offset, origin);
            SeekTargets.Add(result);
            return result;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Lets the first <paramref name="passThroughReads"/> async reads complete, then
    /// waits until the caller cancels so tests can cancel after the RAR signature.
    /// </summary>
    private sealed class GateAfterReadsStream(
        Stream inner,
        int passThroughReads,
        ManualResetEventSlim enteredGate) : Stream
    {
        private int _reads;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _reads);
            if (n > passThroughReads)
            {
                enteredGate.Set();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10, cancellationToken);
                }
            }

            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private sealed class DelayedReadStream(Stream inner, TimeSpan delay) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
