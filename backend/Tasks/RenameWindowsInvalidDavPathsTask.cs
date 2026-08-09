using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

/// <summary>
/// Renames existing DavItems whose <see cref="DavItem.Name"/> fails Windows path rules
/// when <c>webdav.windows-safe-paths</c> is enabled. New items are already sanitized on
/// create; this maintenance task repairs rows created before that gate existed.
/// </summary>
public class RenameWindowsInvalidDavPathsTask(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    bool isDryRun,
    Func<DavDatabaseContext>? createContext = null
) : BaseTask
{
    private static List<string> _auditLines = [];

    private DavDatabaseContext CreateContext() => DavDatabaseContexts.Create(createContext);

    protected override async Task ExecuteInternal()
    {
        try
        {
            await RenameInvalidPaths().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to rename Windows-invalid Dav paths.");
        }
    }

    private async Task RenameInvalidPaths()
    {
        PathSanitizer.SetWindowsSafePathsEnabled(configManager.IsWindowsSafePathsEnabled());
        if (!PathSanitizer.IsWindowsSafePathsEnabled)
        {
            Report("Aborted: webdav.windows-safe-paths is disabled. " +
                   "Enable it in WebDAV settings before running this task.");
            return;
        }

        Report("Scanning DavItems for Windows-invalid names...");
        await using var dbContext = CreateContext();
        var items = await dbContext.Items.AsNoTracking().ToListAsync(CancellationToken).ConfigureAwait(false);

        var plan = BuildRenamePlan(items);
        if (plan.Renames.Count == 0)
        {
            _auditLines = [];
            Report("Done. No Windows-invalid DavItem names found.");
            return;
        }

        Report($"Found {plan.Renames.Count} item(s) to rename " +
               $"({plan.PathUpdates.Count} path update(s) including subtree).");

        _auditLines = plan.Renames
            .Select(r => $"{r.OldPath} -> {r.NewPath}")
            .ToList();

        if (isDryRun)
        {
            Report($"Done. Identified {_auditLines.Count} rename(s).");
            return;
        }

        await ApplyPlan(dbContext, plan).ConfigureAwait(false);
        Report($"Done. Renamed {_auditLines.Count} item(s).");
    }

    /// <summary>
    /// Computes renames (Name + Path) and descendant Path-only updates without writing.
    /// Exposed for unit tests.
    /// </summary>
    internal static RenamePlan BuildRenamePlan(IReadOnlyList<DavItem> items)
    {
        var namesById = items.ToDictionary(x => x.Id, x => x.Name);
        var pathsById = items.ToDictionary(x => x.Id, x => x.Path);
        var parentIdsById = items.ToDictionary(x => x.Id, x => x.ParentId);
        var typesById = items.ToDictionary(x => x.Id, x => x.Type);
        var protectedIds = GetProtectedRootIds();

        var invalid = items
            .Where(x => !protectedIds.Contains(x.Id))
            .Where(x => NeedsWindowsRename(x.Name))
            .OrderBy(x => PathDepth(x.Path))
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Id)
            .ToList();

        var renames = new List<RenameEntry>();
        var pathUpdates = new Dictionary<Guid, string>();

        foreach (var item in invalid)
        {
            var currentPath = pathsById[item.Id];
            var parentPath = GetParentPath(currentPath);
            var desiredName = PathSanitizer.SanitizeComponent(namesById[item.Id]);
            var uniqueName = ResolveUniqueName(
                desiredName,
                item.Id,
                parentIdsById[item.Id],
                parentPath,
                namesById,
                pathsById,
                parentIdsById);
            var newPath = JoinDavPath(parentPath, uniqueName);

            if (string.Equals(namesById[item.Id], uniqueName, StringComparison.Ordinal) &&
                string.Equals(currentPath, newPath, StringComparison.Ordinal))
            {
                continue;
            }

            renames.Add(new RenameEntry(
                item.Id,
                typesById[item.Id],
                namesById[item.Id],
                uniqueName,
                currentPath,
                newPath));

            namesById[item.Id] = uniqueName;
            pathsById[item.Id] = newPath;
            pathUpdates[item.Id] = newPath;

            UpdateDescendantPaths(item.Id, currentPath, newPath, pathsById, pathUpdates);
        }

        return new RenamePlan(renames, pathUpdates
            .Select(kv => new PathUpdate(kv.Key, typesById[kv.Key], kv.Value))
            .ToList());
    }

    private async Task ApplyPlan(DavDatabaseContext dbContext, RenamePlan plan)
    {
        Report("Applying renames...");
        var idsToUpdate = plan.PathUpdates.Select(x => x.Id).ToHashSet();
        var tracked = await dbContext.Items
            .Where(x => idsToUpdate.Contains(x.Id))
            .ToListAsync(CancellationToken)
            .ConfigureAwait(false);
        var byId = tracked.ToDictionary(x => x.Id);

        var forgetItems = new List<DavItem>();
        foreach (var rename in plan.Renames)
        {
            forgetItems.Add(new DavItem
            {
                Id = rename.Id,
                Type = rename.Type,
                Path = rename.OldPath,
            });
            forgetItems.Add(new DavItem
            {
                Id = rename.Id,
                Type = rename.Type,
                Path = rename.NewPath,
            });
        }

        var renameById = plan.Renames.ToDictionary(x => x.Id);
        foreach (var update in plan.PathUpdates)
        {
            if (!byId.TryGetValue(update.Id, out var entity))
                continue;

            entity.Path = update.NewPath;
            if (renameById.TryGetValue(update.Id, out var rename))
                entity.Name = rename.NewName;
        }

        await dbContext.SaveChangesAsync(CancellationToken).ConfigureAwait(false);
        _ = DavDatabaseContext.RcloneVfsForget(forgetItems);
        Report($"Applying renames...\nRenamed {plan.Renames.Count}...");
    }

    internal static bool NeedsWindowsRename(string name) =>
        !string.Equals(name, PathSanitizer.SanitizeComponent(name), StringComparison.Ordinal);

    internal static string ResolveUniqueName(
        string desiredName,
        Guid itemId,
        Guid? parentId,
        string parentPath,
        IReadOnlyDictionary<Guid, string> namesById,
        IReadOnlyDictionary<Guid, string> pathsById,
        IReadOnlyDictionary<Guid, Guid?> parentIdsById)
    {
        var candidate = desiredName;
        var suffix = 2;
        while (true)
        {
            var candidatePath = JoinDavPath(parentPath, candidate);
            var nameTaken = false;
            foreach (var (id, name) in namesById)
            {
                if (id == itemId)
                    continue;
                if (parentIdsById[id] == parentId &&
                    string.Equals(name, candidate, StringComparison.Ordinal))
                {
                    nameTaken = true;
                    break;
                }
            }

            var pathTaken = false;
            if (!nameTaken)
            {
                foreach (var (id, path) in pathsById)
                {
                    if (id == itemId)
                        continue;
                    if (string.Equals(path, candidatePath, StringComparison.Ordinal))
                    {
                        pathTaken = true;
                        break;
                    }
                }
            }

            if (!nameTaken && !pathTaken)
                return candidate;

            var extension = Path.GetExtension(desiredName);
            var stem = Path.GetFileNameWithoutExtension(desiredName);
            candidate = $"{stem}_{suffix}{extension}";
            suffix++;
        }
    }

    internal static string JoinDavPath(string parentPath, string name)
    {
        if (string.IsNullOrEmpty(parentPath) || parentPath == "/")
            return "/" + name;
        return parentPath.TrimEnd('/') + "/" + name;
    }

    internal static string GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return "/";
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        if (idx <= 0)
            return "/";
        return trimmed[..idx];
    }

    private static void UpdateDescendantPaths(
        Guid renamedId,
        string oldPath,
        string newPath,
        Dictionary<Guid, string> pathsById,
        Dictionary<Guid, string> pathUpdates)
    {
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
            return;

        var oldPrefix = oldPath.TrimEnd('/') + "/";
        foreach (var (id, path) in pathsById.ToList())
        {
            if (id == renamedId)
                continue;
            if (!path.StartsWith(oldPrefix, StringComparison.Ordinal))
                continue;

            var updated = newPath.TrimEnd('/') + path[oldPath.TrimEnd('/').Length..];
            pathsById[id] = updated;
            pathUpdates[id] = updated;
        }
    }

    private static HashSet<Guid> GetProtectedRootIds() =>
    [
        DavItem.Root.Id,
        DavItem.NzbFolder.Id,
        DavItem.ContentFolder.Id,
        DavItem.SymlinkFolder.Id,
        DavItem.IdsFolder.Id,
    ];

    private static int PathDepth(string path) =>
        path.Count(c => c == '/');

    private void Report(string message)
    {
        var dryRun = isDryRun ? "Dry Run - " : string.Empty;
        _ = websocketManager.SendMessage(WebsocketTopic.RenameWindowsInvalidPathsProgress, $"{dryRun}{message}");
    }

    public static string GetAuditReport()
    {
        return _auditLines.Count > 0
            ? string.Join("\n", _auditLines)
            : "This list is Empty.\nYou must first run the task.";
    }

    internal static void ClearAuditForTests() => _auditLines = [];

    internal sealed record RenameEntry(
        Guid Id,
        DavItem.ItemType Type,
        string OldName,
        string NewName,
        string OldPath,
        string NewPath);

    internal sealed record PathUpdate(Guid Id, DavItem.ItemType Type, string NewPath);

    internal sealed record RenamePlan(
        IReadOnlyList<RenameEntry> Renames,
        IReadOnlyList<PathUpdate> PathUpdates);
}
