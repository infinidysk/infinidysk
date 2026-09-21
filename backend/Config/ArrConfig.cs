using NzbWebDAV.Clients.RadarrSonarr;

namespace NzbWebDAV.Config;

public class ArrConfig
{
    public const int DefaultQueueReplacementSearchLimit = 3;
    public const int DefaultQueueReplacementSearchWindowMinutes = 30;
    public const int DefaultOrphanedQueueItemGraceMinutes = 30;

    public List<ConnectionDetails> RadarrInstances { get; set; } = [];
    public List<ConnectionDetails> SonarrInstances { get; set; } = [];
    public List<QueueRule> QueueRules { get; set; } = [];
    public int QueueReplacementSearchLimit { get; set; } = DefaultQueueReplacementSearchLimit;
    public int QueueReplacementSearchWindowMinutes { get; set; } = DefaultQueueReplacementSearchWindowMinutes;

    /// <summary>
    /// Deleting a series/movie in Arr does not cancel its in-flight download — Arr
    /// deliberately leaves it in the download client queue (Sonarr/Sonarr#1311, closed
    /// upstream as "limited user interest"), where Arr's own queue view can only show it
    /// as an unmatched "Unknown Series"/"Unknown Movie" row that will never import. Left
    /// alone, it burns a connection slot forever. DoNothing (default) preserves today's
    /// behavior, since re-adding the same series/movie before
    /// <see cref="OrphanedQueueItemGraceMinutes"/> elapses can still let Arr resolve and
    /// import the same download.
    /// </summary>
    public QueueAction OrphanedQueueItemAction { get; set; } = QueueAction.DoNothing;

    public int OrphanedQueueItemGraceMinutes { get; set; } = DefaultOrphanedQueueItemGraceMinutes;

    /// <summary>
    /// Clients for enabled instances only. Disabling an instance opts it out of
    /// queue management, Arr-linked repairs, and Arr Health polling.
    /// <see cref="ConnectionDetails.Enabled"/> defaults true so legacy JSON
    /// without the property keeps today's behavior.
    /// </summary>
    // ReSharper disable once InvokeAsExtensionMethod
    public IEnumerable<ArrClient> GetArrClients() => Enumerable.Concat(
        RadarrInstances.Where(x => x.Enabled).Select(ArrClient (x) => new RadarrClient(x.Host, x.ApiKey)),
        SonarrInstances.Where(x => x.Enabled).Select(ArrClient (x) => new SonarrClient(x.Host, x.ApiKey))
    );

    public IEnumerable<(string AppType, ConnectionDetails Details)> GetEnabledInstances() =>
        RadarrInstances.Where(x => x.Enabled).Select(x => ("radarr", x))
            .Concat(SonarrInstances.Where(x => x.Enabled).Select(x => ("sonarr", x)));

    public int GetInstanceCount() =>
        RadarrInstances.Count + SonarrInstances.Count;

    public int EffectiveQueueReplacementSearchLimit() => Math.Clamp(QueueReplacementSearchLimit, 1, 10);

    public TimeSpan EffectiveQueueReplacementSearchWindow() =>
        TimeSpan.FromMinutes(Math.Clamp(QueueReplacementSearchWindowMinutes, 1, 1440));

    public TimeSpan EffectiveOrphanedQueueItemGrace() =>
        TimeSpan.FromMinutes(Math.Clamp(OrphanedQueueItemGraceMinutes, 1, 1440));

    public static string MakeInstanceKey(string appType, string host) =>
        $"{appType}|{host.TrimEnd('/').ToLowerInvariant()}";

    public class ConnectionDetails
    {
        public string? Name { get; set; }
        public required string Host { get; set; }
        public required string ApiKey { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public class QueueRule
    {
        public string Message { get; set; } = null!;
        public QueueAction Action { get; set; }
    }

    public enum QueueAction
    {
        DoNothing = 0,
        Remove = 1,
        RemoveAndBlocklist = 2,
        RemoveAndBlocklistAndSearch = 3
    }
}
