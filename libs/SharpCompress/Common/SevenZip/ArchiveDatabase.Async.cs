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
internal sealed partial class ArchiveDatabase
{
    internal async ValueTask<Stream> GetFolderStreamAsync(
        Stream stream,
        CFolder folder,
        IPasswordProvider pw,
        CancellationToken cancellationToken
    )
    {
        var packStreamIndex = folder._firstPackStreamId;
        var folderStartPackPos = GetFolderStreamPos(folder, 0);
        var count = folder._packStreams.Count;
        var packSizes = new long[count];
        for (var j = 0; j < count; j++)
        {
            packSizes[j] = _packSizes[packStreamIndex + j];
        }

        return await DecoderStreamHelper
            .CreateDecoderStreamAsync(
                stream,
                folderStartPackPos,
                packSizes,
                folder,
                pw,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Async twin of <see cref="GetFolderStreamCached"/>. Concurrent opens bypass the cache.
    /// </summary>
    internal async ValueTask<Stream> GetFolderStreamCachedAsync(
        Stream baseStream,
        int folderIndex,
        long targetOffset,
        long entryLength,
        IPasswordProvider pw,
        CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.CompareExchange(ref _folderCacheInUse, 1, 0) != 0)
        {
            return await CreateUncachedEntryStreamAsync(
                    baseStream,
                    folderIndex,
                    targetOffset,
                    entryLength,
                    pw,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        try
        {
            await EnsureFolderStreamAtAsync(
                    baseStream,
                    folderIndex,
                    targetOffset,
                    pw,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var entryStream = CreateCachedEntrySubStream(entryLength);
            return new FolderCacheEntryStream(this, entryStream, targetOffset, entryLength);
        }
        catch
        {
            Interlocked.Exchange(ref _folderCacheInUse, 0);
            throw;
        }
    }

    private async ValueTask<ReadOnlySubStream> CreateUncachedEntryStreamAsync(
        Stream baseStream,
        int folderIndex,
        long targetOffset,
        long entryLength,
        IPasswordProvider pw,
        CancellationToken cancellationToken
    )
    {
        var folderStream = await GetFolderStreamAsync(
                baseStream,
                _folders[folderIndex],
                pw,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (targetOffset > 0)
        {
            await folderStream.SkipAsync(targetOffset, cancellationToken).ConfigureAwait(false);
        }
        return new ReadOnlySubStream(folderStream, entryLength, leaveOpen: false);
    }

    private async ValueTask<bool> TryBuildDecodedFolderCacheAsync(
        Stream baseStream,
        int folderIndex,
        long targetOffset,
        IPasswordProvider pw,
        CancellationToken cancellationToken
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
                var folderStream = await GetFolderStreamAsync(
                        baseStream,
                        _folders[folderIndex],
                        pw,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                try
                {
                    await folderStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await folderStream.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Some coders (notably PPMd) can fail when materializing an entire folder;
                // fall back to the live sequential decoder path.
                await buffer.DisposeAsync().ConfigureAwait(false);
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

    private async ValueTask EnsureFolderStreamAtAsync(
        Stream baseStream,
        int folderIndex,
        long targetOffset,
        IPasswordProvider pw,
        CancellationToken cancellationToken
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
                await _cachedFolderStream.SkipAsync(skip, cancellationToken).ConfigureAwait(false);
            }

            _cachedPositionInFolder = targetOffset;
            return;
        }

        if (
            await TryBuildDecodedFolderCacheAsync(
                    baseStream,
                    folderIndex,
                    targetOffset,
                    pw,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            return;
        }

        if (_decodedFolderIndex >= 0 && _decodedFolderIndex != folderIndex)
        {
            InvalidateDecodedFolderCache();
        }

        DisposeLiveFolderStream();
        _cachedFolderStream = await GetFolderStreamAsync(
                baseStream,
                _folders[folderIndex],
                pw,
                cancellationToken
            )
            .ConfigureAwait(false);
        _cachedFolderIndex = folderIndex;
        if (targetOffset > 0)
        {
            await _cachedFolderStream
                .SkipAsync(targetOffset, cancellationToken)
                .ConfigureAwait(false);
        }

        _cachedPositionInFolder = targetOffset;
    }
}
