using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Common;
using SharpCompress.Common.Rar;

namespace SharpCompress.Compressors.Rar;

internal sealed partial class MultiVolumeReadOnlyStream : MultiVolumeReadOnlyStreamBase
{
    private IEnumerator<RarFilePart> filePartEnumerator;

    internal MultiVolumeReadOnlyStream(IEnumerable<RarFilePart> parts)
    {
        filePartEnumerator = parts.GetEnumerator();
        if (!filePartEnumerator.MoveNext())
        {
            filePartEnumerator.Dispose();
            throw new InvalidOperationException("parts must not be empty");
        }

        InitializeNextFilePart();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            DisposeOwnedPartStream();
            filePartEnumerator.Dispose();
            currentStream = null;
        }
    }

    private void InitializeNextFilePart()
    {
        var part = filePartEnumerator.Current;
        ResetPartState(
            part.FileHeader.CompressedSize,
            part.GetCompressedStream().NotNull(),
            part.FileHeader.IsSplitAfter
        );
        CurrentCrc = part.FileHeader.FileCrc;
    }

    public override int Read(byte[] buffer, int offset, int count)
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

            var read = currentStream.NotNull().Read(buffer, currentOffset, readSize);
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

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var totalRead = 0;
        while (buffer.Length > 0)
        {
            var readSize = GetReadSize(buffer.Length);
            if (readSize == 0)
            {
                break;
            }

            var read = currentStream.NotNull().Read(buffer.Slice(0, readSize));
            ValidateVolumeRead(read, currentPosition, maxPosition);

            AdvanceAfterRead(read);
            buffer = buffer.Slice(read);
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

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override void Flush() { }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
