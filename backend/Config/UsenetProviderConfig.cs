using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public List<ConnectionDetails> Providers { get; set; } = [];

    public int TotalPooledConnections => Math.Max(1, Providers
        .Where(x => x.Type == ProviderType.Pooled)
        .Select(x => x.MaxConnections)
        .Sum());

    public class ConnectionDetails
    {
        /// <summary>
        /// Stable per-account identity used as the metrics/usage key. Distinct from
        /// <see cref="Host"/> so two accounts on the same NNTP host keep independent
        /// bandwidth counters, caps, and scoreboard rows.
        /// </summary>
        public Guid ProviderId { get; set; }

        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public bool SkipTlsVerification { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }

        /// <summary>
        /// Optional per-provider ceiling for BODY/ARTICLE connection leases. Null keeps
        /// the legacy shared-pool scheduling behavior.
        /// </summary>
        public int? MaxTransferConnections { get; set; }

        public int Priority { get; set; }

        public int? PipeliningDepth { get; set; }

        // Optional user-friendly label shown in the UI in place of Host. Host is
        // still the real NNTP target; ProviderId is the stable metrics/logs key.
        public string? Nickname { get; set; }

        /// <summary>
        /// Optional label grouping providers that share the same upstream storage
        /// (identical article availability). When set, and one provider on the
        /// group reports an article as missing (NNTP 430), remaining providers
        /// sharing the same label are skipped for that request. Empty (default)
        /// means the provider is never grouped or skipped.
        /// </summary>
        public string StorageGroup { get; set; } = "";

        // null or 0 = no cap. Used by block-account holders to stop a paid block
        // from being drained beyond its purchased size.
        public long? ByteLimit { get; set; }

        // bytes added to the computed usage. Lets the user seed a starting value
        // when migrating from another client, or adjust drift against the
        // provider's own portal. Set to 0 after a reset.
        public long BytesUsedOffset { get; set; }

        // unix-ms cutoff: ProviderHourly rows older than this don't contribute to
        // the live counter. A reset bumps this to "now" so the gauge starts fresh
        // without losing the historical metrics rows underneath.
        public long BytesUsedResetAt { get; set; }
    }
}
