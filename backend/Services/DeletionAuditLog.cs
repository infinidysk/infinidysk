using System.Collections.Concurrent;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Structured audit trail for every <see cref="DavItem"/> deletion. Phase 1 of the
/// content-disappearing investigation (bugs.md #117): no DB table, just a grep-able Serilog
/// line so every deletion source/reason is diagnosable from the logs.
/// </summary>
public static class DeletionAuditLog
{
    /// <summary>
    /// Two-person-rule-style guardrail: a single cleanup that deletes more than this many
    /// mounted files gets a loud warning before proceeding. SAB semantics are unchanged —
    /// this never blocks the delete.
    /// </summary>
    public const int BulkDeleteWarningThreshold = 500;

    private const int RecentBufferCapacity = 200;
    private static readonly ConcurrentQueue<DeletionAuditEntry> Recent = new();

    public sealed record DeletionAuditEntry(
        DateTimeOffset At,
        string Source,
        string Path,
        string Reason,
        Guid? ItemId,
        Guid? ParentId,
        int? Count);

    /// <summary>
    /// Logs a single dav-item deletion.
    /// </summary>
    public static void Record(string source, DavItem item, string reason)
    {
        Log.Information(
            "dav-delete source={Source} id={Id} path={Path} reason={Reason}",
            source, item.Id, item.Path, reason);
        Enqueue(new DeletionAuditEntry(
            DateTimeOffset.UtcNow,
            source,
            item.Path,
            reason,
            item.Id,
            null,
            null));
    }

    /// <summary>
    /// Logs a batch dav-item deletion (e.g. a cascading child sweep or a bulk unlinked-file
    /// removal) as a single line: emitting one line per item would flood the logs for large
    /// batches, so this records the count, an optional parent id, and a small path sample.
    /// </summary>
    public static void RecordBatch(
        string source,
        IReadOnlyCollection<DavItem> items,
        string reason,
        Guid? parentId = null,
        int sampleSize = 5)
    {
        if (items.Count == 0) return;

        var samplePaths = string.Join(", ", items.Take(sampleSize).Select(x => x.Path));
        Log.Information(
            "dav-delete source={Source} count={Count} parentId={ParentId} reason={Reason} samplePaths={SamplePaths}",
            source, items.Count, parentId, reason, samplePaths);
        Enqueue(new DeletionAuditEntry(
            DateTimeOffset.UtcNow,
            source,
            samplePaths,
            reason,
            null,
            parentId,
            items.Count));
    }

    /// <summary>
    /// Warn when a single cleanup operation is about to delete a large number of mounted items.
    /// </summary>
    public static void WarnBulkDelete(string source, int count, string detail)
    {
        if (count <= BulkDeleteWarningThreshold)
            return;

        Log.Warning(
            "dav-delete-bulk source={Source} count={Count} threshold={Threshold} detail={Detail}",
            source, count, BulkDeleteWarningThreshold, detail);
    }

    internal static IReadOnlyList<DeletionAuditEntry> GetRecent() => Recent.ToArray();

    internal static void Reset() => Recent.Clear();

    private static void Enqueue(DeletionAuditEntry entry)
    {
        Recent.Enqueue(entry);
        // ConcurrentQueue has no capacity bound; drop the oldest entries past the cap.
        while (Recent.Count > RecentBufferCapacity && Recent.TryDequeue(out _))
        {
            // Trimming is the condition, not the body.
        }
    }
}
