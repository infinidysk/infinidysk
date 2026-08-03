using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Algorithms;

namespace SharpCompress.Crypto;

[CLSCompliant(false)]
public sealed class Crc32Stream : Stream
{
    private readonly Stream stream;
    private readonly uint _polynomial;
    private uint[]? _table;
    private uint seed;

    public const uint DEFAULT_POLYNOMIAL = 0xedb88320u;
    public const uint DEFAULT_SEED = 0xffffffffu;

    private static uint[]? _defaultTable;

    public Crc32Stream(Stream stream)
        : this(stream, DEFAULT_POLYNOMIAL, DEFAULT_SEED) { }

    public Crc32Stream(Stream stream, uint polynomial, uint seed)
    {
        this.stream = stream;
        _polynomial = polynomial;
        this.seed = seed;
    }

    public Stream WrappedStream => stream;

    public override void Flush() => stream.Flush();

    public override async Task FlushAsync(CancellationToken cancellationToken) =>
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        stream.Write(buffer);

        seed = AppendCrc(buffer);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        stream.Write(buffer, offset, count);
        seed = AppendCrc(buffer.AsSpan(offset, count));
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        await stream
            .WriteAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        seed = AppendCrc(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        seed = AppendCrc(buffer.Span);
    }

    public override void WriteByte(byte value)
    {
        stream.WriteByte(value);
        seed = AppendCrc(value);
    }

    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => stream.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public uint Crc => ~seed;

    public static uint Compute(byte[] buffer) => Compute(DEFAULT_SEED, buffer);

    public static uint Compute(uint seed, byte[] buffer) =>
        Compute(DEFAULT_POLYNOMIAL, seed, buffer);

    public static uint Compute(uint polynomial, uint seed, ReadOnlySpan<byte> buffer) =>
        polynomial == DEFAULT_POLYNOMIAL
            ? ~Crc32Helper.Append(seed, buffer)
            : ~CalculateCrc(InitializeTable(polynomial), seed, buffer);

    private uint AppendCrc(ReadOnlySpan<byte> buffer) =>
        _polynomial == DEFAULT_POLYNOMIAL
            ? Crc32Helper.Append(seed, buffer)
            : CalculateCrc(GetTable(), seed, buffer);

    private uint AppendCrc(byte b) =>
        _polynomial == DEFAULT_POLYNOMIAL
            ? Crc32Helper.Append(seed, b)
            : CalculateCrc(GetTable(), seed, b);

    private uint[] GetTable() => _table ??= InitializeTable(_polynomial);

    internal static uint[] InitializeTable(uint polynomial)
    {
        if (polynomial == DEFAULT_POLYNOMIAL && _defaultTable != null)
        {
            return _defaultTable;
        }

        var createTable = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var entry = (uint)i;
            for (var j = 0; j < 8; j++)
            {
                if ((entry & 1) == 1)
                {
                    entry = (entry >> 1) ^ polynomial;
                }
                else
                {
                    entry >>= 1;
                }
            }

            createTable[i] = entry;
        }

        if (polynomial == DEFAULT_POLYNOMIAL)
        {
            _defaultTable = createTable;
        }

        return createTable;
    }

    internal static uint CalculateCrc(uint[] table, uint crc, ReadOnlySpan<byte> buffer)
    {
        unchecked
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                crc = CalculateCrc(table, crc, buffer[i]);
            }
        }
        return crc;
    }

    internal static uint CalculateCrc(uint[] table, uint crc, byte b) =>
        (crc >> 8) ^ table[(crc ^ b) & 0xFF];
}
