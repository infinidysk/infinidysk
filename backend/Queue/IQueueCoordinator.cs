using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Queue;

/// <summary>
/// Controller and health-facing queue operations. Worker, watchdog, and
/// processing-task ownership stay on <see cref="QueueManager"/>.
/// </summary>
public interface IQueueCoordinator
{
    bool HasActiveQueueItems { get; }
    IReadOnlyList<QueueManager.InProgressQueueItemSnapshot> GetInProgressQueueItems();
    QueueManager.InProgressQueueItemSnapshot? FindInProgressQueueItem(Guid queueItemId);
    IDisposable? TryReserveQueueSlot(int persistedCount, int maxItems, int resumeThreshold);
    void AwakenQueue(DateTime? dateTime = null);
    /// <summary>
    /// Removes the requested items; ids whose in-progress workers ignored
    /// cancellation are returned and remain queued instead of being deleted.
    /// </summary>
    Task<IReadOnlyList<Guid>> RemoveQueueItemsAsync(List<Guid> queueItemIds, DavDatabaseClient dbClient, CancellationToken ct = default);
    Task PauseQueueItemsAsync(List<Guid> queueItemIds, DavDatabaseClient dbClient, CancellationToken ct = default);
    Task ResumeQueueItemsAsync(List<Guid> queueItemIds, DavDatabaseClient dbClient, CancellationToken ct = default);
    Task SetQueueItemsPriorityAsync(
        List<Guid> queueItemIds,
        QueueItem.PriorityOption priority,
        DavDatabaseClient dbClient,
        CancellationToken ct = default);
    Task<DavDatabaseClient.QueueSwitchResult> SwitchQueueItemAsync(
        Guid sourceId,
        string target,
        DavDatabaseClient dbClient,
        CancellationToken ct = default);
    Task<List<Guid>> MoveQueueItemsToTopAsync(
        List<Guid> queueItemIds,
        DavDatabaseClient dbClient,
        CancellationToken ct = default);
    Task<List<Guid>> SetQueueItemsCategoryAsync(
        List<Guid> queueItemIds,
        string category,
        DavDatabaseClient dbClient,
        CancellationToken ct = default);
}
