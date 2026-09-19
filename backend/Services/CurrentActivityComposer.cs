using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Services;

/// <summary>
/// Joins authoritative playback state with transport telemetry without
/// reclassifying transport as playback or assigning shared file traffic to viewers.
/// </summary>
public sealed class CurrentActivityComposer(
    PlaybackSessionRegistry playbackRegistry,
    ActiveReadRegistry activeReadRegistry,
    ProviderUsageTracker providerUsageTracker,
    ConfigManager configManager)
{
    public CurrentActivitySnapshot Compose()
    {
        var playback = playbackRegistry.Snapshot();
        var reads = activeReadRegistry.Snapshot();
        var playbackByDav = playback
            .GroupBy(session => session.DavItemId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var readCountByDav = reads
            .Where(read => read.DavItemId.HasValue)
            .GroupBy(read => read.DavItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var usage = providerUsageTracker.SnapshotMany(reads.Select(read => read.Id));
        var displayByMetricsKey = ProviderUsageHelper.BuildDisplayByMetricsKey(
            configManager.GetUsenetProviderConfig().Providers);

        var transport = reads.Select(read =>
        {
            List<AuthoritativePlaybackSession> matches = [];
            var scope = TransportCorrelationScope.None;

            var nativeMatch = playback.FirstOrDefault(session =>
                session.SourceType == PlaybackSourceType.InfiniDysk
                && read.PlayerSession is not null
                && string.Equals(session.NativeSessionId, read.PlayerSession, StringComparison.Ordinal)
                && (!read.DavItemId.HasValue || read.DavItemId.Value == session.DavItemId));

            if (nativeMatch is not null)
            {
                matches = [nativeMatch];
                scope = TransportCorrelationScope.Session;
            }
            else if (read.DavItemId is { } davItemId
                     && playbackByDav.TryGetValue(davItemId, out var fileMatches))
            {
                matches = fileMatches;
                scope = TransportCorrelationScope.File;
            }

            var shared = scope == TransportCorrelationScope.File
                         && (matches.Count > 1
                             || (read.DavItemId is { } id
                                 && readCountByDav.GetValueOrDefault(id) > 1));

            return new CurrentTransportActivity
            {
                Id = read.Id,
                DavItemId = read.DavItemId,
                FileName = read.FileName,
                Path = read.Path,
                StartedAt = read.StartedAt,
                LastActivityAt = read.LastActivityAt,
                BytesRead = Interlocked.Read(ref read.BytesRead),
                BytesFetched = Interlocked.Read(ref read.BytesFetched),
                SourceOffset = Interlocked.Read(ref read.CurrentOffset),
                FileSize = read.FileSize,
                ClientIp = read.ClientIp,
                ClientUserAgent = read.ClientUserAgent,
                PlayerSession = read.PlayerSession,
                CorrelationScope = scope,
                MatchingPlaybackSessionCount = matches.Count,
                Shared = shared,
                Providers = (usage.GetValueOrDefault(read.Id) ?? new Dictionary<string, long>())
                    .Select(pair =>
                    {
                        displayByMetricsKey.TryGetValue(pair.Key, out var display);
                        return new CurrentProviderContribution
                        {
                            Host = display.Host ?? pair.Key,
                            Nickname = display.Nickname,
                            Segments = pair.Value,
                        };
                    })
                    .OrderByDescending(provider => provider.Segments)
                    .ToList(),
            };
        }).ToList();

        var playbackRows = playback.Select(session =>
        {
            var matchingReads = transport
                .Where(read =>
                    read.CorrelationScope == TransportCorrelationScope.Session
                        ? read.PlayerSession == session.NativeSessionId
                          && session.SourceType == PlaybackSourceType.InfiniDysk
                        : read.DavItemId == session.DavItemId
                          && read.CorrelationScope == TransportCorrelationScope.File)
                .Select(read => read.Id)
                .ToList();

            return new CurrentPlaybackActivity
            {
                Session = session,
                TransportReadIds = matchingReads,
                HasSharedFileTransport = transport.Any(read =>
                    read.DavItemId == session.DavItemId
                    && read.CorrelationScope == TransportCorrelationScope.File
                    && read.Shared),
            };
        }).ToList();

        return new CurrentActivitySnapshot
        {
            Playback = playbackRows,
            Reads = transport,
            Authorities = playbackRegistry.AuthoritySnapshot(),
        };
    }
}
