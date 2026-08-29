using System.Runtime.CompilerServices;
using MemoryPack;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Database.Models;
using ZstdSharp;

namespace NzbWebDAV.Database;

/// <summary>
/// Filesystem blob store under <c>CONFIG_PATH/blobs</c> with a process-local
/// metadata cache. Registered as a DI singleton; the static <see cref="BlobStore"/>
/// facade forwards here until remaining call sites inject <see cref="IBlobStore"/>.
/// </summary>
public sealed class FileBlobStore : IBlobStore, IDisposable
{
    private const int CompressionLevel = 1;
    private readonly Lock _lockObj = new();
    private readonly MemoryCache _metadataCache = new(new MemoryCacheOptions
    {
        SizeLimit = 200_000
    });
    private bool _disposed;

    private static string ConfigPath => DavDatabaseContext.ConfigPath;

    private static string GetBlobPath(Guid id)
    {
        var guidStr = id.ToString("N");
        var firstTwo = guidStr[..2];
        var nextTwo = guidStr.Substring(2, 2);
        var fileName = id.ToString();
        return Path.Join(ConfigPath, "blobs", firstTwo, nextTwo, fileName);
    }

    private FileStream OpenBlobWrite(string blobPath)
    {
        var directory = Path.GetDirectoryName(blobPath);

        FileStream fileStream;
        lock (_lockObj)
        {
            Directory.CreateDirectory(directory!);
            fileStream = File.Create(blobPath);
        }

        return fileStream;
    }

    [OverloadResolutionPriority(1)]
    public async Task WriteBlob(
        Guid id,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var blobPath = GetBlobPath(id);
        var tempPath = blobPath + ".tmp";
        var committed = false;
        try
        {
            await using (var fileStream = OpenBlobWrite(tempPath))
            {
                await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            CommitBlobWrite(blobPath, tempPath);
            committed = true;
            _metadataCache.Remove(id);
        }
        finally
        {
            if (!committed)
                TryDeleteIncompleteWrite(tempPath);
        }
    }

    public async Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blobPath = GetBlobPath(id);
        var tempPath = blobPath + ".tmp";
        var committed = false;
        try
        {
            await using (var fileStream = OpenBlobWrite(tempPath))
            await using (var compressionStream = new CompressionStream(fileStream, CompressionLevel))
            {
                // CPU-bound serialization may finish before the token is observed;
                // ThrowIfCancellationRequested and temp-file cleanup still honor it.
                await MemoryPackSerializer.SerializeAsync(compressionStream, blob, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();

            CommitBlobWrite(blobPath, tempPath);
            committed = true;
            _metadataCache.Remove(id);
        }
        finally
        {
            if (!committed)
                TryDeleteIncompleteWrite(tempPath);
        }
    }

    public Stream? ReadBlob(Guid id)
    {
        var blobPath = GetBlobPath(id);
        return File.Exists(blobPath) ? File.OpenRead(blobPath) : null;
    }

    public bool Exists(Guid id)
    {
        try
        {
            var attributes = File.GetAttributes(GetBlobPath(id));
            return !attributes.HasFlag(FileAttributes.Directory);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public async Task<T?> ReadBlob<T>(Guid id)
    {
        if (_metadataCache.TryGetValue(id, out T? cached)) return cached;

        var stream = ReadBlob(id);
        if (stream == null) return default;
        await using var fileStream = stream;
        await using var decompressionStream = new DecompressionStream(fileStream);
        var blob = await MemoryPackSerializer.DeserializeAsync<T>(decompressionStream).ConfigureAwait(false);
        if (blob is not null)
        {
            _metadataCache.Set(id, blob, new MemoryCacheEntryOptions()
                .SetSize(GetCacheSize(blob))
                .SetSlidingExpiration(TimeSpan.FromMinutes(10)));
        }

        return blob;
    }

    private void CommitBlobWrite(string blobPath, string tempPath)
    {
        lock (_lockObj)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            File.Move(tempPath, blobPath, overwrite: true);
        }
    }

    private void TryDeleteIncompleteWrite(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (IOException)
        {
            // Best-effort: a cancelled/failed write must not leave a readable blob.
        }

        lock (_lockObj)
        {
            var nextTwoDir = Path.GetDirectoryName(tempPath);
            var firstTwoDir = Path.GetDirectoryName(nextTwoDir);
            TryDeleteEmptyDirectory(nextTwoDir);
            TryDeleteEmptyDirectory(firstTwoDir);
        }
    }

    public bool Delete(Guid id)
    {
        _metadataCache.Remove(id);
        var blobPath = GetBlobPath(id);
        var deleted = false;

        lock (_lockObj)
        {
            try
            {
                File.GetAttributes(blobPath);
                File.Delete(blobPath);
                deleted = true;
            }
            catch (FileNotFoundException)
            {
                // The blob is already absent; the cleanup operation is idempotent.
            }
            catch (DirectoryNotFoundException)
            {
                // The blob's sharded directory is already absent.
            }

            var nextTwoDir = Path.GetDirectoryName(blobPath);
            var firstTwoDir = Path.GetDirectoryName(nextTwoDir);
            TryDeleteEmptyDirectory(nextTwoDir);
            TryDeleteEmptyDirectory(firstTwoDir);
        }

        return deleted;
    }

    public void Dispose()
    {
        lock (_lockObj)
        {
            if (_disposed) return;
            _disposed = true;
            _metadataCache.Dispose();
        }

        BlobStore.ClearIfCurrent(this);
    }

    private static void TryDeleteEmptyDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        if (!Directory.Exists(directory)) return;
        if (!IsDirectoryEmpty(directory)) return;
        Directory.Delete(directory, recursive: false);
    }

    private static bool IsDirectoryEmpty(string path) =>
        !Directory.EnumerateFileSystemEntries(path).Any();

    private static int GetCacheSize<T>(T blob)
    {
        var segmentCount = blob switch
        {
            DavNzbFile nzbFile => nzbFile.SegmentIds.Length,
            DavRarFile rarFile => rarFile.RarParts.Sum(part => part.SegmentIds.Length),
            DavMultipartFile multipartFile => multipartFile.Metadata.FileParts
                .Sum(part => part.SegmentIds.Length),
            _ => 1
        };

        return Math.Max(segmentCount, 1);
    }
}
