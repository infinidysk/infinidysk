using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Crypto;
using SharpCompress.IO;

namespace SharpCompress.Writers.SevenZip;

/// <summary>
/// Writes 7z archives. By default each file is compressed independently; enabling
/// <see cref="SevenZipWriterOptions.Solid" /> shares one compressor/folder across
/// consecutive non-empty files. Requires a seekable output stream for back-patching
/// the signature header.
/// </summary>
public partial class SevenZipWriter : AbstractWriter
{
    private readonly SevenZipWriterOptions sevenZipOptions;
    private readonly List<SevenZipWriteEntry> entries = [];
    private readonly List<PackedStream> packedStreams = [];

    // Active solid folder accumulating consecutive non-empty files (null when not in solid mode
    // or before the first non-empty file). Completed on finalize.
    private SevenZipSolidFolderWriter? solidFolder;
    private bool finalized;
    private bool _placeholderWritten;

    /// <summary>
    /// Creates a new SevenZipWriter writing to the specified stream.
    /// </summary>
    /// <param name="destination">Seekable output stream.</param>
    /// <param name="options">Writer options.</param>
    public SevenZipWriter(Stream destination, SevenZipWriterOptions options)
        : base(ArchiveType.SevenZip, options)
    {
        if (!destination.CanSeek)
        {
            throw new ArchiveOperationException(
                "7z writing requires a seekable stream for header back-patching."
            );
        }

        sevenZipOptions = options;

        if (options.LeaveStreamOpen)
        {
            destination = SharpCompressStream.CreateNonDisposing(destination);
        }

        InitializeStream(destination);
    }

    /// <summary>
    /// Ensures the placeholder signature header has been written synchronously.
    /// Called before the first sync write.
    /// </summary>
    private void EnsurePlaceholderWritten()
    {
        if (!_placeholderWritten)
        {
            _placeholderWritten = true;
            // Write placeholder signature header (32 bytes) - will be back-patched on finalize
            SevenZipSignatureHeaderWriter.WritePlaceholder(OutputStream.NotNull());
        }
    }

    /// <summary>
    /// Ensures the placeholder signature header has been written asynchronously.
    /// Called before the first async write.
    /// </summary>
    private async ValueTask EnsurePlaceholderWrittenAsync(CancellationToken cancellationToken)
    {
        if (!_placeholderWritten)
        {
            _placeholderWritten = true;
            // Write placeholder signature header (32 bytes) - will be back-patched on finalize
            await SevenZipSignatureHeaderWriter
                .WritePlaceholderAsync(OutputStream.NotNull(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a file entry to the archive.
    /// </summary>
    public override void Write(string filename, Stream source, DateTime? modificationTime)
    {
        if (finalized)
        {
            throw new ObjectDisposedException(
                nameof(SevenZipWriter),
                "Cannot write to a finalized archive."
            );
        }

        EnsurePlaceholderWritten();

        filename = NormalizeFilename(filename);
        var progressStream = WrapWithProgress(source, filename);

        var isEmpty = source.CanSeek && source.Length == 0;

        if (isEmpty)
        {
            // Empty file - no compression, just record metadata.
            // Empty files never enter a solid folder (7z emptyStream convention).
            entries.Add(
                new SevenZipWriteEntry
                {
                    Name = filename,
                    ModificationTime = modificationTime,
                    IsDirectory = false,
                    IsEmpty = true,
                }
            );
            return;
        }

        if (sevenZipOptions.Solid)
        {
            // Accumulate the payload into the shared solid folder. Per-file size/CRC are
            // recorded as substreams and the folder is completed on finalize.
            solidFolder ??= new SevenZipSolidFolderWriter(
                OutputStream.NotNull(),
                sevenZipOptions.CompressionType,
                sevenZipOptions.LzmaProperties
            );
            solidFolder.AddFile(progressStream);

            entries.Add(
                new SevenZipWriteEntry
                {
                    Name = filename,
                    ModificationTime = modificationTime,
                    IsDirectory = false,
                    IsEmpty = false,
                }
            );
            return;
        }

        // Non-solid: compress file data to its own folder.
        var output = OutputStream.NotNull();
        var outputPosBefore = output.Position;
        var compressor = new SevenZipStreamsCompressor(output);
        var packed = compressor.Compress(
            progressStream,
            sevenZipOptions.CompressionType,
            sevenZipOptions.LzmaProperties
        );

        // Check if the stream was actually empty (handles non-seekable streams with no data)
        var actuallyEmpty = packed.Folder.GetUnpackSize() == 0;
        if (!actuallyEmpty)
        {
            packedStreams.Add(packed);
        }
        else
        {
            // Rewind output to erase orphaned encoder header/end-marker bytes
            // so they don't shift subsequent pack stream offsets
            output.Position = outputPosBefore;
            output.SetLength(outputPosBefore);
        }

        entries.Add(
            new SevenZipWriteEntry
            {
                Name = filename,
                ModificationTime = modificationTime,
                IsDirectory = false,
                IsEmpty = isEmpty || actuallyEmpty,
            }
        );
    }

    /// <summary>
    /// Writes a directory entry to the archive.
    /// </summary>
    public override void WriteDirectory(string directoryName, DateTime? modificationTime)
    {
        if (finalized)
        {
            throw new ObjectDisposedException(
                nameof(SevenZipWriter),
                "Cannot write to a finalized archive."
            );
        }

        directoryName = NormalizeFilename(directoryName);
        directoryName = directoryName.TrimEnd('/');

        entries.Add(
            new SevenZipWriteEntry
            {
                Name = directoryName,
                ModificationTime = modificationTime,
                IsDirectory = true,
                IsEmpty = true,
                Attributes = 0x10, // FILE_ATTRIBUTE_DIRECTORY
            }
        );
    }

    /// <summary>
    /// Finalizes the archive - writes metadata headers and back-patches the signature header.
    /// </summary>
    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing && !finalized && !_isDisposed)
        {
            finalized = true;
            FinalizeArchive();
        }
        base.Dispose(isDisposing);
    }

    private void FinalizeArchive()
    {
        var output = OutputStream.NotNull();

        // Close any open solid folder before computing the packed-data boundary.
        if (solidFolder is { FileCount: > 0 })
        {
            packedStreams.Add(solidFolder.Complete());
            solidFolder = null;
        }

        // Current position = end of packed data streams
        var endOfPackedData = output.Position;

        // Build the header structures
        var mainStreamsInfo = BuildStreamsInfo();
        var filesInfo = new SevenZipFilesInfoWriter { Entries = entries.ToArray() };

        // Write header to a temporary stream first
        using var headerStream = new PooledMemoryStream();
        ArchiveHeaderWriter.WriteRawHeader(headerStream, mainStreamsInfo, filesInfo);

        // Optionally compress the header
        if (sevenZipOptions.CompressHeader && headerStream.Length > 0)
        {
            WriteCompressedHeader(headerStream, endOfPackedData);
        }
        else
        {
            WriteRawHeaderToOutput(headerStream, endOfPackedData);
        }
    }

    private void WriteCompressedHeader(MemoryStream rawHeaderStream, long endOfPackedData)
    {
        var output = OutputStream.NotNull();

        // Compress header using LZMA (always LZMA, not LZMA2, matching 7-Zip standard behavior)
        rawHeaderStream.Position = 0;
        var headerCompressor = new SevenZipStreamsCompressor(output);
        var headerPacked = headerCompressor.Compress(
            rawHeaderStream,
            CompressionType.LZMA,
            sevenZipOptions.LzmaProperties
        );

        // Build EncodedHeader StreamsInfo (describes how to decompress the header)
        var headerPackPos = (ulong)(endOfPackedData - SevenZipSignatureHeaderWriter.HeaderSize);
        var headerStreamsInfo = new SevenZipStreamsInfoWriter
        {
            PackInfo = new SevenZipPackInfoWriter
            {
                PackPos = headerPackPos,
                Sizes = headerPacked.Sizes,
                CRCs = headerPacked.CRCs,
            },
            UnPackInfo = new SevenZipUnPackInfoWriter { Folders = [headerPacked.Folder] },
        };

        // Write encoded header to a second temporary stream
        using var encodedHeaderStream = new PooledMemoryStream();
        ArchiveHeaderWriter.WriteEncodedHeader(encodedHeaderStream, headerStreamsInfo);

        // Write the encoded header to the output
        var headerStartPos = output.Position;
        encodedHeaderStream.Position = 0;
        encodedHeaderStream.CopyTo(output);

        // Compute CRC of the encoded header without allocating a contiguous buffer
        var encodedHeaderCrcSink = new Crc32Stream(Stream.Null);
        encodedHeaderStream.WriteTo(encodedHeaderCrcSink);
        var headerCrc = encodedHeaderCrcSink.Crc;

        // Back-patch signature header
        var nextHeaderOffset = (ulong)(headerStartPos - SevenZipSignatureHeaderWriter.HeaderSize);
        var nextHeaderSize = (ulong)encodedHeaderStream.Length;

        SevenZipSignatureHeaderWriter.WriteFinal(
            output,
            nextHeaderOffset,
            nextHeaderSize,
            headerCrc
        );

        // Seek to end
        output.Seek(0, SeekOrigin.End);
    }

    private void WriteRawHeaderToOutput(MemoryStream rawHeaderStream, long endOfPackedData)
    {
        var output = OutputStream.NotNull();

        // Write raw header directly
        var headerStartPos = output.Position;
        rawHeaderStream.Position = 0;
        rawHeaderStream.CopyTo(output);

        // Compute CRC of the raw header without allocating a contiguous buffer
        var rawHeaderCrcSink = new Crc32Stream(Stream.Null);
        rawHeaderStream.WriteTo(rawHeaderCrcSink);
        var headerCrc = rawHeaderCrcSink.Crc;

        // Back-patch signature header
        var nextHeaderOffset = (ulong)(headerStartPos - SevenZipSignatureHeaderWriter.HeaderSize);
        var nextHeaderSize = (ulong)rawHeaderStream.Length;

        SevenZipSignatureHeaderWriter.WriteFinal(
            output,
            nextHeaderOffset,
            nextHeaderSize,
            headerCrc
        );

        // Seek to end
        output.Seek(0, SeekOrigin.End);
    }

    private SevenZipStreamsInfoWriter? BuildStreamsInfo()
    {
        if (packedStreams.Count == 0)
        {
            return null;
        }

        // Collect all packed sizes and CRCs across all folders
        var totalPackStreams = 0;
        for (var i = 0; i < packedStreams.Count; i++)
        {
            totalPackStreams += packedStreams[i].Sizes.Length;
        }

        var allSizes = new ulong[totalPackStreams];
        var allCRCs = new uint?[totalPackStreams];
        var folders = new CFolder[packedStreams.Count];

        var sizeIndex = 0;
        for (var i = 0; i < packedStreams.Count; i++)
        {
            var ps = packedStreams[i];
            for (var j = 0; j < ps.Sizes.Length; j++)
            {
                allSizes[sizeIndex] = ps.Sizes[j];
                allCRCs[sizeIndex] = ps.CRCs[j];
                sizeIndex++;
            }
            folders[i] = ps.Folder;
        }

        // Build per-file unpack sizes and CRCs for SubStreamsInfo.
        // Non-solid folders carry a single substream; solid folders carry one per file.
        var numUnPackStreamsPerFolder = new ulong[packedStreams.Count];
        var unpackSizes = new List<ulong>();
        var fileCRCs = new List<uint?>();

        for (var i = 0; i < packedStreams.Count; i++)
        {
            var ps = packedStreams[i];
            numUnPackStreamsPerFolder[i] = (ulong)ps.UnpackSizes.Length;
            unpackSizes.AddRange(ps.UnpackSizes);
            fileCRCs.AddRange(ps.UnpackCRCs);

            // Clear folder-level CRC (it's moved to SubStreamsInfo)
            ps.Folder._unpackCrc = null;
        }

        return new SevenZipStreamsInfoWriter
        {
            PackInfo = new SevenZipPackInfoWriter
            {
                PackPos = 0,
                Sizes = allSizes,
                CRCs = allCRCs,
            },
            UnPackInfo = new SevenZipUnPackInfoWriter { Folders = folders },
            SubStreamsInfo = new SevenZipSubStreamsInfoWriter
            {
                Folders = folders,
                NumUnPackStreamsInFolders = numUnPackStreamsPerFolder,
                UnPackSizes = unpackSizes.ToArray(),
                CRCs = fileCRCs.ToArray(),
            },
        };
    }

    /// <summary>
    /// Normalizes a filename for 7z archive storage.
    /// Converts backslashes to forward slashes and removes leading slashes.
    /// </summary>
    private static string NormalizeFilename(string filename)
    {
        filename = filename.Replace('\\', '/');

        // Remove drive letter prefix (e.g., "C:/")
        if (filename.Length >= 3 && filename[1] == ':' && filename[2] == '/')
        {
            filename = filename.Substring(3);
        }

        // Remove leading slashes
        filename = filename.TrimStart('/');

        return filename;
    }
}
