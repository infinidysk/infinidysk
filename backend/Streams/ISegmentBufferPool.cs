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
}
