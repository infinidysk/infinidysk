namespace NzbWebDAV.Streams;

/// <summary>
/// Abstracts segment buffer allocation so <see cref="PooledBufferStream"/> can use
/// either the BCL <see cref="System.Buffers.ArrayPool{T}"/> or a custom byte-bounded
/// pool without changing consumers.
/// </summary>
public interface ISegmentBufferPool
{
    /// <summary>Rent a buffer of at least <paramref name="minimumLength"/> bytes.</summary>
    byte[] Rent(int minimumLength);

    /// <summary>Return a previously rented buffer.</summary>
    void Return(byte[] buffer);

    /// <summary>Approximate bytes held idle in the pool (not rented out).</summary>
    long IdleBytes { get; }

    /// <summary>Number of distinct size classes with at least one idle buffer.</summary>
    int ActiveSizeClasses { get; }
}
