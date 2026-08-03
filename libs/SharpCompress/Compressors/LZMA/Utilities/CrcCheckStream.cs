using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress.Compressors.LZMA.Utilities;

[CLSCompliant(false)]
[Obsolete("Unused by SharpCompress; will be removed in a future major version.")]
public class CrcCheckStream(uint crc) : Stream
{
    private uint _mCurrentCrc = Crc.INIT_CRC;
    private bool _mClosed;
    private bool _mFinished;

    protected override void Dispose(bool disposing)
    {
        if (_mClosed)
        {
            return;
        }

        _mClosed = true;
        base.Dispose(disposing);
    }

    public void Finish()
    {
        if (_mFinished)
        {
            return;
        }

        _mFinished = true;
        _mCurrentCrc = Crc.Finish(_mCurrentCrc);

        if (_mCurrentCrc != crc)
        {
            throw new ArchiveOperationException();
        }
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override void Flush() { }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new ArchiveOperationException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _mCurrentCrc = Crc.Update(_mCurrentCrc, buffer, offset, count);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }
}
