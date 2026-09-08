using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet.Contexts;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Memoizes yEnc headers (segment byte offset/size) so that seeking does not repeatedly
/// re-download article bodies just to read their headers.
/// <para>
/// Reading a yEnc header requires issuing a BODY command, which streams (and therefore
/// transfers) the whole article. Plex/Jellyfin seek around a file many times during
/// playback, and <see cref="Streams.NzbFileStream"/> probes headers on every seek via an
/// interpolation search. yEnc headers are immutable for a given segment id, so caching them
/// turns every repeat probe into a network-free lookup.
/// </para>
/// <para>
/// The cache is intentionally simple and lives on the long-lived streaming client. It is
/// bounded by <see cref="MaxEntries"/> (entries are tiny — a handful of integers and a short
/// name); on overflow it is cleared rather than evicted one-by-one, which is cheap and rare
/// given the high cap.
/// </para>
/// </summary>
public class HeaderCachingNntpClient(INntpClient usenetClient) : WrappingNntpClient(usenetClient)
{
    private const int MaxEntries = 500_000;

    private readonly ConcurrentDictionary<string, UsenetYencHeader> _cache = new();
    private readonly Lock _overflowLock = new();

    public override async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        if (_cache.TryGetValue(segmentId, out var cached))
        {
            if (YencFileValidationContext.MatchesExpectedFile(cached))
                return cached;

            _cache.TryRemove(segmentId, out _);
        }

        var header = await base.GetYencHeadersAsync(segmentId, ct).ConfigureAwait(false);

        if (_cache.Count >= MaxEntries)
        {
            lock (_overflowLock)
            {
                if (_cache.Count >= MaxEntries) _cache.Clear();
            }
        }

        if (YencFileValidationContext.MatchesExpectedFile(header))
            _cache.TryAdd(segmentId, header);
        return header;
    }
}
