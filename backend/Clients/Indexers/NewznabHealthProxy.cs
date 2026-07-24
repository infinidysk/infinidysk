using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.Indexers;

/// <summary>
/// Optional Newznab health-proxy transform. When enabled, indexer API
/// calls are rewritten to go through the Zyclops community NZB health
/// proxy (https://zyclops.elfhosted.com): the proxy forwards the query to
/// the original indexer (`target` param, apikey passes through), annotates
/// results against its crowd-sourced NZB health database, and returns only
/// releases known to be retrievable on the caller's NNTP backbone(s) —
/// dead posts never reach the queue.
///
/// The endpoint is deliberately a constant, not configuration: the value
/// of the health database comes from every participant using the SAME
/// public service (shared verdicts, consistent per-IP rate limiting and
/// abuse controls). There is nothing to gain from pointing this elsewhere.
///
/// Enabled per indexer via the "health proxy" toggle in Settings →
/// Indexers (off by default — private/boutique indexers whose releases
/// the health database rarely covers would otherwise return thin
/// results).
///
/// Env:
///   NEWZNAB_HEALTH_PROXY_PROVIDER_HOSTS — comma-joined NNTP hostnames to
///                                         filter health by; defaults to the
///                                         hosts of this instance's enabled
///                                         usenet providers.
///   NEWZNAB_HEALTH_PROXY_SHOW_UNKNOWN   — "true" to include releases the
///                                         health database has not tested
///                                         yet (default: only known-good).
///
/// Note: in proxy mode the health service is in the search path — if it is
/// unreachable, searches fail rather than falling back to the raw indexer.
/// That is the intended trade-off of this mode (guaranteed-healthy results);
/// deployments wanting fail-open behavior should leave this disabled.
/// </summary>
public static class NewznabHealthProxy
{
    private const string ProxyEndpoint = "https://zyclops.elfhosted.com/api/v1/newznab/proxy";

    private static readonly Uri ProxyEndpointUri = new(ProxyEndpoint);

    /// <summary>
    /// Supplies the instance's NNTP provider hostnames for the
    /// provider_host filter. Wired at startup from ConfigManager so it
    /// tracks runtime provider changes; the env override wins when set.
    /// </summary>
    public static Func<IEnumerable<string>>? ProviderHostsSource { get; set; }

    public static bool ShowUnknown =>
        string.Equals(EnvironmentUtil.GetEnvironmentVariable("NEWZNAB_HEALTH_PROXY_SHOW_UNKNOWN"),
            "true", StringComparison.OrdinalIgnoreCase);

    public static Uri GetProxyUri() => ProxyEndpointUri;

    /// <summary>
    /// True when the given indexer URL already points at the proxy —
    /// double-wrapping would forward the proxy to itself and mangle auth.
    /// </summary>
    public static bool IsProxyEndpoint(string baseUrl)
    {
        var proxy = ProxyEndpointUri;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        // Full-endpoint match (authority + path prefix), not host-only: an
        // unrelated indexer sharing the proxy's reverse-proxy hostname must
        // still be transformed.
        if (!string.Equals(uri.GetLeftPart(UriPartial.Authority), proxy.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            return false;
        var proxyPath = proxy.AbsolutePath.TrimEnd('/');
        if (proxyPath.Length == 0) return true;
        var indexerPath = uri.AbsolutePath.TrimEnd('/');
        // Exact match or a match at a path-segment boundary — "/proxy-alt"
        // must not be mistaken for "/proxy".
        return indexerPath.Equals(proxyPath, StringComparison.OrdinalIgnoreCase)
               || indexerPath.StartsWith(proxyPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the provider_host value to use when transforming the given
    /// indexer, or null when the transform must not apply (indexer already
    /// points at the proxy, or no provider hosts known — the proxy
    /// requires provider_host). Callers gate on the indexer's per-indexer
    /// UseHealthProxy setting before calling this.
    /// </summary>
    public static string? ResolveProviderHostsFor(string baseUrl)
    {
        if (IsProxyEndpoint(baseUrl)) return null;
        return GetProviderHosts();
    }

    /// <summary>
    /// Comma-joined provider hostnames, or null when none are known (the
    /// proxy requires provider_host, so the transform is skipped then).
    /// </summary>
    public static string? GetProviderHosts()
    {
        var overrideHosts = EnvironmentUtil.GetEnvironmentVariable("NEWZNAB_HEALTH_PROXY_PROVIDER_HOSTS");
        if (!string.IsNullOrWhiteSpace(overrideHosts)) return overrideHosts.Trim();

        var hosts = ProviderHostsSource?.Invoke()
            ?.Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        return hosts is { Count: > 0 } ? string.Join(",", hosts) : null;
    }
}
