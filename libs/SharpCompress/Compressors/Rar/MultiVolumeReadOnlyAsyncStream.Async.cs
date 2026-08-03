using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar;

namespace SharpCompress.Compressors.Rar;

internal sealed partial class MultiVolumeReadOnlyAsyncStream : MultiVolumeReadOnlyStreamBase
{
    internal static async ValueTask<MultiVolumeReadOnlyAsyncStream> Create(
        IAsyncEnumerable<RarFilePart> parts
    )
    {
        var stream = new MultiVolumeReadOnlyAsyncStream(parts);
        if (!await stream.filePartEnumerator.MoveNextAsync().ConfigureAwait(false))
        {
            await stream.filePartEnumerator.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("parts must not be empty");
        }

        stream.InitializeNextFilePart();
        return stream;
    }

    internal static ValueTask<MultiVolumeReadOnlyAsyncStream> Create(IEnumerable<RarFilePart> parts)
    {
        var enumerator = parts.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            enumerator.Dispose();
            throw new InvalidOperationException("parts must not be empty");
        }

        var stream = new MultiVolumeReadOnlyAsyncStream(enumerator);
        stream.InitializeNextFilePart();
        return new ValueTask<MultiVolumeReadOnlyAsyncStream>(stream);
    }

    public override async ValueTask DisposeAsync()
    {
        DisposeOwnedPartStream();
        if (filePartEnumerator is not null)
        {
            await filePartEnumerator.DisposeAsync().ConfigureAwait(false);
        }

        currentStream = null;
        await base.DisposeAsync().ConfigureAwait(false);
    }

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
            if (!await filePartEnumerator.MoveNextAsync().ConfigureAwait(false))
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
            if (!await filePartEnumerator.MoveNextAsync().ConfigureAwait(false))
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
