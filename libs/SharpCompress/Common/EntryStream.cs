using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common.Tar;
using SharpCompress.IO;
using SharpCompress.Readers;

namespace SharpCompress.Common;

public partial class EntryStream : Stream
{
    private readonly IReader _reader;
    private readonly Stream _stream;
    private readonly bool _cancelOnDispose;
    private bool _completed;
    private bool _isDisposed;

    internal EntryStream(IReader reader, Stream stream, bool cancelOnDispose = false)
    {
        _reader = reader;
        _stream = stream;
        _cancelOnDispose = cancelOnDispose;
    }

    /// <summary>
    /// When reading a stream from OpenEntryStream, the stream must be completed so use this to finish reading the entire entry.
    /// </summary>
    public void SkipEntry()
    {
        if (_stream is TarReadOnlySubStream tarSubStream)
        {
            tarSubStream.SkipRemaining();
        }
        else if (_stream is ReadOnlySubStream subStream)
        {
            subStream.SkipRemaining();
        }
        else
        {
            this.Skip();
        }
        _completed = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        if (!(_completed || _reader.Cancelled))
        {
            if (_cancelOnDispose)
            {
                _reader.Cancel();
                if (_stream is TarReadOnlySubStream tarStream)
                {
                    tarStream.AbandonWithoutAdvance();
                }
            }
            else
            {
                SkipEntry();
            }
        }

        // Some decompressors read more of the underlying stream than the entry required.
        // Let any such stream in the stack rewind the buffered underlying stream so the
        // next entry starts at the correct position.
        if (!_reader.Cancelled && _stream is IStreamStack ss)
        {
            ss.FindImplementing<IOverreadingStream>()?.ReturnOverread();
        }
        base.Dispose(disposing);
        _stream.Dispose();
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position; //throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _stream.Read(buffer, offset, count);
        if (read <= 0 && count > 0)
        {
            _completed = true;
        }
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _stream.Read(buffer);
        if (read <= 0 && buffer.Length > 0)
        {
            _completed = true;
        }
        return read;
    }

    public override int ReadByte()
    {
        var value = _stream.ReadByte();
        if (value == -1)
        {
            _completed = true;
        }
        return value;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();
}
