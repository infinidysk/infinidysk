namespace NzbWebDAV.Api.Controllers.GetProviderUsage;

public class GetProviderUsageResponse : BaseApiResponse
{
    public List<ProviderUsageItem> Providers { get; set; } = new();

    public class ProviderUsageItem
    {
        // Index into the user's UsenetProviderConfig.Providers list at the time
        // of the request. The settings UI joins live stats by ProviderId instead.
        public int Index { get; set; }
        public string Host { get; set; } = string.Empty;
        public string? Nickname { get; set; }
        /// <summary>
        /// Stable metrics key (<c>ProviderId</c> in Guid "N" format — no dashes).
        /// Callers must normalize dashed config UUIDs the same way before joining.
        /// Null when the provider has not yet been assigned an id.
        /// </summary>
        public string? ProviderId { get; set; }
        public long BytesUsed { get; set; }
        public long? ByteLimit { get; set; }
        public bool OverLimit { get; set; }
        // Average bytes downloaded per day over the last 7 days for this host.
        // Zero when there's no recent activity (or no data yet).
        public long BytesPerDay { get; set; }
        // Projected days until the cap is hit at the current 7-day burn rate.
        // Null when the user hasn't set a cap, when burn rate is zero, or when
        // the cap is already exceeded — in any of those cases there's no
        // honest number to display.
        public double? DaysRemaining { get; set; }
        /// <summary>
        /// Server-reported connection limit learned from a 502 rejection, if any.
        /// Null when the provider has not hit a connection-limit rejection.
        /// </summary>
        public int? LearnedConnectionLimit { get; set; }
        /// <summary>
        /// The pool's current effective max (after any learned-limit shrink).
        /// Equals configured MaxConnections when no shrink has occurred.
        /// </summary>
        public int? EffectiveMaxConnections { get; set; }
    }
}
