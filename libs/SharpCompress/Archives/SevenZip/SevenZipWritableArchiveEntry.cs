using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;

namespace SharpCompress.Archives.SevenZip;

/// <summary>
/// A pending entry added to a <see cref="SevenZipArchive"/> through the writable Archive API.
/// It has no backing 7z file part; its payload lives in the supplied stream and is compressed
/// when the archive is written out via SaveTo.
/// </summary>
internal sealed class SevenZipWritableArchiveEntry : SevenZipArchiveEntry, IWritableArchiveEntry
{
    private readonly bool closeStream;
    private readonly Stream? stream;
    private readonly bool isDirectory;
    private bool isDisposed;

    internal SevenZipWritableArchiveEntry(
        SevenZipArchive archive,
        Stream stream,
        string path,
        long size,
        DateTime? lastModified,
        bool closeStream
    )
        : base(archive, null, archive.ReaderOptions)
    {
        this.stream = stream;
        Key = path;
        Size = size;
        LastModifiedTime = lastModified;
        this.closeStream = closeStream;
        isDirectory = false;
    }

    internal SevenZipWritableArchiveEntry(
        SevenZipArchive archive,
        string directoryPath,
        DateTime? lastModified
    )
        : base(archive, null, archive.ReaderOptions)
    {
        stream = null;
        Key = directoryPath;
        Size = 0;
        LastModifiedTime = lastModified;
        closeStream = false;
        isDirectory = true;
    }

    public override CompressionType CompressionType => CompressionType.Unknown;

    public override long Crc => 0;

    internal override ChecksumDescriptor Checksum => default;

    public override string Key { get; }

    public override long CompressedSize => 0;

    public override long Size { get; }

    public override DateTime? LastModifiedTime { get; }

    public override DateTime? CreatedTime => null;

    public override DateTime? LastAccessedTime => null;

    public override DateTime? ArchivedTime => null;

    public override bool IsEncrypted => false;

    public override bool IsDirectory => isDirectory;

    public override bool IsSplitAfter => false;

    public override bool IsAnti => false;

    public override int? Attrib => null;

    internal override IEnumerable<FilePart> Parts => throw new NotImplementedException();

    public override bool TryGetPackedByteRange(out long startOffset, out long length)
    {
        startOffset = 0;
        length = 0;
        return false;
    }

    public override long FolderStartOffset => 0;

    public override byte[]? AesCoderProperties => null;

    Stream IWritableArchiveEntry.Stream => stream ?? Stream.Null;

    public override Stream OpenEntryStream()
    {
        if (stream is null)
        {
            return Stream.Null;
        }
        //ensure new stream is at the start, this could be reset
        stream.Seek(0, SeekOrigin.Begin);
        return SharpCompressStream.CreateNonDisposing(stream);
    }

    public override ValueTask<Stream> OpenEntryStreamAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(OpenEntryStream());
    }

    internal override void Close()
    {
        if (closeStream && !isDisposed && stream is not null)
        {
            stream.Dispose();
            isDisposed = true;
        }
    }
}
