using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.Library;

/// <summary>
/// Discovers symlinks/STRMs under the primary and additional scan directories and upserts
/// <see cref="LibraryLinkMap"/> rows. Never follows a link whose target sits
/// under the rclone mount (self-deadlock risk); such targets are classified
/// from text only. Links absent from a completed full scan are marked Stale.
/// </summary>
public sealed class LibraryCatalogScanner(
    ConfigManager configManager,
    IDbContextFactory<DavDatabaseContext> dbContextFactory) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettingsPollInterval = TimeSpan.FromSeconds(30);

    public DateTimeOffset? LastSuccessfulScanAt { get; private set; }
    public string? LastScanWarning { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        DateTimeOffset? lastScanFinishedAt = null;
        string? lastRootSelection = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var rootSelection = string.Join("\n",
                new[] { configManager.GetLibraryDir() ?? "" }
                    .Concat(configManager.GetMediaLibraryScanDirs())
                    .Append(configManager.IsMediaLibraryVideoOnly().ToString()));
            if (!string.Equals(rootSelection, lastRootSelection, StringComparison.Ordinal))
            {
                lastRootSelection = rootSelection;
                lastScanFinishedAt = null;
            }
            if (!configManager.IsMediaLibraryEnabled())
            {
                lastScanFinishedAt = null;
            }
            else if (lastScanFinishedAt is null ||
                     DateTimeOffset.UtcNow >= lastScanFinishedAt.Value + configManager.GetMediaLibraryScanInterval())
            {
                try { await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    e.LogWarningKnownOrStack("Library catalog scan failed.");
                    LastScanWarning = e.Message;
                }
                lastScanFinishedAt = DateTimeOffset.UtcNow;
            }

            try { await Task.Delay(SettingsPollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task ReconcileOnceAsync(CancellationToken ct)
    {
        if (!configManager.IsMediaLibraryEnabled()) return;
        var libraryRoot = configManager.GetLibraryDir();
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            LastScanWarning = "Library directory is not configured.";
            return;
        }
        var videoOnly = configManager.IsMediaLibraryVideoOnly();

        var mountDir = configManager.GetRcloneMountDir();
        var primaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        var roots = new List<(bool IsPrimary, string Path)> { (true, primaryRoot) };
        roots.AddRange(configManager.GetMediaLibraryScanDirs()
            .Where(path => !string.Equals(path, primaryRoot, StringComparison.Ordinal))
            .Select(path => (false, path)));
        var normalizedMount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountDir));
        if (roots.Any(root => IsSameOrWithin(root.Path, normalizedMount) ||
                              IsSameOrWithin(normalizedMount, root.Path)) ||
            roots.SelectMany((root, index) => roots.Skip(index + 1)
                .Select(other => (root.Path, OtherPath: other.Path)))
                .Any(pair => IsSameOrWithin(pair.Path, pair.OtherPath) ||
                             IsSameOrWithin(pair.OtherPath, pair.Path)))
        {
            LastScanWarning = "Media Library scan directories overlap each other or the rclone mount.";
            return;
        }
        List<(bool IsPrimary, string RootPath, string LinkPath, string TargetText, bool IsStrm)> discovered = [];
        try
        {
            foreach (var (isPrimary, rootPath) in roots)
            {
                discovered.AddRange(SymlinkAndStrmUtil.GetAllSymlinksAndStrms(rootPath)
                    .Where(info => !videoOnly || MediaLibraryVideoFilter.IsVideoLink(info))
                    .Select(info => info switch
                    {
                        SymlinkAndStrmUtil.SymlinkInfo s => (isPrimary, rootPath, s.SymlinkPath, s.TargetPath, false),
                        SymlinkAndStrmUtil.StrmInfo s => (isPrimary, rootPath, s.StrmPath, s.TargetUrl, true),
                        _ => throw new InvalidOperationException("Unknown link type"),
                    }));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            e.LogWarningKnownOrStack("Library catalog discovery failed.");
            LastScanWarning = e.Message;
            return;
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existingRows = await context.LinkMaps.ToListAsync(ct).ConfigureAwait(false);
        var existingByKey = existingRows.ToDictionary(row => row.LinkPath, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (isPrimary, rootPath, linkPath, targetText, isStrm) in discovered)
        {
            ct.ThrowIfCancellationRequested();
            ClassifiedLink classified;
            try
            {
                classified = LibraryLinkClassifier.Classify(linkPath, targetText, isStrm, mountDir, rootPath);
            }
            catch (ArgumentException)
            {
                continue; // escaping link — skip, never touch it
            }

            // Keep legacy primary mappings relative. Extra roots use absolute paths so
            // identical relative names in different roots cannot overwrite one another.
            var key = isPrimary ? classified.RelativeLinkPath : Path.GetFullPath(linkPath);
            seen.Add(key);
            var status = await ResolveStatusAsync(context, classified, mountDir, ct).ConfigureAwait(false);
            existingByKey.TryGetValue(key, out var existing);
            if (existing is null)
            {
                context.LinkMaps.Add(new LibraryLinkMap
                {
                    Id = Guid.NewGuid(),
                    DavItemId = classified.DavItemId,
                    LinkPath = key,
                    TargetText = classified.TargetText,
                    MappingType = classified.MappingType,
                    Status = status,
                    LastSeenUtc = DateTime.UtcNow,
                    LastCheckedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.DavItemId = classified.DavItemId;
                existing.TargetText = classified.TargetText;
                existing.MappingType = classified.MappingType;
                existing.Status = status;
                existing.LastSeenUtc = DateTime.UtcNow;
                existing.LastCheckedUtc = DateTime.UtcNow;
            }
        }

        foreach (var row in existingRows.Where(row =>
                     !seen.Contains(row.LinkPath) && row.Status != LibraryLinkStatus.Stale))
            row.Status = LibraryLinkStatus.Stale;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        LastSuccessfulScanAt = DateTimeOffset.UtcNow;
        LastScanWarning = null;
    }

    private static bool IsSameOrWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static async Task<LibraryLinkStatus> ResolveStatusAsync(
        DavDatabaseContext context,
        ClassifiedLink classified,
        string mountDir,
        CancellationToken ct)
    {
        if (classified.MappingType == LibraryMappingType.Internal)
        {
            var exists = classified.DavItemId.HasValue && await context.Items
                .AnyAsync(x => x.Id == classified.DavItemId.Value, ct)
                .ConfigureAwait(false);
            return exists ? LibraryLinkStatus.Valid : LibraryLinkStatus.Broken;
        }

        // Never probe paths under the rclone mount (following them can recurse
        // into WebDAV). External paths may point to a host mount that is not
        // available inside this container, so only call a missing target
        // broken when its parent directory is visible here.
        var absolute = classified.TargetText;
        if (!Path.IsPathRooted(absolute))
            return LibraryLinkStatus.Unchecked;
        if (absolute.StartsWith(mountDir, StringComparison.Ordinal))
            return LibraryLinkStatus.Unchecked;
        try
        {
            if (File.Exists(absolute) || Directory.Exists(absolute))
                return LibraryLinkStatus.Valid;
            var parent = Path.GetDirectoryName(absolute);
            return parent is not null && Directory.Exists(parent)
                ? LibraryLinkStatus.Broken
                : LibraryLinkStatus.Unchecked;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LibraryLinkStatus.Unchecked;
        }
    }
}
