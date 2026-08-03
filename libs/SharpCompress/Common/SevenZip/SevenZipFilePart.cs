using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Common.SevenZip;

internal class SevenZipFilePart : FilePart
{
    private CompressionType? _type;
    private bool? _isEncrypted;
    private readonly Stream _stream;
    private readonly ArchiveDatabase _database;

    internal SevenZipFilePart(
        Stream stream,
        ArchiveDatabase database,
        int index,
        CFileItem fileEntry,
        IArchiveEncoding archiveEncoding
    )
        : base(archiveEncoding)
    {
        _stream = stream;
        _database = database;
        Index = index;
        Header = fileEntry;
        if (Header.HasStream)
        {
            FolderIndex = database._fileIndexToFolderIndexMap[index];
            Folder = database._folders[FolderIndex];
        }
        else
        {
            FolderIndex = -1;
        }
    }

    internal CFileItem Header { get; }
    internal CFolder? Folder { get; }
    internal int FolderIndex { get; }

    internal override string FilePartName => Header.Name;

    internal override Stream? GetRawStream() => null;

    internal override Stream GetCompressedStream()
    {
        if (!Header.HasStream)
        {
            return Stream.Null;
        }

        var skipSize = _database._fileInFolderOffset[Index];
        return _database.GetFolderStreamCached(
            _stream,
            FolderIndex,
            skipSize,
            Header.Size,
            _database.PasswordProvider
        );
    }

    internal override async ValueTask<Stream?> GetCompressedStreamAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!Header.HasStream)
        {
            return Stream.Null;
        }

        var skipSize = _database._fileInFolderOffset[Index];
        return await _database
            .GetFolderStreamCachedAsync(
                _stream,
                FolderIndex,
                skipSize,
                Header.Size,
                _database.PasswordProvider,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public CompressionType CompressionType
    {
        get
        {
            _type ??= GetCompression();
            return _type.Value;
        }
    }

    private const uint K_COPY = 0x0;
    private const uint K_LZMA2 = 0x21;
    private const uint K_LZMA = 0x030101;
    private const uint K_PPMD = 0x030401;
    private const uint K_B_ZIP2 = 0x040202;

    private CompressionType GetCompression()
    {
        if (Header.IsDir)
        {
            return CompressionType.None;
        }

        // Report the primary non-crypto coder. AES-first chains (encrypted stored/compressed
        // entries) must not throw: AES + Copy resolves to None, AES + LZMA to LZMA, etc.
        var coder = GetPrimaryCoder();
        return coder._methodId._id switch
        {
            K_COPY => CompressionType.None,
            K_LZMA or K_LZMA2 => CompressionType.LZMA,
            K_PPMD => CompressionType.PPMd,
            K_B_ZIP2 => CompressionType.BZip2,
            _ => throw new InvalidFormatException(),
        };
    }

    private CCoderInfo GetPrimaryCoder()
    {
        var coders = Folder.NotNull()._coders;
        foreach (var candidate in coders)
        {
            if (candidate._methodId._id != CMethodId.K_AES_ID)
            {
                return candidate;
            }
        }

        // Folder contained only an AES coder (no payload coder); treat as stored.
        return coders[0];
    }

    /// <summary>
    /// Absolute stream offset of this entry's folder pack stream
    /// (<c>_dataStartPosition + _packStreamStartPositions[folder._firstPackStreamId]</c>).
    /// </summary>
    internal long FolderStartOffset => Folder is null ? 0 : _database.GetFolderStreamPos(Folder, 0);

    /// <summary>
    /// Raw properties of the AES coder (<c>0x06F10701</c>) for this entry's folder, or
    /// <c>null</c> when the entry is not AES-encrypted.
    /// </summary>
    internal byte[]? AesCoderProperties
    {
        get
        {
            if (Folder is null)
            {
                return null;
            }

            foreach (var coder in Folder._coders)
            {
                if (coder._methodId._id == CMethodId.K_AES_ID)
                {
                    return coder._props;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Computes the absolute packed byte range for entries with a contiguous stored payload:
    /// a Copy-only folder (range = folder start + in-folder offset, entry size), or an
    /// AES + Copy folder holding a single file (range = whole folder pack range incl. AES
    /// padding). Returns false for compressed or multi-file AES folders.
    /// </summary>
    internal bool TryGetPackedByteRange(out long startOffset, out long length)
    {
        startOffset = 0;
        length = 0;

        if (!Header.HasStream || Folder is null)
        {
            return false;
        }

        var coders = Folder._coders;

        if (coders.Count == 1 && coders[0]._methodId._id == CMethodId.K_COPY_ID)
        {
            startOffset =
                _database.GetFolderStreamPos(Folder, 0) + _database._fileInFolderOffset[Index];
            length = Header.Size;
            return true;
        }

        if (
            coders.Count == 2
            && HasCoder(coders, CMethodId.K_AES_ID)
            && HasCoder(coders, CMethodId.K_COPY_ID)
            && _database._numUnpackStreamsVector[FolderIndex] == 1
        )
        {
            startOffset = _database.GetFolderStreamPos(Folder, 0);
            length = _database.GetFolderFullPackSize(FolderIndex);
            return true;
        }

        return false;
    }

    private static bool HasCoder(System.Collections.Generic.List<CCoderInfo> coders, ulong methodId)
    {
        foreach (var coder in coders)
        {
            if (coder._methodId._id == methodId)
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsEncrypted
    {
        get
        {
            _isEncrypted ??=
                !Header.IsDir
                && Folder?._coders.FindIndex(c => c._methodId._id == CMethodId.K_AES_ID) != -1;
            return _isEncrypted.Value;
        }
    }
}
