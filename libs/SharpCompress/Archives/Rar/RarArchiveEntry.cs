using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Compressors.Rar;
using SharpCompress.Readers;

namespace SharpCompress.Archives.Rar;

public partial class RarArchiveEntry : RarEntry, IArchiveEntry
{
    private readonly List<RarFilePart> parts;
    private readonly RarArchive archive;
    private readonly ReaderOptions readerOptions;
    private readonly FileHeader _fileHeader;
    private long? _crc;
    private long? _compressedSize;
    private bool? _isComplete;

    internal RarArchiveEntry(
        RarArchive archive,
        IEnumerable<RarFilePart> parts,
        ReaderOptions readerOptions
    )
        : base(readerOptions)
    {
        this.parts = parts.ToList();
        this.archive = archive;
        this.readerOptions = readerOptions;
        _fileHeader = this.parts[0].FileHeader;
        IsSolid = _fileHeader.IsSolid;
    }

    public override CompressionType CompressionType => CompressionType.Rar;

    public IArchive Archive => archive;

    internal override IEnumerable<FilePart> Parts => parts.Cast<FilePart>();

    internal override FileHeader FileHeader => _fileHeader;

    public override long Crc
    {
        get
        {
            CheckIncomplete();
            if (_crc is null)
            {
                FileHeader? match = null;
                foreach (var part in parts)
                {
                    var header = part.FileHeader;
                    if (!header.IsSplitAfter)
                    {
                        if (match is not null)
                        {
                            throw new InvalidOperationException(
                                "Sequence contains more than one matching element"
                            );
                        }
                        match = header;
                    }
                }
                if (match is null)
                {
                    throw new InvalidOperationException("Sequence contains no matching element");
                }
                _crc = BinaryPrimitives.ReadUInt32LittleEndian(match.FileCrc.NotNull().AsSpan());
            }
            return _crc.Value;
        }
    }

    public override long Size
    {
        get
        {
            CheckIncomplete();
            return _fileHeader.UncompressedSize;
        }
    }

    public override long CompressedSize
    {
        get
        {
            CheckIncomplete();
            if (_compressedSize is null)
            {
                long total = 0;
                foreach (var part in parts)
                {
                    total += part.FileHeader.CompressedSize;
                }
                _compressedSize = total;
            }
            return _compressedSize.Value;
        }
    }

    public Stream OpenEntryStream()
    {
        // Fast path: stored (m0), RAR5 encrypted, non-solid entries over seekable volumes.
        if (
            !archive.IsSolid
            && EncryptedStoredRarEntryStream.TryCreate(parts, out var encryptedStoredStream)
        )
        {
            return encryptedStoredStream;
        }

        // Fast path: stored (m0), non-encrypted, non-solid entries over seekable volumes.
        // Solid archives keep the slow path so solid single-stream accounting is unchanged.
        if (!archive.IsSolid && StoredRarEntryStream.TryCreate(parts, out var storedStream))
        {
            return storedStream;
        }

        var readStream = new MultiVolumeReadOnlyStream(parts);
        var isSolidArchive = archive.IsSolid;
        var unpack = archive.AcquireUnpackForEntry(IsRarV3, isSolidArchive, out var ownsUnpack);
        Action? onDispose = isSolidArchive ? archive.ReleaseSolidEntryStream : null;
        RarStream? stream = null;
        try
        {
            if (IsRarV3)
            {
                stream = RarCrcStream.Create(unpack, FileHeader, readStream, ownsUnpack, onDispose);
            }
            else if (FileHeader.FileCrc?.Length > 5)
            {
                stream = RarBLAKE2spStream.Create(
                    unpack,
                    FileHeader,
                    readStream,
                    ownsUnpack,
                    onDispose
                );
            }
            else
            {
                stream = RarCrcStream.Create(unpack, FileHeader, readStream, ownsUnpack, onDispose);
            }

            stream.Initialize();
            return stream;
        }
        catch
        {
            if (stream is not null)
            {
                stream.Dispose();
            }
            else
            {
                readStream.Dispose();
                if (ownsUnpack && unpack is IDisposable disposableUnpack)
                {
                    disposableUnpack.Dispose();
                }

                onDispose?.Invoke();
            }

            throw;
        }
    }

    public bool IsComplete
    {
        get
        {
            _isComplete ??=
                !parts[0].FileHeader.IsSplitBefore && !parts[^1].FileHeader.IsSplitAfter;
            return _isComplete.Value;
        }
    }

    private void CheckIncomplete()
    {
        if (!readerOptions.DisableCheckIncomplete && !IsComplete)
        {
            throw new IncompleteArchiveException(
                "ArchiveEntry is incomplete and cannot perform this operation."
            );
        }
    }
}
