using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Compressors.Deflate64;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.LZMA.Utilities;
using SharpCompress.IO;
using BlockType = SharpCompress.Compressors.LZMA.Utilities.BlockType;

namespace SharpCompress.Common.SevenZip;

internal sealed partial class ArchiveReader
{
    private async ValueTask ReadStartHeaderAtCurrentPositionAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        _header = new byte[StartHeaderSize];
        try
        {
            await stream
                .ReadExactlyAsync(_header.AsMemory(0, StartHeaderSize), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new IncompleteArchiveException("Unexpected end of stream.", e);
        }
    }

    private async ValueTask LocateAndReadStartHeaderAsync(
        Stream stream,
        bool lookForHeader,
        CancellationToken cancellationToken
    )
    {
        _streamOrigin = stream.Position;
        _streamEnding = stream.Length;

        if (!lookForHeader)
        {
            await ReadStartHeaderAtCurrentPositionAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (!HasSevenZipSignature(_header))
            {
                throw new InvalidFormatException("Invalid 7z signature");
            }

            ValidateStartHeaderCrc(_header);
            return;
        }

        var window = ArrayPool<byte>.Shared.Rent(SlidingWindowSize);
        try
        {
            var windowLength = 0;
            var windowStart = _streamOrigin;

            while (windowStart - _streamOrigin <= SfxScanLimit)
            {
                if (windowLength < SlidingWindowSize)
                {
                    var read = await stream
                        .ReadAsync(
                            window.AsMemory(windowLength, SlidingWindowSize - windowLength),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    windowLength += read;
                }

                var signatureIndex = window.AsSpan(0, windowLength).IndexOf(SignatureMagic);
                if (signatureIndex >= 0)
                {
                    var signatureOffset = windowStart + signatureIndex;
                    if (signatureOffset - _streamOrigin > SfxScanLimit)
                    {
                        throw new InvalidFormatException("Unable to find 7z signature");
                    }

                    _streamOrigin = signatureOffset;
                    stream.Seek(_streamOrigin, SeekOrigin.Begin);
                    await ReadStartHeaderAtCurrentPositionAsync(stream, cancellationToken)
                        .ConfigureAwait(false);
                    ValidateStartHeaderCrc(_header);
                    return;
                }

                if (windowLength <= SignatureOverlap)
                {
                    break;
                }

                var advance = windowLength - SignatureOverlap;
                windowStart += advance;
                Buffer.BlockCopy(window, advance, window, 0, SignatureOverlap);
                windowLength = SignatureOverlap;
            }

            throw new InvalidFormatException("Unable to find 7z signature");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    public async ValueTask OpenAsync(
        Stream stream,
        bool lookForHeader,
        CancellationToken cancellationToken = default
    )
    {
        Close();

        await LocateAndReadStartHeaderAsync(stream, lookForHeader, cancellationToken)
            .ConfigureAwait(false);
        _stream = stream;
    }

    public async ValueTask<ArchiveDatabase> ReadDatabaseAsync(
        IPasswordProvider pass,
        CancellationToken cancellationToken = default
    )
    {
        var db = new ArchiveDatabase(pass);
        db.Clear();

        db._majorVersion = _header[6];
        db._minorVersion = _header[7];

        if (db._majorVersion != 0)
        {
            throw new ArchiveOperationException();
        }

        // StartHeaderCRC was already verified in Open/OpenAsync.
        var nextHeaderOffset = (long)DataReader.Get64(_header, 0xC);
        var nextHeaderSize = (long)DataReader.Get64(_header, 0x14);
        var nextHeaderCrc = DataReader.Get32(_header, 0x1C);

        db._startPositionAfterHeader = _streamOrigin + 0x20;

        // empty header is ok
        if (nextHeaderSize == 0)
        {
            db.Fill();
            return db;
        }

        if (nextHeaderOffset < 0 || nextHeaderSize < 0 || nextHeaderSize > int.MaxValue)
        {
            throw new ArchiveOperationException();
        }

        if (nextHeaderOffset > _streamEnding - db._startPositionAfterHeader)
        {
            throw new ArchiveOperationException("nextHeaderOffset is invalid");
        }

        _stream.Seek(nextHeaderOffset, SeekOrigin.Current);

        var header = new byte[nextHeaderSize];
        try
        {
            await _stream
                .ReadExactlyAsync(header.AsMemory(0, header.Length), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new IncompleteArchiveException("Unexpected end of stream.", e);
        }

        if (Crc.Finish(Crc.Update(Crc.INIT_CRC, header, 0, header.Length)) != nextHeaderCrc)
        {
            throw new InvalidFormatException("7z next header CRC mismatch");
        }

        using (var streamSwitch = new CStreamSwitch())
        {
            streamSwitch.Set(this, header);

            var type = ReadId();
            if (type != BlockType.Header)
            {
                if (type != BlockType.EncodedHeader)
                {
                    throw new ArchiveOperationException();
                }

                var dataVector = await ReadAndDecodePackedStreamsAsync(
                        db._startPositionAfterHeader,
                        db.PasswordProvider,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                // compressed header without content is odd but ok
                if (dataVector.Count == 0)
                {
                    db.Fill();
                    return db;
                }

                if (dataVector.Count != 1)
                {
                    throw new ArchiveOperationException();
                }

                streamSwitch.Set(this, dataVector[0]);

                if (ReadId() != BlockType.Header)
                {
                    throw new ArchiveOperationException();
                }
            }

            await ReadHeaderAsync(db, db.PasswordProvider, cancellationToken).ConfigureAwait(false);
        }
        db.Fill();
        return db;
    }

    private async ValueTask<List<byte[]>> ReadAndDecodePackedStreamsAsync(
        long baseOffset,
        IPasswordProvider pass,
        CancellationToken cancellationToken
    )
    {
        ReadStreamsInfo(
            null,
            out var dataStartPos,
            out var packSizes,
            out var packCrCs,
            out var folders,
            out var numUnpackStreamsInFolders,
            out var unpackSizes,
            out var digests
        );

        dataStartPos += baseOffset;

        var dataVector = new List<byte[]>(folders.Count);
        var packIndex = 0;
        foreach (var folder in folders)
        {
            var oldDataStartPos = dataStartPos;
            var myPackSizes = new long[folder._packStreams.Count];
            for (var i = 0; i < myPackSizes.Length; i++)
            {
                var packSize = packSizes[packIndex + i];
                myPackSizes[i] = packSize;
                dataStartPos += packSize;
            }

            var outStream = await DecoderStreamHelper
                .CreateDecoderStreamAsync(
                    _stream,
                    oldDataStartPos,
                    myPackSizes,
                    folder,
                    pass,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var unpackSize = checked((int)folder.GetUnpackSize());
            // Decoded bytes are stored in dataVector and outlive this method as DataReader
            // stream sources (CStreamSwitch), so an exact-size array is required; pooling
            // would be unsafe because returned buffers could be reused while still referenced.
            var data = new byte[unpackSize];
            try
            {
                await outStream
                    .ReadExactlyAsync(data.AsMemory(0, data.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException e)
            {
                throw new IncompleteArchiveException("Unexpected end of stream.", e);
            }
            if (outStream.ReadByte() >= 0)
            {
                throw new InvalidFormatException("Decoded stream is longer than expected.");
            }
            dataVector.Add(data);

            if (folder.UnpackCrcDefined)
            {
                if (Crc.Finish(Crc.Update(Crc.INIT_CRC, data, 0, unpackSize)) != folder._unpackCrc)
                {
                    throw new InvalidFormatException("Decoded stream does not match expected CRC.");
                }
            }
        }
        return dataVector;
    }

    private async ValueTask ReadHeaderAsync(
        ArchiveDatabase db,
        IPasswordProvider getTextPassword,
        CancellationToken cancellationToken
    )
    {
        var type = ReadId();

        if (type == BlockType.ArchiveProperties)
        {
            ReadArchiveProperties();
            type = ReadId();
        }

        List<byte[]>? dataVector = null;
        if (type == BlockType.AdditionalStreamsInfo)
        {
            dataVector = await ReadAndDecodePackedStreamsAsync(
                    db._startPositionAfterHeader,
                    getTextPassword,
                    cancellationToken
                )
                .ConfigureAwait(false);
            type = ReadId();
        }

        List<long> unpackSizes;
        List<uint?> digests;

        if (type == BlockType.MainStreamsInfo)
        {
            ReadStreamsInfo(
                dataVector,
                out db._dataStartPosition,
                out db._packSizes,
                out db._packCrCs,
                out db._folders,
                out db._numUnpackStreamsVector,
                out unpackSizes,
                out digests
            );

            db._dataStartPosition += db._startPositionAfterHeader;
            type = ReadId();
        }
        else
        {
            unpackSizes = new List<long>(db._folders.Count);
            digests = new List<uint?>(db._folders.Count);
            db._numUnpackStreamsVector = new List<int>(db._folders.Count);
            for (var i = 0; i < db._folders.Count; i++)
            {
                var folder = db._folders[i];
                unpackSizes.Add(folder.GetUnpackSize());
                digests.Add(folder._unpackCrc);
                db._numUnpackStreamsVector.Add(1);
            }
        }

        db._files.Clear();

        if (type == BlockType.End)
        {
            return;
        }

        if (type != BlockType.FilesInfo)
        {
            throw new ArchiveOperationException();
        }

        var numFiles = ReadNum();
        db._files = new List<CFileItem>(numFiles);
        for (var i = 0; i < numFiles; i++)
        {
            db._files.Add(new CFileItem());
        }

        var emptyStreamVector = new BitVector(numFiles);
        BitVector emptyFileVector = null!;
        BitVector antiFileVector = null!;
        var numEmptyStreams = 0;

        for (; ; )
        {
            type = ReadId();
            if (type == BlockType.End)
            {
                break;
            }

            var size = ReadPropertySize();
            var oldPos = _currentReader.Offset;
            switch (type)
            {
                case BlockType.Name:
                    using (var streamSwitch = new CStreamSwitch())
                    {
                        streamSwitch.Set(this, dataVector ?? []);
                        for (var i = 0; i < db._files.Count; i++)
                        {
                            db._files[i].Name = _currentReader.ReadString();
                        }
                    }
                    break;
                case BlockType.WinAttributes:
                    ReadAttributeVector(
                        dataVector,
                        numFiles,
                        (i, attr) =>
                        {
                            db._files[i].ExtendedAttrib = attr;

                            if (attr.HasValue && (attr.Value >> 16) != 0)
                            {
                                attr = attr.Value & 0x7FFFu;
                            }

                            db._files[i].Attrib = attr;
                        }
                    );
                    break;
                case BlockType.EmptyStream:
                    emptyStreamVector = ReadBitVector(numFiles);
                    for (var i = 0; i < emptyStreamVector.Length; i++)
                    {
                        if (emptyStreamVector[i])
                        {
                            numEmptyStreams++;
                        }
                    }

                    emptyFileVector = new BitVector(numEmptyStreams);
                    antiFileVector = new BitVector(numEmptyStreams);
                    break;
                case BlockType.EmptyFile:
                    emptyFileVector = ReadBitVector(numEmptyStreams);
                    break;
                case BlockType.Anti:
                    antiFileVector = ReadBitVector(numEmptyStreams);
                    break;
                case BlockType.StartPos:
                    ReadNumberVector(
                        dataVector,
                        numFiles,
                        (i, startPos) => db._files[i].StartPos = startPos
                    );
                    break;
                case BlockType.CTime:
                    ReadDateTimeVector(
                        dataVector,
                        numFiles,
                        (i, time) => db._files[i].CTime = time
                    );
                    break;
                case BlockType.ATime:
                    ReadDateTimeVector(
                        dataVector,
                        numFiles,
                        (i, time) => db._files[i].ATime = time
                    );
                    break;
                case BlockType.MTime:
                    ReadDateTimeVector(
                        dataVector,
                        numFiles,
                        (i, time) => db._files[i].MTime = time
                    );
                    break;
                case BlockType.Dummy:
                    for (long j = 0; j < size; j++)
                    {
                        if (ReadByte() != 0)
                        {
                            throw new ArchiveOperationException();
                        }
                    }
                    break;
                default:
                    SkipData(size);
                    break;
            }

            var checkRecordsSize = (db._majorVersion > 0 || db._minorVersion > 2);
            if (checkRecordsSize && _currentReader.Offset - oldPos != size)
            {
                throw new ArchiveOperationException();
            }
        }

        var emptyFileIndex = 0;
        var sizeIndex = 0;
        for (var i = 0; i < numFiles; i++)
        {
            var file = db._files[i];
            file.HasStream = !emptyStreamVector[i];
            if (file.HasStream)
            {
                file.IsDir = false;
                file.IsAnti = false;
                file.Size = unpackSizes[sizeIndex];
                file.Crc = digests[sizeIndex];
                sizeIndex++;
            }
            else
            {
                file.IsDir = !emptyFileVector[emptyFileIndex];
                file.IsAnti = antiFileVector[emptyFileIndex];
                emptyFileIndex++;
                file.Size = 0;
                file.Crc = null;
            }
        }
    }
}
