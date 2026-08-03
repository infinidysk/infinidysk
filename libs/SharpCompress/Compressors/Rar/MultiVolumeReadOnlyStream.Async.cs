using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar;

namespace SharpCompress.Compressors.Rar;

internal sealed partial class MultiVolumeReadOnlyStream : MultiVolumeReadOnlyStreamBase
{
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (count == 0)
        {
            return 0;
        }

        var totalRead = 0;
        var currentOffset = offset;
        var currentCount = count;
        while (currentCount > 0)
        {
            var readSize = GetReadSize(currentCount);
            if (readSize == 0)
            {
                break;
            }

            var read = await currentStream
                .NotNull()
                .ReadAsync(buffer.AsMemory(currentOffset, readSize), cancellationToken)
                .ConfigureAwait(false);
            ValidateVolumeRead(read, currentPosition, maxPosition);

            AdvanceAfterRead(read);
            currentOffset += read;
            currentCount -= read;
            totalRead += read;

            if (!ShouldSwitchPart)
            {
                break;
            }

            if (filePartEnumerator.Current.FileHeader.R4Salt != null)
            {
                throw new InvalidFormatException(
                    "Sharpcompress currently does not support multi-volume decryption."
                );
            }

            var fileName = filePartEnumerator.Current.FileHeader.FileName;
            if (!filePartEnumerator.MoveNext())
            {
                throw new InvalidFormatException(
                    "Multi-part rar file is incomplete.  Entry expects a new volume: " + fileName
                );
            }

            InitializeNextFilePart();
        }

        return totalRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var totalRead = 0;
        var currentOffset = 0;
        var currentCount = buffer.Length;
        while (currentCount > 0)
        {
            var readSize = GetReadSize(currentCount);
            if (readSize == 0)
            {
                break;
            }

            var read = await currentStream
                .NotNull()
                .ReadAsync(buffer.Slice(currentOffset, readSize), cancellationToken)
                .ConfigureAwait(false);
            ValidateVolumeRead(read, currentPosition, maxPosition);

            AdvanceAfterRead(read);
            currentOffset += read;
            currentCount -= read;
            totalRead += read;

            if (!ShouldSwitchPart)
            {
                break;
            }

            if (filePartEnumerator.Current.FileHeader.R4Salt != null)
            {
                throw new InvalidFormatException(
                    "Sharpcompress currently does not support multi-volume decryption."
                );
            }

            var fileName = filePartEnumerator.Current.FileHeader.FileName;
            if (!filePartEnumerator.MoveNext())
            {
                throw new InvalidFormatException(
                    "Multi-part rar file is incomplete.  Entry expects a new volume: " + fileName
                );
            }

            InitializeNextFilePart();
        }

        return totalRead;
    }
}
