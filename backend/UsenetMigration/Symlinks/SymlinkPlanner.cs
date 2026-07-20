using Microsoft.EntityFrameworkCore;
using NzbWebDAV.UsenetMigration.Naming;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using Serilog;

namespace NzbWebDAV.UsenetMigration.Symlinks;

/// <summary>A symlink discovered in the arr/Plex library, reduced to what planning needs.</summary>
public readonly record struct SymlinkPair(string SymlinkPath, string TargetPath);

/// <summary>One correlated Altmount virtual file, indexed for suffix matching.</summary>
public sealed class CorrelatedFile
{
    /// <summary>Forward-slash, extension-trimmed path relative to the metadata root.</summary>
    public required string VirtualPath { get; init; }
    public required string StoreRef { get; init; }
    public required long ReleaseFileId { get; init; }

    /// <summary>Set only after the matcher pairs this file with a live leaf.</summary>
    public Guid? NewDavItemId { get; set; }
    public string? MatchMethod { get; set; }
}

/// <summary>The classification of a single library symlink.</summary>
public sealed class SymlinkClassification
{
    public required string Status { get; init; } // rewrite|already-nzbdav|not-altmount|orphan
    public string? NewTarget { get; init; }
    public string? StoreRef { get; init; }
    public string? MatchMethod { get; init; }
}

public sealed class SymlinkPlanSummary
{
    public int Rewrite { get; init; }
    public int AlreadyNzbdav { get; init; }
    public int NotAltmount { get; init; }
    public int Orphan { get; init; }
    public int Total => Rewrite + AlreadyNzbdav + NotAltmount + Orphan;
}

/// <summary>
/// Builds the Step 6 rewrite plan as a pure dry-run: it enumerates the library's
/// symlinks, correlates each to a migrated Altmount virtual file, and writes a
/// <see cref="MigrationSymlinkRewrite"/> row per symlink. It never touches the
/// filesystem beyond reading link targets — applying is <c>SymlinkRewriter</c>'s job.
///
/// <para>Classification (safe by construction — only positively-correlated, matched
/// symlinks become <c>rewrite</c>):</para>
/// <list type="bullet">
/// <item><b>already-nzbdav</b> — the target already points into NzbDAV's mount
///   (<c>{mountDir}</c> or a <c>/.ids/</c> path). Nothing to do.</item>
/// <item><b>rewrite</b> — correlates to a file whose release completed and whose leaf
///   the matcher found. <c>NewTarget</c> is computed by CALLING
///   <see cref="DatabaseStoreSymlinkFile.GetTargetPath(System.Guid,string,char?)"/>,
///   which also provides the canonical lowercase GUID path.</item>
/// <item><b>orphan</b> — correlates to a scanned Altmount file that was not migrated or
///   whose leaf was not matched. Left pointing at Altmount.</item>
/// <item><b>not-altmount</b> — correlates to nothing we scanned. Left untouched. Because
///   the scan persists ReleaseFiles for <i>every</i> release (included or not), this is
///   cleanly distinct from orphan.</item>
/// </list>
/// </summary>
public sealed class SymlinkPlanner(UsenetMigrationStore store, ConfigManager configManager)
{
    /// <summary>Test seam for the live NzbDAV context; production leaves it null.</summary>
    internal Func<DavDatabaseContext>? DavContextFactory { get; set; }

    /// <summary>Test seam for library enumeration; production uses the real filesystem walk.</summary>
    internal Func<string, IEnumerable<SymlinkPair>> SymlinkEnumerator { get; set; } = DefaultEnumerator;

    public async Task<SymlinkPlanSummary> PlanAsync(CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(ct).ConfigureAwait(false);
        var libraryRoot = session.SymlinkLibraryRoot
                          ?? throw new InvalidOperationException("Symlink planning requires SymlinkLibraryRoot to be set.");
        var mountDir = configManager.GetRcloneMountDir();

        // Correlate + match, then classify against a fresh library walk.
        var index = await BuildCorrelationIndexAsync(ct).ConfigureAwait(false);

        await using var ctx = store.NewContext();
        // A plan is a fresh dry-run each time — clear any prior one.
        await ctx.SymlinkRewrites.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        int rewrite = 0, already = 0, notAltmount = 0, orphan = 0;
        foreach (var link in SymlinkEnumerator(libraryRoot))
        {
            ct.ThrowIfCancellationRequested();
            var c = Classify(link.TargetPath, index, mountDir);
            switch (c.Status)
            {
                case "rewrite": rewrite++; break;
                case "already-nzbdav": already++; break;
                case "not-altmount": notAltmount++; break;
                default: orphan++; break;
            }

            ctx.SymlinkRewrites.Add(new MigrationSymlinkRewrite
            {
                SymlinkPath = link.SymlinkPath,
                OldTarget = link.TargetPath,
                NewTarget = c.NewTarget,
                Status = c.Status,
                MatchMethod = c.MatchMethod,
                StoreRef = c.StoreRef,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        var summary = new SymlinkPlanSummary
        {
            Rewrite = rewrite,
            AlreadyNzbdav = already,
            NotAltmount = notAltmount,
            Orphan = orphan,
        };
        Log.Information(
            "Symlink plan: {Total} links — {Rewrite} rewrite, {Orphan} orphan, " +
            "{Already} already-nzbdav, {NotAltmount} not-altmount",
            summary.Total, rewrite, orphan, already, notAltmount);
        return summary;
    }

    /// <summary>
    /// The pure classification of one link target against the correlation index and
    /// mount dir. Uses a longest-suffix correlation so nested virtual paths resolve to
    /// the most specific file.
    /// </summary>
    public static SymlinkClassification Classify(
        string oldTarget,
        IReadOnlyDictionary<string, List<CorrelatedFile>> byBasename,
        string mountDir)
    {
        var normTarget = oldTarget.Replace('\\', '/').TrimEnd('/');

        if (IsNzbdavTarget(normTarget, mountDir))
            return new SymlinkClassification { Status = "already-nzbdav" };

        var correlated = FindCorrelation(normTarget, byBasename);
        if (correlated is null)
            return new SymlinkClassification { Status = "not-altmount" };

        if (correlated.NewDavItemId is { } davItemId)
        {
            return new SymlinkClassification
            {
                Status = "rewrite",
                NewTarget = DatabaseStoreSymlinkFile.GetTargetPath(davItemId, mountDir, '/'),
                StoreRef = correlated.StoreRef,
                MatchMethod = correlated.MatchMethod,
            };
        }

        return new SymlinkClassification { Status = "orphan", StoreRef = correlated.StoreRef };
    }

    private static bool IsNzbdavTarget(string normTarget, string mountDir)
    {
        if (normTarget.Contains("/.ids/", StringComparison.Ordinal))
            return true;
        var normMount = mountDir.Replace('\\', '/').TrimEnd('/');
        return !string.IsNullOrEmpty(normMount)
               && normTarget.StartsWith(normMount + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static CorrelatedFile? FindCorrelation(
        string normTarget, IReadOnlyDictionary<string, List<CorrelatedFile>> byBasename)
    {
        var basename = Path.GetFileName(normTarget).ToLowerInvariant();
        if (!byBasename.TryGetValue(basename, out var candidates))
            return null;

        CorrelatedFile? best = null;
        foreach (var c in candidates)
        {
            var isSuffix = normTarget.Equals(c.VirtualPath, StringComparison.OrdinalIgnoreCase)
                           || normTarget.EndsWith("/" + c.VirtualPath, StringComparison.OrdinalIgnoreCase);
            if (isSuffix && (best is null || c.VirtualPath.Length > best.VirtualPath.Length))
                best = c;
        }

        return best;
    }

    /// <summary>
    /// Loads every scanned file, runs the matcher over completed releases to fill
    /// <c>NewDavItemId</c> (persisting it back), and returns the basename-indexed
    /// correlation set.
    /// </summary>
    private async Task<Dictionary<string, List<CorrelatedFile>>> BuildCorrelationIndexAsync(CancellationToken ct)
    {
        await using var ctx = store.NewContext();

        var files = await ctx.ReleaseFiles.ToListAsync(ct).ConfigureAwait(false);

        // Completed releases only: StoreRef -> NzoId.
        var submissions = await ctx.Submissions
            .Select(s => new { s.StoreRef, s.State, s.NzoId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var submissionByStore = submissions.ToDictionary(s => s.StoreRef, StringComparer.Ordinal);
        var nzoByStore = submissions
            .Where(s => (s.State is "completed" or "history_cleared") && Guid.TryParse(s.NzoId, out _))
            .ToDictionary(s => s.StoreRef, s => Guid.Parse(s.NzoId!), StringComparer.Ordinal);
        foreach (var invalid in submissions.Where(s => (s.State is "completed" or "history_cleared")
                                                        && !Guid.TryParse(s.NzoId, out _)))
        {
            Log.Warning(
                "Unable to correlate symlinks for {StoreRef}: State={State}, invalid NzoId={NzoId}",
                invalid.StoreRef, invalid.State, invalid.NzoId);
        }

        var filesByStore = files.GroupBy(f => f.StoreRef, StringComparer.Ordinal);
        var matchByFileId = new Dictionary<long, (Guid DavItemId, string Method)>();
        foreach (var file in files)
            file.NewDavItemId = null;

        foreach (var group in filesByStore)
        {
            if (!nzoByStore.TryGetValue(group.Key, out var nzoId))
                continue; // not a completed release ⇒ its files stay orphan candidates

            var releaseFiles = group.ToList();
            var submission = submissionByStore[group.Key];
            var leaves = await SymlinkMatcher.LoadLeavesAsync(nzoId, DavContextFactory, ct).ConfigureAwait(false);
            Log.Information(
                "Symlink correlation for {StoreRef}: State={State}, NzoId={NzoId}, " +
                "AltmountFiles={FileCount}, NzbdavLeaves={LeafCount}, Identity={Identity}",
                group.Key, submission.State, nzoId, releaseFiles.Count, leaves.Count,
                leaves.Count == 0
                    ? "none"
                    : string.Join(",", leaves.Select(l => l.IdentityMethod).Distinct(StringComparer.Ordinal)));
            Log.Debug(
                "Symlink correlation candidates for {StoreRef}: Altmount={AltmountFiles}; Nzbdav={NzbdavLeaves}",
                group.Key,
                releaseFiles.Select(f => new { f.VirtualPath, f.NormalisedName, f.FileSize }).ToList(),
                leaves.Select(l => new { l.Name, NormalisedName = MatchKey.ForLeaf(l.Name), l.FileSize }).ToList());
            if (leaves.Count == 0)
            {
                Log.Warning(
                    "No NzbDAV leaf DavItems found for {StoreRef} using NzoId {NzoId} " +
                    "through NzbBlobId or HistoryItemId",
                    group.Key, nzoId);
            }
            var matches = SymlinkMatcher.Match(
                releaseFiles.Select(f => new MatchableFile
                {
                    ReleaseFileId = f.Id,
                    NormalisedName = f.NormalisedName,
                    NormalisedRelativePath = MatchKey.ForRelativePath(f.VirtualPath),
                    FileSize = f.FileSize,
                }).ToList(),
                leaves);

            foreach (var m in matches)
            {
                if (m.DavItemId is { } id && m.MatchMethod is { } method)
                {
                    matchByFileId[m.ReleaseFileId] = (id, method);
                    var rf = releaseFiles.First(f => f.Id == m.ReleaseFileId);
                    Log.Debug(
                        "Matched Altmount file {VirtualPath} to DavItem {DavItemId} using {MatchMethod}",
                        rf.VirtualPath, id, method);
                    rf.NewDavItemId = id.ToString(); // Persist only positively matched live leaves.
                }
            }

            foreach (var unmatched in matches.Where(m => m.DavItemId == null))
            {
                var rf = releaseFiles.First(f => f.Id == unmatched.ReleaseFileId);
                Log.Warning(
                    "Unable to match Altmount file {VirtualPath} using normalized name {NormalizedName} " +
                    "and size {FileSize}. Available leaves: {Leaves}",
                    rf.VirtualPath, rf.NormalisedName, rf.FileSize,
                    leaves.Select(l => new { l.DavItemId, l.Name, l.FileSize }).ToList());
            }
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        var historical = await LoadHistoricalCorrelationsAsync(ctx, ct).ConfigureAwait(false);
        var correlations = new Dictionary<string, CorrelatedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in historical)
            correlations[item.VirtualPath] = item;
        foreach (var f in files)
        {
            var current = new CorrelatedFile
            {
                VirtualPath = f.VirtualPath,
                StoreRef = f.StoreRef,
                ReleaseFileId = f.Id,
            };
            if (matchByFileId.TryGetValue(f.Id, out var match))
            {
                current.NewDavItemId = match.DavItemId;
                current.MatchMethod = match.Method;
            }

            if (current.NewDavItemId != null || !correlations.ContainsKey(current.VirtualPath))
                correlations[current.VirtualPath] = current;
        }

        var index = new Dictionary<string, List<CorrelatedFile>>(StringComparer.Ordinal);
        foreach (var entry in correlations.Values)
        {
            var basename = Path.GetFileName(entry.VirtualPath.Replace('\\', '/').TrimEnd('/')).ToLowerInvariant();
            if (!index.TryGetValue(basename, out var list))
                index[basename] = list = new List<CorrelatedFile>();
            list.Add(entry);
        }

        return index;
    }

    private async Task<List<CorrelatedFile>> LoadHistoricalCorrelationsAsync(
        UsenetMigrationDbContext migrationContext,
        CancellationToken ct)
    {
        var releases = await migrationContext.MigratedReleases.AsNoTracking()
            .Where(r => r.SourceType == "altmount")
            .ToDictionaryAsync(r => r.Id, ct)
            .ConfigureAwait(false);
        if (releases.Count == 0)
            return new List<CorrelatedFile>();

        var files = await migrationContext.MigratedFiles.AsNoTracking()
            .Where(f => releases.Keys.Contains(f.MigratedReleaseId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (files.Count == 0)
            return new List<CorrelatedFile>();

        var validIds = new HashSet<Guid>();
        await using var davContext = DavContextFactory?.Invoke() ?? new DavDatabaseContext();
        foreach (var batch in files.Select(f => f.DavItemId).Distinct().Chunk(500))
        {
            var found = await davContext.Items.AsNoTracking()
                .Where(i => batch.Contains(i.Id) && i.Type == DavItem.ItemType.UsenetFile)
                .Select(i => i.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            validIds.UnionWith(found);
        }

        var stale = files.Count(f => !validIds.Contains(f.DavItemId));
        if (stale > 0)
            Log.Warning("Ignored {StaleCount} stale historical migration file mapping(s)", stale);

        return files
            .Where(f => validIds.Contains(f.DavItemId))
            .Select(f => new CorrelatedFile
            {
                VirtualPath = f.VirtualPath,
                StoreRef = releases[f.MigratedReleaseId].SourceReleaseId,
                ReleaseFileId = 0,
                NewDavItemId = f.DavItemId,
                MatchMethod = "provenance",
            })
            .ToList();
    }

    private static IEnumerable<SymlinkPair> DefaultEnumerator(string libraryRoot) =>
        SymlinkAndStrmUtil.GetAllSymlinksAndStrms(libraryRoot)
            .OfType<SymlinkAndStrmUtil.SymlinkInfo>()
            .Select(s => new SymlinkPair(s.SymlinkPath, s.TargetPath));
}
