using System.Runtime.CompilerServices;
using MemoryPack;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Database.Models;
using ZstdSharp;

namespace NzbWebDAV.Database;

public static class BlobStore
{
    private const int CompressionLevel = 1;
    private static string ConfigPath => DavDatabaseContext.ConfigPath;
    private static readonly Lock LockObj = new();
    private static readonly MemoryCache MetadataCache = new(new MemoryCacheOptions
    {
        // Cache size is measured in segment references, which tracks the dominant memory cost.
        SizeLimit = 200_000
    });

    private static string GetBlobPath(Guid id)
    {
        var guidStr = id.ToString("N"); // Without hyphens
        var firstTwo = guidStr[..2];
        var nextTwo = guidStr.Substring(2, 2);
        var fileName = id.ToString(); // With hyphens for readability

        return Path.Join(ConfigPath, "blobs", firstTwo, nextTwo, fileName);
    }

    private static FileStream OpenBlobWrite(Guid id)
    {
        var blobPath = GetBlobPath(id);
        var directory = Path.GetDirectoryName(blobPath);

        // Acquire file handle inside lock to prevent race condition where
        // directory gets deleted between CreateDirectory and File.Create
        FileStream fileStream;
        lock (LockObj)
        {
            Directory.CreateDirectory(directory!);
            fileStream = File.Create(blobPath);
        }

        return fileStream;
    }

    // Prefer this overload over WriteBlob<T> when the argument is a Stream;
    // an optional CancellationToken otherwise loses to the generic method.
    [OverloadResolutionPriority(1)]
    public static async Task WriteBlob(
        Guid id,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        await using var fileStream = OpenBlobWrite(id);
        await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        MetadataCache.Remove(id);
    }

    public static async Task WriteBlob<T>(Guid id, T blob)
    {
        await using var fileStream = OpenBlobWrite(id);
        await using var compressionStream = new CompressionStream(fileStream, CompressionLevel);
        await MemoryPackSerializer.SerializeAsync(compressionStream, blob).ConfigureAwait(false);
        MetadataCache.Remove(id);
    }

    public static Stream? ReadBlob(Guid id)
    {
        var blobPath = GetBlobPath(id);
        return File.Exists(blobPath) ? File.OpenRead(blobPath) : null;
    }

    public static async Task<T?> ReadBlob<T>(Guid id)
    {
        if (MetadataCache.TryGetValue(id, out T? cached)) return cached;

        var stream = ReadBlob(id);
        if (stream == null) return default;
        await using var fileStream = stream;
        await using var decompressionStream = new DecompressionStream(fileStream);
        var blob = await MemoryPackSerializer.DeserializeAsync<T>(decompressionStream).ConfigureAwait(false);
        if (blob is not null)
        {
            MetadataCache.Set(id, blob, new MemoryCacheEntryOptions()
                .SetSize(GetCacheSize(blob))
                .SetSlidingExpiration(TimeSpan.FromMinutes(10)));
        }

        return blob;
    }

    public static void Delete(Guid id)
    {
        MetadataCache.Remove(id);
        var blobPath = GetBlobPath(id);

        // Delete the file
        if (File.Exists(blobPath))
        {
            File.Delete(blobPath);
        }

        lock (LockObj)
        {
            // Clean up empty directories
            // Structure: CONFIG_PATH/blobs/{firstTwo}/{nextTwo}/{fileName}
            var nextTwoDir = Path.GetDirectoryName(blobPath);
            var firstTwoDir = Path.GetDirectoryName(nextTwoDir);

            TryDeleteEmptyDirectory(nextTwoDir);
            TryDeleteEmptyDirectory(firstTwoDir);
        }
    }

    private static void TryDeleteEmptyDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        if (!Directory.Exists(directory)) return;
        if (!IsDirectoryEmpty(directory)) return;
        Directory.Delete(directory, recursive: false);
    }

    private static bool IsDirectoryEmpty(string path)
    {
        return !Directory.EnumerateFileSystemEntries(path).Any();
    }

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
