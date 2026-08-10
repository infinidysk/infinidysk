using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.Controllers.DeleteWebdavItem;

internal static class DeleteWebdavItemSupport
{
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
