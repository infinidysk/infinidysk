using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common.Options;
using SharpCompress.Common.SevenZip;

namespace SharpCompress.Archives.SevenZip;

public class SevenZipArchiveEntry : SevenZipEntry, IArchiveEntry
{
    internal SevenZipArchiveEntry(
        SevenZipArchive archive,
        SevenZipFilePart? part,
        IReaderOptions readerOptions
    )
        : base(part, readerOptions) => Archive = archive;

    public virtual Stream OpenEntryStream() => FilePart.GetCompressedStream();

    public virtual async ValueTask<Stream> OpenEntryStreamAsync(
        CancellationToken cancellationToken = default
    ) =>
        (
            await FilePart.GetCompressedStreamAsync(cancellationToken).ConfigureAwait(false)
        ).NotNull();

    public IArchive Archive { get; }

    public bool IsComplete => true;

    /// <summary>
    /// This is a 7Zip Anti item
    /// </summary>
    public virtual bool IsAnti => FilePart.Header.IsAnti;

    /// <summary>
    /// Absolute byte range of this entry's packed bytes within the archive stream, for entries
    /// with a contiguous stored payload. True when the folder coders are exactly <c>[Copy]</c>
    /// (range = folder start + in-folder offset, <see cref="Common.Entry.Size"/>), or
    /// <c>[AES, Copy]</c> with a single file in the folder (range = whole folder pack range
    /// including AES padding). False otherwise (compressed, or multi-file AES folder).
    /// </summary>
    public virtual bool TryGetPackedByteRange(out long startOffset, out long length) =>
        FilePart.TryGetPackedByteRange(out startOffset, out length);

    /// <summary>
    /// Absolute stream offset of the pack stream for this entry's folder.
    /// </summary>
    public virtual long FolderStartOffset => FilePart.FolderStartOffset;

    /// <summary>
    /// Raw properties of the AES coder (<c>0x06F10701</c>) when this entry is AES-encrypted,
    /// otherwise <c>null</c>.
    /// </summary>
    public virtual byte[]? AesCoderProperties => FilePart.AesCoderProperties;
}
