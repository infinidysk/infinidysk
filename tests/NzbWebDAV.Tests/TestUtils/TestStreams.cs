namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Test helpers that construct streams whose ownership is transferred to the caller.
/// </summary>
internal static class TestStreams
{
    public static Stream Create(byte[] payload) => new MemoryStream(payload, writable: false);

    /// <summary>
    /// Wraps <paramref name="inner"/> and cancels <paramref name="cts"/> once more than
    /// <paramref name="cancelAfterBytes"/> bytes have been read, so parsers observe
    /// cancellation deterministically mid-stream.
    /// </summary>
    public static Stream CancelAfterBytes(Stream inner, long cancelAfterBytes, CancellationTokenSource cts) =>
        new CancelAfterBytesStream(inner, cancelAfterBytes, cts);

    private sealed class CancelAfterBytesStream(Stream inner, long cancelAfterBytes, CancellationTokenSource cts)
        : Stream
    {
        private long _bytesRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Add(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Add(read);
            return read;
        }

        private void Add(int read)
        {
            if (read <= 0) return;
            _bytesRead += read;
            if (_bytesRead > cancelAfterBytes)
                cts.Cancel();
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
