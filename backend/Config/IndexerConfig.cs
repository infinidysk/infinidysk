namespace NzbWebDAV.Config;

public class IndexerConfig
{
    // Hard default applied when neither the indexer nor the global override sets a timeout.
    public const int DefaultTimeoutSeconds = 30;

    // Hard default for how many results to gather per indexer per search. Matches the historical
    // hard-coded value, so behavior is unchanged by default. Values above 100 page the indexer.
    public const int DefaultSearchResultLimit = 100;

    // Global HTTP(S) proxy URL applied to every indexer that doesn't set its own ProxyUrl.
    // Empty/null = no proxy. Accepts http://host:port or http://user:pass@host:port.
    public string? ProxyUrl { get; set; }

    // Global per-request HTTP timeout (seconds) applied to indexers that don't set their own.
    // null or <= 0 = fall back to DefaultTimeoutSeconds.
    public int? TimeoutSeconds { get; set; }

    // Global max number of results to gather from each indexer per search. Individual indexers may
    // override this. Above 100 the indexer is paged. null or <= 0 = fall back to DefaultSearchResultLimit.
    public int? SearchResultLimit { get; set; }

    public List<ConnectionDetails> Indexers { get; set; } = [];

    public int GetEffectiveTimeoutSeconds(ConnectionDetails indexer)
    {
        if (indexer.TimeoutSeconds is int per && per > 0) return per;
        if (TimeoutSeconds is int global && global > 0) return global;
        return DefaultTimeoutSeconds;
    }

    public int GetEffectiveSearchResultLimit(ConnectionDetails indexer)
    {
        if (indexer.SearchResultLimit is int per && per > 0) return per;
        if (SearchResultLimit is int global && global > 0) return global;
        return DefaultSearchResultLimit;
    }

    public bool ShouldSkipTlsVerification(string indexerName)
    {
        return Indexers.FirstOrDefault(x => x.Enabled &&
                                            string.Equals(x.Name, indexerName, StringComparison.Ordinal))
            ?.SkipTlsVerification == true;
    }

    public static string? PerIndexerSearchUserAgent(ConnectionDetails? indexer)
        => indexer is null ? null : FirstNonBlank(indexer.SearchUserAgent, indexer.UserAgent);

    public static string? PerIndexerRetrieveUserAgent(ConnectionDetails? indexer)
        => indexer is null ? null : FirstNonBlank(indexer.RetrieveUserAgent, indexer.UserAgent);

    private static string? FirstNonBlank(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);

    public class ConnectionDetails
    {
        public required string Name { get; set; }
        public required string Url { get; set; }
        public required string ApiKey { get; set; }
        public bool Enabled { get; set; } = true;
        public string? UserAgent { get; set; }
        public string? SearchUserAgent { get; set; }
        public string? RetrieveUserAgent { get; set; }
        public bool SkipTlsVerification { get; set; } = false;
        public int MaxRequestsPerMinute { get; set; } = 0;
        public bool EnableStrictMatching { get; set; } = false;
        // Route this indexer's API calls through the Zyclops community NZB
        // health proxy so only known-retrievable releases are returned.
        // Off by default; toggled per indexer in Settings → Indexers.
        public bool UseHealthProxy { get; set; } = false;
        // Per-indexer HTTP(S) proxy URL. Overrides the global ProxyUrl. Empty/null = inherit global.
        public string? ProxyUrl { get; set; }
        // Per-indexer HTTP timeout (seconds). Overrides the global TimeoutSeconds.
        // null or <= 0 = inherit global.
        public int? TimeoutSeconds { get; set; }
        // Per-indexer max results to gather per search. Overrides the global SearchResultLimit.
        // null or <= 0 = inherit global.
        public int? SearchResultLimit { get; set; }
        // Max API search hits per reset window. null or <= 0 = unlimited.
        public int? HitLimit { get; set; }
        // Max NZB download hits per reset window. null or <= 0 = unlimited.
        public int? DownloadLimit { get; set; }
        // Hour of day (0-23, UTC) when the hit counters reset. null = use rolling 24h window
        // (limits hits in the trailing 24 hours from now). Matches NZBHydra2 semantics.
        public int? HitLimitResetTime { get; set; }
        public string? ExtraMovieCategories { get; set; }
        public string? ExtraTvCategories { get; set; }
        public bool IgnoreCategoryFilter { get; set; } = false;
        public ResultFilter? Filter { get; set; }
    }

    public class ResultFilter
    {
        // Master toggle. When false, all rules below are ignored regardless of value.
        public bool Enabled { get; set; } = false;

        // Drop rules. Each defaults to "no effect".
        // Skip releases where password != 0 (RAR-passworded or contains inner archive).
        public bool SkipPassworded { get; set; } = false;

        // Minimum download count to keep a release. 0 = disabled (any count is fine).
        public int MinGrabs { get; set; } = 0;

        // Grace window for the MinGrabs rule. Releases newer than this many hours bypass
        // the MinGrabs check (so a fresh post isn't punished for having no grabs yet).
        // 0 disables the grace window entirely (MinGrabs applies to everything).
        public int GrabsGraceHours { get; set; } = 6;

        // Drop releases older than N days that still have zero recorded grabs.
        // 0 = disabled.
        public int MaxAgeDaysWithoutGrabs { get; set; } = 0;

        // Ranking. When true, this indexer's items will be sorted by grabs descending
        // before merging with other indexers' results. Items missing the grabs attribute
        // sort to the bottom of this indexer's slice (treated as unknown).
        public bool PreferDownloaded { get; set; } = false;
    }
}
