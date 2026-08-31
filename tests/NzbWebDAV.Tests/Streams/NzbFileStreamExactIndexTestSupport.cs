using System.Text;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.Tests.Streams;

internal static class NzbFileStreamExactIndexTestSupport
{
    internal static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    internal static readonly byte[][] SegmentBytes =
    [
        Encoding.ASCII.GetBytes("abcde"),
        Encoding.ASCII.GetBytes("fghij"),
        Encoding.ASCII.GetBytes("klmno")
    ];

    internal static readonly string[] SegmentIds = ["one", "two", "three"];
    internal static readonly LongRange[] SegmentRanges =
    [
        new(0, 5),
        new(5, 10),
        new(10, 15)
    ];

    internal const long LargeBudget = 2L * 1024 * 1024;

    internal static FakeNntpClient CreateClient(
        Func<string, byte[], Stream>? decodedStreamFactory = null) =>
        new(
            SegmentIds.Zip(SegmentBytes).ToDictionary(pair => pair.First, pair => pair.Second),
            useCachedYencStreams: true,
            SegmentIds.Zip(SegmentRanges).ToDictionary(pair => pair.First, pair => pair.Second),
            decodedStreamFactory);

    internal static NzbFileStream CreateStream(FakeNntpClient client, int articleBufferSize = 4) =>
        new(
            SegmentIds,
            15,
            client,
            articleBufferSize,
            SegmentRanges,
            segmentByteRangesTrusted: true);

    internal static BudgetScope SetBudget(long? budget)
    {
        var previous = RangeContext.GetReadBudget();
        RangeContext.SetReadBudget(budget);
        return new BudgetScope(previous);
    }

    internal readonly struct BudgetScope(long? previous) : IDisposable
    {
        public void Dispose() => RangeContext.SetReadBudget(previous);
    }

    internal sealed class ImmediateEofStream : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new EndOfStreamException("truncated article");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new EndOfStreamException("truncated article"));

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Disposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class ThrowingPhaseStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
