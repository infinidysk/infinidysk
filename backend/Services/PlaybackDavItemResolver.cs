using System.Collections.Concurrent;
using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

/// <summary>
/// Resolves only the currently-playing media source path to an exact InfiniDysk DavItem.
/// It never performs a full library walk and never falls back to title/filename matching.
/// </summary>
public sealed class PlaybackDavItemResolver(ConfigManager configManager)
{
    private readonly ConcurrentDictionary<CacheKey, Guid> _cache = new();

    public Guid? Resolve(MediaServerInstance instance, PlaybackObservation observation)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(observation);

        var sourcePath = observation.MediaSourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        if (TryResolveDirectIdentity(sourcePath, configManager.GetRcloneMountDir(), out var directId))
            return directId;

        var translated = TranslateToInfiniDyskPath(instance, sourcePath);
        if (translated is null)
            return null;

        var libraryRoot = configManager.GetLibraryDir();
        if (string.IsNullOrWhiteSpace(libraryRoot))
            return null;

        var fullRoot = Path.GetFullPath(libraryRoot);
        var fullPath = Path.GetFullPath(translated);
        if (!IsUnderRoot(fullPath, fullRoot))
            return null;

        var key = new CacheKey(instance.Id, fullPath);
        if (_cache.TryGetValue(key, out var cachedId))
        {
            if (OrganizedLinksUtil.PathStillTargets(fullPath, cachedId, configManager))
                return cachedId;
            _cache.TryRemove(key, out _);
        }

        var info = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(fullPath));
        var link = info switch
        {
            SymlinkAndStrmUtil.SymlinkInfo symlink =>
                OrganizedLinksUtil.GetDavItemLink(symlink, configManager.GetRcloneMountDir()),
            SymlinkAndStrmUtil.StrmInfo strm =>
                OrganizedLinksUtil.GetDavItemLink(strm),
            _ => null,
        };

        if (link is not { } exact)
            return null;

        _cache[key] = exact.DavItemId;
        return exact.DavItemId;
    }

    internal static string? TranslateToInfiniDyskPath(
        MediaServerInstance instance,
        string mediaSourcePath)
    {
        var normalizedSource = NormalizeRemotePath(mediaSourcePath);
        var mapping = instance.PathMappings
            .Where(candidate => PrefixMatches(
                normalizedSource,
                NormalizeRemotePath(candidate.MediaServerPrefix),
                IsWindowsStyle(candidate.MediaServerPrefix)))
            .OrderByDescending(candidate => NormalizeRemotePath(candidate.MediaServerPrefix).Length)
            .FirstOrDefault();

        if (mapping is null)
        {
            return Path.IsPathRooted(mediaSourcePath)
                ? mediaSourcePath
                : null;
        }

        var normalizedPrefix = NormalizeRemotePath(mapping.MediaServerPrefix).TrimEnd('/');
        var suffix = normalizedSource[normalizedPrefix.Length..].TrimStart('/');
        var localSuffix = suffix.Replace('/', Path.DirectorySeparatorChar);
        return Path.Join(mapping.InfiniDyskPrefix, localSuffix);
    }

    internal static bool TryResolveDirectIdentity(
        string mediaSourcePath,
        string mountDir,
        out Guid davItemId)
    {
        davItemId = Guid.Empty;
        if (Uri.TryCreate(mediaSourcePath, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && TryParseIdsPath(uri.AbsolutePath, "/view/.ids/", out davItemId))
            return true;

        var normalized = NormalizeRemotePath(mediaSourcePath);
        if (TryParseIdsPath(normalized, "/.ids/", out davItemId))
            return true;

        var normalizedMount = NormalizeRemotePath(mountDir).TrimEnd('/');
        if (!PrefixMatches(normalized, normalizedMount, IsWindowsStyle(mountDir)))
            return false;

        var relative = normalized[normalizedMount.Length..];
        return TryParseIdsPath(relative, "/.ids/", out davItemId);
    }

    private static bool TryParseIdsPath(string path, string prefix, out Guid davItemId)
    {
        davItemId = Guid.Empty;
        var normalized = NormalizeRemotePath(path);
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var remainder = normalized[prefix.Length..].TrimEnd('/');
        if (string.IsNullOrWhiteSpace(remainder))
            return false;

        var slash = remainder.LastIndexOf('/');
        var fileName = slash >= 0 ? remainder[(slash + 1)..] : remainder;
        var dot = fileName.LastIndexOf('.');
        var idText = dot > 0 ? fileName[..dot] : fileName;
        return Guid.TryParse(idText, out davItemId);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static bool PrefixMatches(string value, string prefix, bool ignoreCase)
    {
        prefix = prefix.TrimEnd('/');
        if (prefix.Length == 0 || value.Length < prefix.Length)
            return false;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!value.StartsWith(prefix, comparison))
            return false;

        return value.Length == prefix.Length || value[prefix.Length] == '/';
    }

    private static string NormalizeRemotePath(string path) =>
        path.Trim().Replace('\\', '/');

    private static bool IsWindowsStyle(string path) =>
        path.Contains('\\', StringComparison.Ordinal)
        || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

    private readonly record struct CacheKey(Guid InstanceId, string LocalPath);
}
