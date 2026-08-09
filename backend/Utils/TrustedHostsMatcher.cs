using System.Net;
using Serilog;

namespace NzbWebDAV.Utils;

/// <summary>
/// Parses a comma/whitespace-separated allowlist of hostnames, IP literals, CIDR
/// ranges, or <c>*</c> used to exempt destinations from the addurl SSRF guard.
/// </summary>
public sealed class TrustedHostsMatcher
{
    public static readonly TrustedHostsMatcher Empty = new(
        trustAll: false,
        hostnames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        addresses: [],
        networks: []);

    private readonly bool _trustAll;
    private readonly HashSet<string> _hostnames;
    private readonly HashSet<IPAddress> _addresses;
    private readonly List<IPNetwork> _networks;

    private TrustedHostsMatcher(
        bool trustAll,
        HashSet<string> hostnames,
        HashSet<IPAddress> addresses,
        List<IPNetwork> networks)
    {
        _trustAll = trustAll;
        _hostnames = hostnames;
        _addresses = addresses;
        _networks = networks;
    }

    public bool IsEmpty => !_trustAll && _hostnames.Count == 0 && _addresses.Count == 0 && _networks.Count == 0;

    public static TrustedHostsMatcher Parse(string? rawEntries)
    {
        if (string.IsNullOrWhiteSpace(rawEntries))
            return Empty;

        var hostnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<IPAddress>();
        var networks = new List<IPNetwork>();
        var trustAll = false;

        foreach (var part in SplitEntries(rawEntries))
        {
            if (part == "*")
            {
                trustAll = true;
                continue;
            }

            if (part.Contains('/', StringComparison.Ordinal) && IPNetwork.TryParse(part, out var network))
            {
                networks.Add(network);
                continue;
            }

            if (IPAddress.TryParse(part, out var address))
            {
                addresses.Add(Normalize(address));
                continue;
            }

            // Reject entries that look like broken CIDR/IP before treating as hostname.
            if (part.Contains('/', StringComparison.Ordinal) || part.Any(char.IsWhiteSpace))
            {
                Log.Warning("Ignoring invalid trusted-hosts entry: {Entry}", part);
                continue;
            }

            hostnames.Add(part);
        }

        if (!trustAll && hostnames.Count == 0 && addresses.Count == 0 && networks.Count == 0)
            return Empty;

        return new TrustedHostsMatcher(trustAll, hostnames, addresses, networks);
    }

    public bool IsTrustedHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (_trustAll)
            return true;

        if (_hostnames.Contains(host))
            return true;

        // Allow listing an IP literal as a "host" entry to trust that destination by name.
        if (IPAddress.TryParse(host, out var literal) && IsTrustedAddress(literal))
            return true;

        return false;
    }

    public bool IsTrustedAddress(IPAddress address)
    {
        if (_trustAll)
            return true;

        address = Normalize(address);

        if (_addresses.Contains(address))
            return true;

        foreach (var network in _networks.Where(network => network.Contains(address)))
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitEntries(string rawEntries)
    {
        return rawEntries
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        return address;
    }
}
