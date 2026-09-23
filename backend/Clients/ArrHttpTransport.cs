using System.Net;

namespace NzbWebDAV.Clients;

/// <summary>
/// Shared transport for Radarr/Sonarr/Prowlarr management API calls. Single-label
/// hostnames (Docker service names such as <c>sonarr</c>) always connect directly,
/// even when a process-wide proxy is configured; every other destination keeps the
/// default proxy policy, including NO_PROXY rules and proxy credentials.
/// </summary>
public static class ArrHttpTransport
{
    internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    public static SocketsHttpHandler CreateHandler() => CreateHandler(HttpClient.DefaultProxy);

    internal static SocketsHttpHandler CreateHandler(IWebProxy defaultProxy) => new()
    {
        // Bounded reuse lets replacement connections pick up a recreated container's address.
        PooledConnectionLifetime = PooledConnectionLifetime,
        Proxy = new SingleLabelBypassProxy(defaultProxy),
        UseProxy = true,
    };

    public static bool IsSingleLabelHost(Uri destination) =>
        destination.HostNameType == UriHostNameType.Dns && !destination.IdnHost.Contains('.', StringComparison.Ordinal);

    public static string DescribeRouting(Uri destination) =>
        IsSingleLabelHost(destination) ? "direct (single-label hostname)" : "default proxy policy";

    internal sealed class SingleLabelBypassProxy(IWebProxy inner) : IWebProxy
    {
        public IWebProxy Inner => inner;

        public ICredentials? Credentials
        {
            get => inner.Credentials;
            set => inner.Credentials = value;
        }

        public Uri? GetProxy(Uri destination) =>
            IsSingleLabelHost(destination) ? null : inner.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            IsSingleLabelHost(host) || inner.IsBypassed(host);
    }
}
