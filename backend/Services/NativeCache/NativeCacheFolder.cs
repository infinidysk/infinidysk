namespace NzbWebDAV.Services.NativeCache;

public sealed record NativeCacheFolder
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Native cache";
    public string Path { get; init; } = "";
    public long MaxBytes { get; init; } = 1_000_000_000_000;
    public long MinFreeBytes { get; init; } = 10_000_000_000;
    public int MaxAgeDays { get; init; } = 30;
    public int Priority { get; init; }
    public int HighWaterPercent { get; init; } = 90;
    public int LowWaterPercent { get; init; } = 80;
    public bool Enabled { get; init; } = true;
    public bool ReadOnly { get; init; }
    public string StorageType { get; init; } = "hdd";

    public static void Validate(IReadOnlyList<NativeCacheFolder> folders)
    {
        if (folders.Count > 32) throw new ArgumentException("At most 32 native cache folders are supported.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var folder in folders)
        {
            if (folder is null) throw new ArgumentException("Native cache folders cannot contain null records.");
            if (string.IsNullOrWhiteSpace(folder.Name) || folder.Id is null || folder.Path is null)
                throw new ArgumentException("Native cache folders need a name, ID and path.");
            if (folder.Name.Length > 128 || folder.Id.Length > 128 || folder.Path.Length > 4096)
                throw new ArgumentException("Native cache folder name, ID or path is too long.");
            if (string.IsNullOrWhiteSpace(folder.Id) || !ids.Add(folder.Id))
                throw new ArgumentException("Native cache folder IDs must be unique and nonempty.");
            if (!System.IO.Path.IsPathFullyQualified(folder.Path))
                throw new ArgumentException("Native cache folders require absolute paths.");
            var path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder.Path));
            if (path == System.IO.Path.GetPathRoot(path))
                throw new ArgumentException("A filesystem root cannot be a native cache folder.");
            if (paths.Any(other => path == other
                    || path.StartsWith(other + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || other.StartsWith(path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new ArgumentException("Native cache folders must not overlap.");
            if (folder.MaxBytes <= 0 || folder.MinFreeBytes < 0 || folder.MaxAgeDays < 0)
                throw new ArgumentException("Native cache quotas must be positive; reserve and age cannot be negative.");
            if (folder.LowWaterPercent < 1 || folder.LowWaterPercent >= folder.HighWaterPercent || folder.HighWaterPercent > 100)
                throw new ArgumentException("Native cache watermarks require 1 <= low < high <= 100 percent.");
            if (folder.StorageType is not ("hdd" or "nas" or "ssd"))
                throw new ArgumentException("Native cache storage type must be hdd, nas or ssd.");
            paths.Add(path);
        }
    }
}

public sealed record NativeCacheIdentity(string ItemId, string Generation, long Length)
{
    // Diagnostic metadata only; it does not participate in the cache key.
    public string? DisplayName { get; init; }
    public string Key => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{ItemId.Length}:{ItemId}{Generation.Length}:{Generation}:{Length}"))).ToLowerInvariant();
}

public sealed record NativeCacheFolderStatus(string Id, bool Online, bool Writable, long CommittedBytes, long Entries, string? Error);

public sealed record NativeCacheEntry(string Key, string FolderId, string ItemId, long Length, long AllocatedBytes, long VerifiedBytes, bool Pinned,
    string? Generation = null);
