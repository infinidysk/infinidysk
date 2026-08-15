using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.Controllers.DeleteWebdavItem;

internal static class DeleteWebdavItemSupport
{
    /// <summary>
    /// Resolves a WebDAV item from an Explore/API path. Literal names are tried first so
    /// files that actually contain <c>%2C</c> (or other percent sequences) are found;
    /// percent-decoded lookup is only a fallback for callers that still send encoded paths.
    /// </summary>
    public static async Task<DavItem?> ResolvePathAsync(
        DavDatabaseClient dbClient,
        string path,
        CancellationToken cancellationToken)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var item = await LookupAsync(dbClient, parts, cancellationToken).ConfigureAwait(false);
        if (item is not null) return item;

        var unescaped = new string[parts.Length];
        var changed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            unescaped[i] = Uri.UnescapeDataString(parts[i]);
            changed |= !string.Equals(unescaped[i], parts[i], StringComparison.Ordinal);
        }

        if (!changed) return null;
        return await LookupAsync(dbClient, unescaped, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DavItem?> LookupAsync(
        DavDatabaseClient dbClient,
        string[] parts,
        CancellationToken cancellationToken)
    {
        var absolutePath = "/" + string.Join('/', parts);
        var byPath = await dbClient.GetItemByPathAsync(absolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (byPath is not null) return byPath;

        var current = DavItem.Root;
        foreach (var name in parts)
        {
            var child = await dbClient.GetDirectoryChildAsync(current.Id, name, cancellationToken)
                .ConfigureAwait(false);
            if (child is null) return null;
            current = child;
        }

        return current;
    }

    public static string? ValidateDeletableRoot(string absolutePath)
    {
        var parts = absolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "Cannot delete the root directory.";

        return parts[0] switch
        {
            "content" => null,
            "nzbs" => "Items under /nzbs can be removed from the Queue page",
            "completed-symlinks" => "Entries are cleared from the History page",
            ".ids" => "Internal system view",
            _ => "Items can only be deleted from under /content",
        };
    }

    public static (string? category, string? jobName) TryGetContentJob(string absolutePath)
    {
        var parts = absolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return (null, null);
        if (!string.Equals(parts[0], "content", StringComparison.Ordinal)) return (null, null);
        return (parts[1], parts[2]);
    }

    public static bool HasInProgressDownload(
        string absolutePath,
        IEnumerable<QueueManager.InProgressQueueItemSnapshot> inProgress)
    {
        var (category, jobName) = TryGetContentJob(absolutePath);
        if (category is null || jobName is null) return false;
        return inProgress.Any(x =>
            string.Equals(x.QueueItem.Category, category, StringComparison.Ordinal)
            && string.Equals(x.QueueItem.JobName, jobName, StringComparison.Ordinal));
    }
}
