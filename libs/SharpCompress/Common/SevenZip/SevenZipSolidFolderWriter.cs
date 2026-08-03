using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Crypto;

namespace SharpCompress.Common.SevenZip;

/// <summary>
/// Accumulates multiple file payloads into a single LZMA/LZMA2 stream (a solid folder).
/// The underlying compressor stream is kept open across <see cref="AddFile" /> calls so
/// that all files share one compression context. On <see cref="Complete" /> the folder is
/// finalized and per-file substream sizes/CRCs are recorded for the header writer.
/// </summary>
internal sealed class SevenZipSolidFolderWriter : IDisposable, IAsyncDisposable
{
    private readonly Stream _outputStream;
    private readonly long _outStartOffset;
    private readonly bool _isLzma2;
    private readonly Crc32Stream _outCrcStream;
    private readonly Stream _encoderStream;

    // Per-file (substream) uncompressed sizes and CRCs, in the order files were added.
    private readonly List<ulong> _fileSizes = new();
    private readonly List<uint?> _fileCrcs = new();
    private ulong _totalUnpackSize;
    private bool _completed;
    private uint? _packedCrc;

    public SevenZipSolidFolderWriter(
        Stream outputStream,
        CompressionType compressionType,
        LzmaEncoderProperties? encoderProperties
    )
    {
        _outputStream = outputStream;
        _outStartOffset = outputStream.Position;
        _isLzma2 = compressionType == CompressionType.LZMA2;
        encoderProperties ??= new LzmaEncoderProperties(eos: !_isLzma2);

        // Crc32Stream computes the packed-stream CRC without disposing the output stream.
        _outCrcStream = new Crc32Stream(outputStream);
        _encoderStream = _isLzma2
            ? new Lzma2EncoderStream(
                _outCrcStream,
                encoderProperties.DictionarySize,
                encoderProperties.NumFastBytes
            )
            : LzmaStream.Create(encoderProperties, false, _outCrcStream);
    }

    /// <summary>Number of files added to the folder so far.</summary>
    public int FileCount => _fileSizes.Count;

    /// <summary>
    /// Compresses a file's payload into the shared folder, recording its uncompressed size and CRC.
    /// </summary>
    public void AddFile(Stream source)
    {
        SevenZipStreamsCompressor.CopyWithCrc(
            source,
            _encoderStream,
            out var crc,
            out var bytesRead
        );
        _fileSizes.Add((ulong)bytesRead);
        _fileCrcs.Add(crc);
        _totalUnpackSize += (ulong)bytesRead;
    }

    /// <summary>
    /// Asynchronously compresses a file's payload into the shared folder.
    /// </summary>
    public async ValueTask AddFileAsync(Stream source, CancellationToken cancellationToken)
    {
        var (crc, bytesRead) = await SevenZipStreamsCompressor
            .CopyWithCrcAsync(source, _encoderStream, cancellationToken)
            .ConfigureAwait(false);
        _fileSizes.Add((ulong)bytesRead);
        _fileCrcs.Add(crc);
        _totalUnpackSize += (ulong)bytesRead;
    }

    /// <summary>
    /// Closes the compressor stream and builds the folder metadata with one substream per file.
    /// </summary>
    public PackedStream Complete()
    {
        DisposeEncoder();
        return BuildPackedStream();
    }

    /// <summary>
    /// Asynchronously closes the compressor stream and builds the folder metadata.
    /// </summary>
    public async ValueTask<PackedStream> CompleteAsync()
    {
        await DisposeEncoderAsync().ConfigureAwait(false);
        return BuildPackedStream();
    }

    private void DisposeEncoder()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        // Disposing flushes the final chunk and writes the LZMA end marker (LZMA only).
        _encoderStream.Dispose();
        _packedCrc = _outCrcStream.Crc;
        _outCrcStream.Dispose();
    }

    private async ValueTask DisposeEncoderAsync()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        await _encoderStream.DisposeAsync().ConfigureAwait(false);
        _packedCrc = _outCrcStream.Crc;
        await _outCrcStream.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeEncoder();

    public ValueTask DisposeAsync() => DisposeEncoderAsync();

    private PackedStream BuildPackedStream()
    {
        var properties = _encoderStream is Lzma2EncoderStream lzma2
            ? lzma2.Properties
            : ((LzmaStream)_encoderStream).Properties;

        var compressedSize = (ulong)(_outputStream.Position - _outStartOffset);

        var folder = new CFolder();
        folder._coders.Add(
            new CCoderInfo
            {
                _methodId = _isLzma2 ? CMethodId.K_LZMA2 : CMethodId.K_LZMA,
                _numInStreams = 1,
                _numOutStreams = 1,
                _props = properties,
            }
        );
        folder._packStreams.Add(0);
        folder._unpackSizes.Add((long)_totalUnpackSize);
        // Folder-level CRC is intentionally left undefined; per-file CRCs live in SubStreamsInfo.

        return new PackedStream
        {
            Folder = folder,
            Sizes = [compressedSize],
            CRCs = [_packedCrc],
            UnpackSizes = _fileSizes.ToArray(),
            UnpackCRCs = _fileCrcs.ToArray(),
        };
    }
}
