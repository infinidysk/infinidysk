using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.LZMA.Utilities;
using SharpCompress.IO;

namespace SharpCompress.Common.SevenZip;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "DisposeFolderStreamCache and Clear release live decoders and decoded-folder buffers."
)]
internal partial class ArchiveDatabase
{
    internal byte _majorVersion;
    internal byte _minorVersion;
    internal long _startPositionAfterHeader;
    internal long _dataStartPosition;

    internal List<long> _packSizes = new();
    internal List<uint?> _packCrCs = new();
    internal List<CFolder> _folders = new();
    internal List<int> _numUnpackStreamsVector = null!;
    internal List<CFileItem> _files = new();

    internal List<long> _packStreamStartPositions = new();
    internal List<int> _folderStartFileIndex = new();
    internal List<int> _fileIndexToFolderIndexMap = new();

    /// <summary>
    /// Decompressed byte offset of each file within its folder (0 for empty-stream entries).
    /// </summary>
    internal List<long> _fileInFolderOffset = new();

    // MRU folder-stream cache for Archive API sequential opens within a solid folder.
    private Stream? _cachedFolderStream;
    private int _cachedFolderIndex = -1;
    private long _cachedPositionInFolder;
    private int _folderCacheInUse;

    // Decoded-folder buffer for random access within a solid folder (Archive API).
    private PooledMemoryStream? _decodedFolderBuffer;
    private int _decodedFolderIndex = -1;

    internal long SolidFolderDecodedCacheMaxBytes { get; set; } = 128L * 1024 * 1024;

    internal IPasswordProvider PasswordProvider { get; }

    public ArchiveDatabase(IPasswordProvider passwordProvider) =>
        PasswordProvider = passwordProvider;

    internal void Clear()
    {
        DisposeFolderStreamCache();

        _packSizes.Clear();
        _packCrCs.Clear();
        _folders.Clear();
        _numUnpackStreamsVector = null!;
        _files.Clear();

        _packStreamStartPositions.Clear();
        _folderStartFileIndex.Clear();
        _fileIndexToFolderIndexMap.Clear();
        _fileInFolderOffset.Clear();
    }

    internal bool IsEmpty() =>
        _packSizes.Count == 0
        && _packCrCs.Count == 0
        && _folders.Count == 0
        && _numUnpackStreamsVector.Count == 0
        && _files.Count == 0;

    private void FillStartPos()
    {
        _packStreamStartPositions.Clear();

        long startPos = 0;
        for (var i = 0; i < _packSizes.Count; i++)
        {
            _packStreamStartPositions.Add(startPos);
            startPos += _packSizes[i];
        }
    }

    private void FillFolderStartFileIndex()
    {
        _folderStartFileIndex.Clear();
        _fileIndexToFolderIndexMap.Clear();
        _fileInFolderOffset.Clear();

        var folderIndex = 0;
        var indexInFolder = 0;
        long offsetInFolder = 0;
        for (var i = 0; i < _files.Count; i++)
        {
            var file = _files[i];

            var emptyStream = !file.HasStream;

            if (emptyStream && indexInFolder == 0)
            {
                _fileIndexToFolderIndexMap.Add(-1);
                _fileInFolderOffset.Add(0);
                continue;
            }

            if (indexInFolder == 0)
            {
                // v3.13 incorrectly worked with empty folders
                // v4.07: Loop for skipping empty folders
                for (; ; )
                {
                    if (folderIndex >= _folders.Count)
                    {
                        throw new ArchiveOperationException();
                    }

                    _folderStartFileIndex.Add(i); // check it
                    offsetInFolder = 0;

                    if (_numUnpackStreamsVector![folderIndex] != 0)
                    {
                        break;
                    }

                    folderIndex++;
                }
            }

            _fileIndexToFolderIndexMap.Add(folderIndex);
            _fileInFolderOffset.Add(offsetInFolder);
            offsetInFolder += file.Size;

            if (emptyStream)
            {
                continue;
            }

            indexInFolder++;

            if (indexInFolder >= _numUnpackStreamsVector![folderIndex])
            {
                folderIndex++;
                indexInFolder = 0;
                offsetInFolder = 0;
            }
        }
    }

    public void Fill()
    {
        FillStartPos();
        FillFolderStartFileIndex();
    }

    internal long GetFolderStreamPos(CFolder folder, int indexInFolder)
    {
        var index = folder._firstPackStreamId + indexInFolder;
        return _dataStartPosition + _packStreamStartPositions[index];
    }

    internal long GetFolderFullPackSize(int folderIndex)
    {
        var packStreamIndex = _folders[folderIndex]._firstPackStreamId;
        var folder = _folders[folderIndex];

        long size = 0;
        for (var i = 0; i < folder._packStreams.Count; i++)
        {
            size += _packSizes[packStreamIndex + i];
        }

        return size;
    }

    internal Stream GetFolderStream(Stream stream, CFolder folder, IPasswordProvider pw)
    {
        var packStreamIndex = folder._firstPackStreamId;
        var folderStartPackPos = GetFolderStreamPos(folder, 0);
        var count = folder._packStreams.Count;
        var packSizes = new long[count];
        for (var j = 0; j < count; j++)
        {
            packSizes[j] = _packSizes[packStreamIndex + j];
        }

        return DecoderStreamHelper.CreateDecoderStream(
            stream,
            folderStartPackPos,
            packSizes,
            folder,
            pw
        );
    }

    /// <summary>
    /// Returns an entry substream over a cached folder decoder when possible.
    /// Sequential opens within the same folder reuse the decoder; backward or concurrent
    /// opens rebuild (concurrent opens bypass the cache entirely).
    /// </summary>
    internal Stream GetFolderStreamCached(
        Stream baseStream,
        int folderIndex,
        long targetOffset,
        long entryLength,
        IPasswordProvider pw
    )
    {
        if (Interlocked.CompareExchange(ref _folderCacheInUse, 1, 0) != 0)
        {
            return CreateUncachedEntryStream(
                baseStream,
                folderIndex,
                targetOffset,
                entryLength,
                pw
            );
        }

        try
        {
            EnsureFolderStreamAt(baseStream, folderIndex, targetOffset, pw);
            var entryStream = CreateCachedEntrySubStream(entryLength);
            return new FolderCacheEntryStream(this, entryStream, targetOffset, entryLength);
        }
        catch
        {
            Interlocked.Exchange(ref _folderCacheInUse, 0);
            throw;
        }
    }

    private ReadOnlySubStream CreateUncachedEntryStream(
        Stream baseStream,
        int folderIndex,
        long targetOffset,
        long entryLength,
        IPasswordProvider pw
    )
    {
        var folderStream = GetFolderStream(baseStream, _folders[folderIndex], pw);
        if (targetOffset > 0)
        {
            folderStream.Skip(targetOffset);
        }
        return new ReadOnlySubStream(folderStream, entryLength, leaveOpen: false);
    }

    private ReadOnlySubStream CreateCachedEntrySubStream(long entryLength)
    {
        if (
            _decodedFolderBuffer is not null
            && ReferenceEquals(_cachedFolderStream, _decodedFolderBuffer)
        )
        {
            return new ReadOnlySubStream(
                _decodedFolderBuffer,
                _cachedPositionInFolder,
                entryLength,
                leaveOpen: true
            );
        }

        return new ReadOnlySubStream(_cachedFolderStream!, entryLength, leaveOpen: true);
    }

    private bool TryUseDecodedFolderCache(int folderIndex, long targetOffset)
    {
        if (_decodedFolderIndex != folderIndex || _decodedFolderBuffer is null)
        {
            return false;
        }

        _cachedFolderIndex = folderIndex;
        _cachedFolderStream = _decodedFolderBuffer;
        _cachedPositionInFolder = targetOffset;
        return true;
    }

    private bool TryBuildDecodedFolderCache(
        Stream baseStream,
        int folderIndex,
        long targetOffset,
        IPasswordProvider pw
    )
    {
        if (SolidFolderDecodedCacheMaxBytes <= 0)
        {
            return false;
        }

        var unpackSize = _folders[folderIndex].GetUnpackSize();
        if (unpackSize <= 0 || unpackSize > SolidFolderDecodedCacheMaxBytes)
        {
            return false;
        }

        if (_decodedFolderIndex != folderIndex || _decodedFolderBuffer is null)
        {
            InvalidateDecodedFolderCache();
            DisposeLiveFolderStream();

            var buffer = new PooledMemoryStream(checked((int)unpackSize));
            try
            {
                using (var folderStream = GetFolderStream(baseStream, _folders[folderIndex], pw))
                {
                    folderStream.CopyTo(buffer);
                }
            }
            catch
            {
                // Some coders (notably PPMd) can fail when materializing an entire folder;
                // fall back to the live sequential decoder path.
                buffer.Dispose();
                return false;
            }

            buffer.Position = 0;
            _decodedFolderBuffer = buffer;
            _decodedFolderIndex = folderIndex;
        }

        _cachedFolderIndex = folderIndex;
        _cachedFolderStream = _decodedFolderBuffer;
        _cachedPositionInFolder = targetOffset;
        return true;
    }

    private void EnsureFolderStreamAt(
        Stream baseStream,
        int folderIndex,
        long targetOffset,
        IPasswordProvider pw
    )
    {
        if (TryUseDecodedFolderCache(folderIndex, targetOffset))
        {
            return;
        }

        if (
            _cachedFolderIndex == folderIndex
            && _cachedFolderStream is not null
            && !ReferenceEquals(_cachedFolderStream, _decodedFolderBuffer)
            && targetOffset >= _cachedPositionInFolder
        )
        {
            var skip = targetOffset - _cachedPositionInFolder;
            if (skip > 0)
            {
                _cachedFolderStream.Skip(skip);
            }

            _cachedPositionInFolder = targetOffset;
            return;
        }

        if (TryBuildDecodedFolderCache(baseStream, folderIndex, targetOffset, pw))
        {
            return;
        }

        if (_decodedFolderIndex >= 0 && _decodedFolderIndex != folderIndex)
        {
            InvalidateDecodedFolderCache();
        }

        DisposeLiveFolderStream();
        _cachedFolderStream = GetFolderStream(baseStream, _folders[folderIndex], pw);
        _cachedFolderIndex = folderIndex;
        if (targetOffset > 0)
        {
            _cachedFolderStream.Skip(targetOffset);
        }

        _cachedPositionInFolder = targetOffset;
    }

    private void DisposeLiveFolderStream()
    {
        if (
            _cachedFolderStream is not null
            && !ReferenceEquals(_cachedFolderStream, _decodedFolderBuffer)
        )
        {
            _cachedFolderStream.Dispose();
        }

        _cachedFolderStream = null;
    }

    private void ReleaseFolderStreamCache(long targetOffset, long entryLength, bool fullyConsumed)
    {
        try
        {
            if (_decodedFolderBuffer is not null)
            {
                return;
            }

            if (!fullyConsumed)
            {
                InvalidateFolderStreamCache();
            }
            else
            {
                _cachedPositionInFolder = targetOffset + entryLength;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _folderCacheInUse, 0);
        }
    }

    private void InvalidateFolderStreamCache()
    {
        DisposeLiveFolderStream();
        _cachedFolderIndex = -1;
        _cachedPositionInFolder = 0;
    }

    private void InvalidateDecodedFolderCache()
    {
        _decodedFolderBuffer?.Dispose();
        _decodedFolderBuffer = null;
        _decodedFolderIndex = -1;
    }

    internal void DisposeFolderStreamCache()
    {
        InvalidateFolderStreamCache();
        InvalidateDecodedFolderCache();
        Interlocked.Exchange(ref _folderCacheInUse, 0);
    }

    /// <summary>
    /// Entry substream that keeps the folder decoder alive and updates/invalidates the MRU cache on dispose.
    /// </summary>
    private sealed class FolderCacheEntryStream : Stream, IStreamStack
    {
        private readonly ArchiveDatabase _database;
        private readonly ReadOnlySubStream _inner;
        private readonly long _targetOffset;
        private readonly long _entryLength;
        private bool _disposed;

        public FolderCacheEntryStream(
            ArchiveDatabase database,
            ReadOnlySubStream inner,
            long targetOffset,
            long entryLength
        )
        {
            _database = database;
            _inner = inner;
            _targetOffset = targetOffset;
            _entryLength = entryLength;
        }

        Stream IStreamStack.BaseStream() => _inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int ReadByte() => _inner.ReadByte();

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            var fullyConsumed = _inner.BytesLeftToRead <= 0;
            await _inner.DisposeAsync().ConfigureAwait(false);
            _database.ReleaseFolderStreamCache(_targetOffset, _entryLength, fullyConsumed);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (disposing)
            {
                var fullyConsumed = _inner.BytesLeftToRead <= 0;
                _inner.Dispose();
                _database.ReleaseFolderStreamCache(_targetOffset, _entryLength, fullyConsumed);
            }
            base.Dispose(disposing);
        }
    }
}
