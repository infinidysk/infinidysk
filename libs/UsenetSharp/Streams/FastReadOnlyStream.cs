using System.Buffers;

namespace UsenetSharp.Streams;

/// <summary>
/// Abstract base class for high-performance read-only streams.
/// Only requires implementing ReadAsync(Memory&lt;byte&gt;, CancellationToken).
/// All other read operations call into this async method.
/// </summary>
public abstract class FastReadOnlyStream : ReadOnlyStream
{
    // Core method - must be implemented by derived classes
    public abstract override ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default);

    // All other read methods call into ReadAsync
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(new Memory<byte>(buffer, offset, count), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
    }

    public override int Read(Span<byte> buffer)
    {
        var rentedArray = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var memory = new Memory<byte>(rentedArray, 0, buffer.Length);
            var bytesRead = ReadAsync(memory, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            memory.Span.Slice(0, bytesRead).CopyTo(buffer);
            return bytesRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        var bytesRead = Read(buffer);
        return bytesRead == 0 ? -1 : buffer[0];
    }
}
