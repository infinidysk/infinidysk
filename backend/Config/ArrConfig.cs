using NzbWebDAV.Clients.RadarrSonarr;

namespace NzbWebDAV.Config;

public class ArrConfig
{
    public List<ConnectionDetails> RadarrInstances { get; set; } = [];
    public List<ConnectionDetails> SonarrInstances { get; set; } = [];
    public List<QueueRule> QueueRules { get; set; } = [];

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
