namespace UsenetSharp.Streams;

/// <summary>
/// Abstract base class for read-only streams that do not support write operations.
/// </summary>
public abstract class ReadOnlyStream : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => false;

    // Write operations - all throw NotSupportedException
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("This stream does not support writing.");
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        throw new NotSupportedException("This stream does not support writing.");
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("This stream does not support writing.");
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This stream does not support writing.");
    }

    public override void WriteByte(byte value)
    {
        throw new NotSupportedException("This stream does not support writing.");
    }

    public override void Flush()
    {
        // No-op for read-only streams
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        // No-op for read-only streams
        return Task.CompletedTask;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("This stream does not support setting length.");
    }
}
