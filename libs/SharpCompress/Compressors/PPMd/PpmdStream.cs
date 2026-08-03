using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Compressors.LZMA.RangeCoder;
using SharpCompress.Compressors.PPMd.H;
using SharpCompress.Compressors.PPMd.I1;
using SharpCompress.IO;

namespace SharpCompress.Compressors.PPMd;

public class PpmdStream : Stream, IAsyncDisposable
{
    private readonly PpmdProperties _properties;
    private readonly Stream _stream;
    private readonly bool _compress;
    private Model? _model;
    private ModelPpm? _modelH;
    private Decoder? _decoder;
    private long _position;
    private bool _isDisposed;

    private PpmdStream(PpmdProperties properties, Stream stream, bool compress)
    {
        _properties = properties;
        _stream = stream;
        _compress = compress;

        InitializeSync(stream, compress);
    }

    private PpmdStream(
        PpmdProperties properties,
        Stream stream,
        bool compress,
        bool skipInitialization
    )
    {
        _properties = properties;
        _stream = stream;
        _compress = compress;

        // Skip initialization - used by CreateAsync
    }

    private void InitializeSync(Stream stream, bool compress)
    {
        if (_properties.Version == PpmdVersion.I1)
        {
            _model = new Model();
            if (compress)
            {
                _model.EncodeStart(_properties);
            }
            else
            {
                _model.DecodeStart(stream, _properties);
            }
        }
        if (_properties.Version == PpmdVersion.H)
        {
            _modelH = new ModelPpm();
            if (compress)
            {
                throw new NotImplementedException();
            }
            _modelH.DecodeInit(stream, _properties.ModelOrder, _properties.AllocatorSize);
        }
        if (_properties.Version == PpmdVersion.H7Z)
        {
            _modelH = new ModelPpm();
            if (compress)
            {
                throw new NotImplementedException();
            }
            _modelH.DecodeInit(null, _properties.ModelOrder, _properties.AllocatorSize);
            _decoder = new Decoder();
            _decoder.Init(stream);
        }
    }

    public static PpmdStream Create(PpmdProperties properties, Stream stream, bool compress) =>
        new PpmdStream(properties, stream, compress);

    public static async ValueTask<PpmdStream> CreateAsync(
        PpmdProperties properties,
        Stream stream,
        bool compress,
        CancellationToken cancellationToken = default
    )
    {
        ThrowHelper.ThrowIfNull(stream);

        if (properties.Version == PpmdVersion.H && compress)
        {
            throw new NotImplementedException("PPMd H version compression not supported");
        }

        if (properties.Version == PpmdVersion.H7Z && compress)
        {
            throw new NotImplementedException("PPMd H7Z version compression not supported");
        }

        var instance = new PpmdStream(properties, stream, compress, skipInitialization: true);

        try
        {
            if (properties.Version == PpmdVersion.I1)
            {
                instance._model = new Model();
                if (compress)
                {
                    instance._model.EncodeStart(properties);
                }
                else
                {
                    await instance
                        ._model.DecodeStartAsync(stream, properties, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (properties.Version == PpmdVersion.H)
            {
                instance._modelH = new ModelPpm();
                await instance
                    ._modelH.DecodeInitAsync(
                        stream,
                        properties.ModelOrder,
                        properties.AllocatorSize,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            else if (properties.Version == PpmdVersion.H7Z)
            {
                instance._modelH = new ModelPpm();
                await instance
                    ._modelH.DecodeInitAsync(
                        null,
                        properties.ModelOrder,
                        properties.AllocatorSize,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                instance._decoder = new Decoder();
                await instance._decoder.InitAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            return instance;
        }
        catch
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override bool CanRead => !_compress;

    public override bool CanSeek => false;

    public override bool CanWrite => _compress;

    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        if (disposing)
        {
            if (_compress)
            {
                _model.NotNull().EncodeBlock(_stream, Stream.Null, true);
            }
            _modelH?.Dispose();
            _modelH = null;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_compress)
        {
            await _model
                .NotNull()
                .EncodeBlockAsync(_stream, new MemoryStream(), true)
                .ConfigureAwait(false);
        }
        _modelH?.Dispose();
        _modelH = null;

        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_compress)
        {
            return 0;
        }
        var size = 0;
        if (_properties.Version == PpmdVersion.I1)
        {
            size = _model.NotNull().DecodeBlock(_stream, buffer, offset, count);
        }
        if (_properties.Version == PpmdVersion.H)
        {
            int c;
            while (size < count && (c = _modelH.NotNull().DecodeChar()) >= 0)
            {
                buffer[offset++] = (byte)c;
                size++;
            }
        }
        if (_properties.Version == PpmdVersion.H7Z)
        {
            int c;
            while (size < count && (c = _modelH.NotNull().DecodeChar(_decoder.NotNull())) >= 0)
            {
                buffer[offset++] = (byte)c;
                size++;
            }
        }
        _position += size;
        return size;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_compress)
        {
            return 0;
        }

        var size = 0;
        if (_properties.Version == PpmdVersion.I1)
        {
            var tempBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                size = _model.NotNull().DecodeBlock(_stream, tempBuffer, 0, buffer.Length);
                tempBuffer.AsSpan(0, size).CopyTo(buffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tempBuffer);
            }
        }
        if (_properties.Version == PpmdVersion.H)
        {
            int c;
            while (size < buffer.Length && (c = _modelH.NotNull().DecodeChar()) >= 0)
            {
                buffer[size++] = (byte)c;
            }
        }
        if (_properties.Version == PpmdVersion.H7Z)
        {
            int c;
            while (
                size < buffer.Length && (c = _modelH.NotNull().DecodeChar(_decoder.NotNull())) >= 0
            )
            {
                buffer[size++] = (byte)c;
            }
        }
        _position += size;
        return size;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (_compress)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var size = 0;
        if (_properties.Version == PpmdVersion.I1)
        {
            size = await _model
                .NotNull()
                .DecodeBlockAsync(_stream, buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);
        }
        if (_properties.Version == PpmdVersion.H)
        {
            int c;
            while (
                size < count
                && (
                    c = await _modelH
                        .NotNull()
                        .DecodeCharAsync(cancellationToken)
                        .ConfigureAwait(false)
                ) >= 0
            )
            {
                buffer[offset++] = (byte)c;
                size++;
            }
        }
        if (_properties.Version == PpmdVersion.H7Z)
        {
            int c;
            while (
                size < count
                && (
                    c = await _modelH
                        .NotNull()
                        .DecodeCharAsync(_decoder.NotNull(), cancellationToken)
                        .ConfigureAwait(false)
                ) >= 0
            )
            {
                buffer[offset++] = (byte)c;
                size++;
            }
        }
        _position += size;
        return size;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_compress)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var size = 0;
        var offset = 0;
        var count = buffer.Length;

        if (_properties.Version == PpmdVersion.I1)
        {
            // DecodeBlockAsync works with byte[]; use the Memory's array when available.
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
            {
                size = await _model
                    .NotNull()
                    .DecodeBlockAsync(
                        _stream,
                        segment.Array!,
                        segment.Offset,
                        count,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                var tempBuffer = ArrayPool<byte>.Shared.Rent(count);
                try
                {
                    size = await _model
                        .NotNull()
                        .DecodeBlockAsync(_stream, tempBuffer, 0, count, cancellationToken)
                        .ConfigureAwait(false);
                    tempBuffer.AsMemory(0, size).CopyTo(buffer);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tempBuffer);
                }
            }
        }
        if (_properties.Version == PpmdVersion.H)
        {
            int c;
            while (
                size < count
                && (
                    c = await _modelH
                        .NotNull()
                        .DecodeCharAsync(cancellationToken)
                        .ConfigureAwait(false)
                ) >= 0
            )
            {
                buffer.Span[offset++] = (byte)c;
                size++;
            }
        }
        if (_properties.Version == PpmdVersion.H7Z)
        {
            int c;
            while (
                size < count
                && (
                    c = await _modelH
                        .NotNull()
                        .DecodeCharAsync(_decoder.NotNull(), cancellationToken)
                        .ConfigureAwait(false)
                ) >= 0
            )
            {
                buffer.Span[offset++] = (byte)c;
                size++;
            }
        }
        _position += size;
        return size;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_compress)
        {
            _model.NotNull().EncodeBlock(_stream, new MemoryStream(buffer, offset, count), false);
        }
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_compress)
        {
            await _model
                .NotNull()
                .EncodeBlockAsync(
                    _stream,
                    new MemoryStream(buffer, offset, count),
                    false,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_compress)
        {
            await _model
                .NotNull()
                .EncodeBlockAsync(
                    _stream,
                    new MemoryStream(buffer.ToArray()),
                    false,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }
}
