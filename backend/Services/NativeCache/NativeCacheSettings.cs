using System.Globalization;
using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services.NativeCache;

public sealed record NativeCacheSettings(NativeCacheFolder[] Folders, string MetadataPath, int BufferMb,
    int MinFileMb, int ChunkMb)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static NativeCacheSettings FromConfig(ConfigManager config) => FromValues(config.GetEffectiveConfigValue);

    public static long MinimumFileBytes(ConfigManager config) =>
        ParseMinFileMb(config.GetEffectiveConfigValue(ConfigKeys.NativeCacheMinFileMb)) * 1024L * 1024L;

    public static string RepairRevisionPath(ConfigManager config)
    {
        // Repair is shared by every cache mode. Resolving its optional revision
        // ledger must not initialize or validate unrelated Native settings.
        var path = config.GetEffectiveConfigValue(ConfigKeys.NativeCacheMetadataPath);
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                return Path.Combine(Path.GetFullPath(path), "repair-revisions.db");
        }
        catch (ArgumentException) { /* Invalid optional native settings cannot break repair DI. */ }
        return Path.Combine(DavDatabaseContext.ConfigPath, "native-cache-metadata", "repair-revisions.db");
    }

    public static void ValidateProposed(ConfigManager config, IReadOnlyCollection<ConfigItem> items)
    {
        if (!items.Any(item => item.ConfigName is ConfigKeys.CacheMode or ConfigKeys.NativeCacheFolders
                or ConfigKeys.NativeCacheMetadataPath or ConfigKeys.NativeCacheWriterMb
                or ConfigKeys.NativeCacheMinFileMb or ConfigKeys.NativeCacheChunkMb)) return;
        string? Value(string key) => items.FirstOrDefault(item => item.ConfigName == key)?.ConfigValue
            ?? config.GetEffectiveConfigValue(key);
        var mode = CacheModeResolver.Resolve(Value(ConfigKeys.CacheMode), Value(ConfigKeys.UsenetSegmentCacheEnabled),
            config.IsEnvironmentManaged(ConfigKeys.UsenetSegmentCacheEnabled));
        if (mode != CacheMode.Native) return;
        var settings = FromValues(Value);
        if (!settings.Folders.Any(folder => folder.Enabled && Directory.Exists(folder.Path)))
            throw new ArgumentException("Native mode requires at least one enabled, existing cache folder. Mount the storage before applying.");
        NativeFileSystem.RequireLocalMetadata(settings.MetadataPath);
    }

    public static NativeCacheSettings FromValues(Func<string, string?> value)
    {
        var folders = ParseFolders(value(ConfigKeys.NativeCacheFolders));
        var path = value(ConfigKeys.NativeCacheMetadataPath);
        path = string.IsNullOrWhiteSpace(path) ? Path.Combine(DavDatabaseContext.ConfigPath, "native-cache-metadata") : path;
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Native cache metadata path must be absolute and on local storage.");
        var fullPath = Path.GetFullPath(path);
        foreach (var folder in folders)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            if (fullPath == root || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Native cache metadata must be separate from media cache folders, on local storage.");
        }
        return new(folders, fullPath, ParseBufferMb(value(ConfigKeys.NativeCacheWriterMb)),
            ParseMinFileMb(value(ConfigKeys.NativeCacheMinFileMb)),
            ParseChunkMb(value(ConfigKeys.NativeCacheChunkMb)));
    }

    public static bool ValidateItem(ConfigItem item)
    {
        switch (item.ConfigName)
        {
            case ConfigKeys.NativeCacheFolders:
                ParseFolders(item.ConfigValue);
                return true;
            case ConfigKeys.NativeCacheWriterMb:
                ParseBufferMb(item.ConfigValue);
                return true;
            case ConfigKeys.NativeCacheMinFileMb:
                ParseMinFileMb(item.ConfigValue);
                return true;
            case ConfigKeys.NativeCacheChunkMb:
                ParseChunkMb(item.ConfigValue);
                return true;
            case ConfigKeys.NativeCacheMetadataPath:
                if (!string.IsNullOrWhiteSpace(item.ConfigValue) && !Path.IsPathFullyQualified(item.ConfigValue))
                    throw new ArgumentException("Native cache metadata path must be absolute.");
                return true;
            default: return false;
        }
    }

    public static NativeCacheFolder[] ParseFolders(string? json)
    {
        NativeCacheFolder[] folders;
        try { folders = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<NativeCacheFolder[]>(json, JsonOptions) ?? []; }
        catch (JsonException exception) { throw new ArgumentException("Native cache folders must be a valid folder array.", nameof(json), exception); }
        NativeCacheFolder.Validate(folders);
        return folders;
    }

    private static int ParseBufferMb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 32;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) || mb is < 4 or > 256)
            throw new ArgumentException("Native cache buffer budget must be between 4 and 256 MiB.");
        return mb;
    }

    private static int ParseMinFileMb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 100;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) || mb is < 0 or > 1_048_576)
            throw new ArgumentException("Native cache minimum file size must be between 0 and 1,048,576 MiB.");
        return mb;
    }

    private static int ParseChunkMb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 64;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) || mb is < 4 or > 256 || mb % 4 != 0)
            throw new ArgumentException("Native cache warming chunk size must be a multiple of 4 MiB between 4 and 256 MiB.");
        return mb;
    }
}
