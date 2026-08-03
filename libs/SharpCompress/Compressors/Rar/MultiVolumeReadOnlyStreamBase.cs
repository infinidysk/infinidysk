using System.IO;
using SharpCompress.Common;
using SharpCompress.Common.Rar;

namespace SharpCompress.Compressors.Rar;

internal abstract class MultiVolumeReadOnlyStreamBase : Stream
{
    protected long currentPosition;
    protected long maxPosition;
    protected long remainingInPart;
    protected Stream? currentStream;
    protected bool currentPartSplitAfter;

    public byte[]? CurrentCrc { get; protected set; }

    /// <summary>
    /// Validates a volume part read. <see cref="Stream.Read"/> never returns a negative value;
    /// EOF is signaled by 0. A zero read before the part's expected end is a truncated volume.
    /// </summary>
    protected static void ValidateVolumeRead(int read, long currentPosition, long maxPosition)
    {
        if (read == 0 && currentPosition < maxPosition)
        {
            throw new IncompleteArchiveException(
                $"Unexpected end of stream reading RAR volume part. Expected {maxPosition} bytes, got {currentPosition}."
            );
        }
    }

    protected void ResetPartState(long partCompressedSize, Stream stream, bool isSplitAfter)
    {
        DisposeOwnedPartStream();
        maxPosition = partCompressedSize;
        currentPosition = 0;
        remainingInPart = partCompressedSize;
        currentStream = stream;
        currentPartSplitAfter = isSplitAfter;
    }

    protected void DisposeOwnedPartStream()
    {
        if (currentStream is RarCryptoWrapper)
        {
            currentStream.Dispose();
        }
    }

    protected int GetReadSize(int requestedCount)
    {
        if (remainingInPart <= 0)
        {
            return 0;
        }

        return requestedCount > remainingInPart ? (int)remainingInPart : requestedCount;
    }

    protected void AdvanceAfterRead(int read)
    {
        currentPosition += read;
        remainingInPart -= read;
    }

    protected bool ShouldSwitchPart => remainingInPart == 0 && currentPartSplitAfter;
}
