using System;
using System.Collections.Generic;
using SharpCompress.Common.Options;

namespace SharpCompress.Common.SevenZip;

public class SevenZipEntry : Entry
{
    private readonly SevenZipFilePart? _filePart;

    internal SevenZipEntry(SevenZipFilePart? filePart, IReaderOptions readerOptions)
        : base(readerOptions)
    {
        _filePart = filePart;
    }

    // Non-null for entries loaded from an existing archive. Writable (pending) entries created
    // through the Archive write API have no backing file part and override every member that
    // would otherwise read it, so the null-forgiving access here is never reached for them.
    internal SevenZipFilePart FilePart => _filePart!;

    public override CompressionType CompressionType => FilePart.CompressionType;

    public override long Crc => FilePart.Header.Crc ?? 0;

    internal override ChecksumDescriptor Checksum
    {
        get
        {
            if (
                IsDirectory
                || FilePart.Header.IsAnti
                || !FilePart.Header.HasStream
                || !FilePart.Header.Crc.HasValue
            )
            {
                return default;
            }

            return new ChecksumDescriptor(
                ChecksumKind.Crc32,
                FilePart.Header.Crc.Value,
                IsAvailable: true
            );
        }
    }

    public override string? Key => FilePart.Header.Name;

    public override string? LinkTarget => null;

    public override long CompressedSize => 0;

    public override long Size => FilePart.Header.Size;

    public override DateTime? LastModifiedTime => FilePart.Header.MTime;

    public override DateTime? CreatedTime => null;

    public override DateTime? LastAccessedTime => null;

    public override DateTime? ArchivedTime => null;

    public override bool IsEncrypted => FilePart.IsEncrypted;

    public override bool IsDirectory => FilePart.Header.IsDir;

    public override bool IsSplitAfter => false;

    public override int? Attrib =>
        FilePart.Header.Attrib.HasValue ? (int?)FilePart.Header.Attrib.Value : null;

    public int? ExtendedAttrib =>
        FilePart.Header.ExtendedAttrib.HasValue ? (int?)FilePart.Header.ExtendedAttrib.Value : null;

    internal override IEnumerable<FilePart> Parts => [FilePart];
}
