using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar;

namespace SharpCompress.Compressors.Rar;

internal sealed partial class MultiVolumeReadOnlyAsyncStream : MultiVolumeReadOnlyStreamBase
{
    private IAsyncEnumerator<RarFilePart> filePartEnumerator;

    private MultiVolumeReadOnlyAsyncStream(IAsyncEnumerable<RarFilePart> parts)
    {
        filePartEnumerator = parts.GetAsyncEnumerator();
    }

    private MultiVolumeReadOnlyAsyncStream(IEnumerator<RarFilePart> parts)
    {
        filePartEnumerator = new SyncEnumeratorAsyncAdapter(parts);
    }

    private void InitializeNextFilePart()
    {
        var part = filePartEnumerator.Current;
        ResetPartState(
            part.FileHeader.CompressedSize,
            part.GetCompressedStream().NotNull(),
            part.FileHeader.IsSplitAfter
        );
        CurrentCrc = part.FileHeader.FileCrc;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException(
            "Synchronous read is not supported in MultiVolumeReadOnlyAsyncStream."
        );

    public override int Read(Span<byte> buffer) =>
        throw new NotSupportedException(
            "Synchronous read is not supported in MultiVolumeReadOnlyAsyncStream."
        );

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override void Flush() { }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private sealed class SyncEnumeratorAsyncAdapter(IEnumerator<RarFilePart> inner)
        : IAsyncEnumerator<RarFilePart>
    {
        public RarFilePart Current => inner.Current;

        public ValueTask<bool> MoveNextAsync() => new(inner.MoveNext());

        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return default;
        }
    }
}
