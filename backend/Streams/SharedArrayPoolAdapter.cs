using System.Buffers;

namespace NzbWebDAV.Streams;

/// <summary>
/// Default <see cref="ISegmentBufferPool"/> wrapping <see cref="ArrayPool{T}.Shared"/>.
/// Used as the production baseline for comparison with <see cref="SegmentBufferPool"/>.
/// </summary>
public sealed class SharedArrayPoolAdapter : ISegmentBufferPool
{
    public static readonly SharedArrayPoolAdapter Instance = new();

    public byte[] Rent(int minimumLength) => ArrayPool<byte>.Shared.Rent(minimumLength);
    public void Return(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);
}
