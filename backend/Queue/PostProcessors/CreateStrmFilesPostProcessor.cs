using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Auth;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using Serilog;

namespace NzbWebDAV.Queue.PostProcessors;

public class CreateStrmFilesPostProcessor(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    Guid historyItemId)
{
    private static readonly string ContentRootPrefix =
        DavItem.ContentFolder.Path.TrimEnd('/') + "/";

    /// <summary>
    /// Outcome of a written STRM sidecar. <see cref="PreviousContent"/> is null for a
    /// newly created file and holds the pre-write content for a rewritten one, so a
    /// failed publish can restore rewrites instead of deleting pre-existing files.
    /// </summary>
    internal sealed record StrmWrite(string Path, string? PreviousContent);

    public async Task CreateStrmFilesAsync(CancellationToken cancellationToken = default)
    {
        var candidates = CollectVideoItems();
        var created = new List<DavItem>();
        var rewritten = new List<StrmWrite>();
        try
        {
            foreach (var videoItem in candidates)
            {
                var write = await CreateStrmFileAsync(videoItem, cancellationToken).ConfigureAwait(false);
                if (write is null)
                    continue;
                if (write.PreviousContent is null)
                    created.Add(videoItem);
                else
                    rewritten.Add(write);
            }
        }
        catch
        {
            foreach (var createdItem in created)
                DeleteStrmFile(createdItem);
            foreach (var rewrite in rewritten)
                TryRestorePreviousContent(rewrite);
            throw;
        }
    }

    private static void TryRestorePreviousContent(StrmWrite write)
    {
        try
        {
            File.WriteAllText(write.Path, write.PreviousContent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(
                e,
                "Could not restore previous STRM file {StrmPath} after a publish failure",
                write.Path);
        }
    }

    internal List<DavItem> CollectVideoItems()
    {
        var byId = new Dictionary<Guid, DavItem>();

        foreach (var item in dbClient.Ctx.ChangeTracker.Entries<DavItem>()
                     .Where(x => x.State == EntityState.Added)
                     .Select(x => x.Entity)
                     .Where(IsStrmCandidate))
        {
            byId[item.Id] = item;
        }

        foreach (var item in dbClient.Ctx.Items
                     .Where(x => x.HistoryItemId == historyItemId
                                 && x.Type != DavItem.ItemType.Directory)
                     .AsEnumerable()
                     .Where(IsStrmCandidate))
        {
            byId.TryAdd(item.Id, item);
        }

        return byId.Values.ToList();
    }

    internal static bool IsStrmCandidate(DavItem item) =>
        FilenameUtil.IsVideoFile(item.Name)
        && !item.Name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes (or updates) the STRM sidecar for a DavItem. Shared by queue post-processing
    /// and the Recreate STRM maintenance task. Returns null when no write was needed;
    /// otherwise the written path plus the pre-write content (null when newly created).
    /// </summary>
    internal static async Task<StrmWrite?> WriteStrmFileAsync(
        ConfigManager configManager,
        DavItem davItem,
        bool forceRewrite,
        CancellationToken cancellationToken = default)
    {
        if (!IsStrmCandidate(davItem))
            return null;

        var strmFilePath = Path.GetFullPath(GetStrmFilePath(configManager, davItem));
        var completedDownloadsRoot = Path.GetFullPath(configManager.GetStrmCompletedDownloadDir());
        if (!IsPathWithinRoot(strmFilePath, completedDownloadsRoot))
            throw new IOException($"Generated STRM path '{strmFilePath}' escapes its configured output directory.");
        if (HasSymlinkedAncestor(strmFilePath, completedDownloadsRoot))
            throw new IOException($"Generated STRM path '{strmFilePath}' is beneath a symbolic-link directory.");

        var directoryName = Path.GetDirectoryName(strmFilePath);
        if (directoryName != null)
            await Task.Run(() => Directory.CreateDirectory(directoryName), cancellationToken).ConfigureAwait(false);
        if (HasSymlinkedAncestor(strmFilePath, completedDownloadsRoot))
            throw new IOException($"Generated STRM path '{strmFilePath}' is beneath a symbolic-link directory.");

        var targetUrl = GetStrmTargetUrl(configManager, davItem);
        string? previousContent = null;
        if (File.Exists(strmFilePath))
        {
            previousContent = await File.ReadAllTextAsync(strmFilePath, cancellationToken).ConfigureAwait(false);
            if (!forceRewrite && previousContent == targetUrl)
                return null;
        }

        await File.WriteAllTextAsync(strmFilePath, targetUrl, cancellationToken).ConfigureAwait(false);
        davItem.GeneratedStrmOutputRoot = completedDownloadsRoot;
        davItem.GeneratedStrmPath = strmFilePath;
        davItem.GeneratedStrmTarget = targetUrl;
        return new StrmWrite(strmFilePath, previousContent);
    }

    private async Task<StrmWrite?> CreateStrmFileAsync(DavItem davItem, CancellationToken cancellationToken) =>
        await WriteStrmFileAsync(configManager, davItem, forceRewrite: false, cancellationToken)
            .ConfigureAwait(false);

    internal static string GetStrmFilePath(ConfigManager configManager, DavItem davItem)
    {
        var relativePath = GetPathRelativeToContentRoot(davItem.Path) + ".strm";
        return Path.Join(configManager.GetStrmCompletedDownloadDir(), relativePath);
    }

    /// <summary>
    /// Removes a generated STRM sidecar only when its target belongs to <paramref name="davItem"/>.
    /// </summary>
    /// <returns>True when an owned sidecar file was deleted.</returns>
    internal static bool DeleteStrmFile(DavItem davItem)
    {
        if (!IsStrmCandidate(davItem)
            || string.IsNullOrWhiteSpace(davItem.GeneratedStrmOutputRoot)
            || string.IsNullOrWhiteSpace(davItem.GeneratedStrmPath)
            || string.IsNullOrWhiteSpace(davItem.GeneratedStrmTarget))
            return false;

        var completedDownloadsRoot = Path.GetFullPath(davItem.GeneratedStrmOutputRoot);
        var strmFilePath = Path.GetFullPath(davItem.GeneratedStrmPath);
        if (!IsPathWithinRoot(strmFilePath, completedDownloadsRoot))
            return false;

        if (HasSymlinkedAncestor(strmFilePath, completedDownloadsRoot))
            return false;

        SymlinkAndStrmUtil.ISymlinkOrStrmInfo? strmOrSymlink;
        try
        {
            strmOrSymlink = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(strmFilePath));
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        if (strmOrSymlink is not SymlinkAndStrmUtil.StrmInfo strmInfo)
            return false;

        if (!string.Equals(strmInfo.TargetUrl, davItem.GeneratedStrmTarget, StringComparison.Ordinal))
            return false;

        File.Delete(strmFilePath);
        try
        {
            DeleteEmptyParentDirectories(Path.GetDirectoryName(strmFilePath), completedDownloadsRoot);
        }
        catch (DirectoryNotFoundException)
        {
            // A concurrent cleanup already pruned the empty parent directory.
        }

        return true;
    }

    internal string GetStrmFilePath(DavItem davItem) =>
        GetStrmFilePath(configManager, davItem);

    internal static bool IsPathWithinRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, comparison);
    }

    internal static bool HasSymlinkedAncestor(string path, string root)
    {
        var directoryPath = Path.GetDirectoryName(path);
        while (directoryPath is not null)
        {
            if (new DirectoryInfo(directoryPath).LinkTarget is not null)
                return true;

            if (string.Equals(
                    directoryPath.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return false;

            directoryPath = Path.GetDirectoryName(directoryPath);
        }

        return true;
    }

    internal static void DeleteEmptyParentDirectories(string? directoryPath, string root)
    {
        while (directoryPath != null && IsPathWithinRoot(directoryPath, root))
        {
            if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
                return;

            Directory.Delete(directoryPath);
            directoryPath = Path.GetDirectoryName(directoryPath);
        }
    }

    internal static string GetPathRelativeToContentRoot(string davPath)
    {
        if (davPath.StartsWith(ContentRootPrefix, StringComparison.Ordinal))
            return davPath[ContentRootPrefix.Length..];

        // Fallback: preserve previous parts[2..] behavior for unexpected layouts.
        var parts = davPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 2 ? Path.Join(parts[2..]) : Path.GetFileName(davPath);
    }

    internal static string GetStrmTargetUrl(ConfigManager configManager, DavItem davItem)
    {
        var baseUrl = configManager.GetBaseUrl();
        if (baseUrl.EndsWith('/')) baseUrl = baseUrl.TrimEnd('/');
        var pathUrl = DatabaseStoreSymlinkFile.GetTargetPath(davItem.Id, "", '/');
        if (pathUrl.StartsWith('/')) pathUrl = pathUrl.TrimStart('/');
        var strmKey = configManager.GetStrmKey();
        var downloadKey = DownloadKey.Generate(strmKey, pathUrl);
        var extension = Path.GetExtension(davItem.Name).ToLowerInvariant().TrimStart('.');
        return $"{baseUrl}/view/{pathUrl}?downloadKey={downloadKey}&extension={extension}";
    }

    private string GetStrmTargetUrl(DavItem davItem) =>
        GetStrmTargetUrl(configManager, davItem);
}
