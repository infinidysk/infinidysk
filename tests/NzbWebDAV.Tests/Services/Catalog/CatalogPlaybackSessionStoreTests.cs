using NzbWebDAV.Services.Catalog;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services.Catalog;

/// <summary>
/// Session bookkeeping: the 32-session cap evicts least-recently-used entries and idle
/// sessions disappear after 24 hours, so a stale browser tab cannot keep media alive.
/// </summary>
public sealed class CatalogPlaybackSessionStoreTests
{
    [Fact]
    public void LeastRecentlyUsedSessionsAreEvictedAtTheCap()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new CatalogPlaybackSessionStore(clock);

        var ids = new List<Guid>();
        for (var index = 0; index < CatalogPlaybackSessionStore.MaxSessions + 1; index++)
        {
            ids.Add(AddSession(store, index));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(CatalogPlaybackSessionStore.MaxSessions, store.Count);
        Assert.False(store.Contains(ids[0]));
        Assert.True(store.Contains(ids[^1]));
        Assert.Equal(ids.Skip(1).Order(), ids.Where(store.Contains).Order());
    }

    [Fact]
    public void IdleSessionsExpireAfterTwentyFourHours()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new CatalogPlaybackSessionStore(clock);
        var id = AddSession(store, 0);

        clock.Advance(TimeSpan.FromHours(23));
        // A lookup refreshes activity, so the idle window restarts here.
        Assert.True(store.TryGet(id, out _));

        clock.Advance(TimeSpan.FromHours(25));
        Assert.False(store.TryGet(id, out _));
        Assert.False(store.Contains(id));
    }

    [Fact]
    public void PruneDropsExpiredSessionsWithoutTouchingActivity()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new CatalogPlaybackSessionStore(clock);
        var live = AddSession(store, 1);
        var stale = AddSession(store, 2);

        clock.Advance(TimeSpan.FromHours(20));
        Assert.True(store.TryGet(live, out _));

        store.Prune(DateTimeOffset.UnixEpoch.AddHours(25));

        Assert.True(store.Contains(live));
        Assert.False(store.Contains(stale));
    }

    [Theory]
    [InlineData(12.5, 3)]
    [InlineData(12, 2)]
    [InlineData(6, 1)]
    [InlineData(0.5, 1)]
    [InlineData(0, 0)]
    public void SegmentCountUsesSixSecondChunks(double seconds, int expected)
    {
        Assert.Equal(expected, CatalogHlsManifest.SegmentCount(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void SegmentCountFoldsContainerRoundingIntoTheLastSegment()
    {
        // Probe durations carry container rounding; a 100ns tail is not a playable segment.
        Assert.Equal(2, CatalogHlsManifest.SegmentCount(TimeSpan.FromSeconds(12.0000001)));
    }

    private static Guid AddSession(CatalogPlaybackSessionStore store, int index)
        => store.Add(
            $"http://localhost:3000/api/search/token/play/play-{index}.mkv",
            $"http://127.0.0.1:8080/api/search/token/play/play-{index}.mkv",
            "movie",
            "tt0063350",
            "Night.of.the.Living.Dead.1968.1080p.WEB-DL.x264",
            "").Id;
}
