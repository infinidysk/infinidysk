using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ActiveReadRegistryEnrichmentTests
{
    [Fact]
    public void GetOrCreate_StoresClientMetadata()
    {
        var registry = new ActiveReadRegistry();
        var id = registry.GetOrCreate(
            "/view/movie.mkv",
            "127.0.0.1|VLC",
            "movie.mkv",
            1024,
            "VLC/3.0",
            "127.0.0.1");

        var snap = registry.Snapshot().Single(e => e.Id == id);
        Assert.Equal("VLC/3.0", snap.ClientUserAgent);
        Assert.Equal("127.0.0.1", snap.ClientIp);
        Assert.Equal(ReadSession.EndReasonCode.Completed, snap.EndReason);
    }

    [Fact]
    public void SetEndReason_And_AddBytesFetched_UpdateEntry()
    {
        var registry = new ActiveReadRegistry();
        var id = registry.GetOrCreate("/p", "k", "f", 100);
        registry.AddBytesFetched(id, 40);
        registry.AddBytesFetched(id, 10);
        registry.Touch(id, 25, 25);
        registry.SetEndReason(id, ReadSession.EndReasonCode.Aborted);

        var entry = registry.Snapshot().Single(e => e.Id == id);
        Assert.Equal(50, Interlocked.Read(ref entry.BytesFetched));
        Assert.Equal(25, Interlocked.Read(ref entry.BytesRead));
        Assert.Equal(ReadSession.EndReasonCode.Aborted, entry.EndReason);
        Assert.Equal(25, registry.GetBytesRead(id));
    }

    [Fact]
    public void GetOrCreate_SamePlayerSession_DedupesOntoOneSession()
    {
        var registry = new ActiveReadRegistry();
        var first = registry.GetOrCreate("/p", "k", "f", 100, playerSession: "abc123");
        var second = registry.GetOrCreate("/p", "k", "f", 100, playerSession: "abc123");

        Assert.Equal(first, second);
        Assert.Single(registry.Snapshot());
        Assert.Equal("abc123", registry.Snapshot().Single().PlayerSession);
    }

    [Fact]
    public void GetOrCreate_DifferentPlayerSessions_StayDistinct()
    {
        var registry = new ActiveReadRegistry();
        var first = registry.GetOrCreate("/p", "k", "f", 100, playerSession: "player-a");
        var second = registry.GetOrCreate("/p", "k", "f", 100, playerSession: "player-b");

        Assert.NotEqual(first, second);
        Assert.Equal(2, registry.Snapshot().Count);
    }

    [Fact]
    public void GetOrCreate_MissingPlayerSession_KeepsLegacyDedupe()
    {
        var registry = new ActiveReadRegistry();
        var first = registry.GetOrCreate("/p", "k", "f", 100);
        var second = registry.GetOrCreate("/p", "k", "f", 100);

        Assert.Equal(first, second);
        Assert.Null(registry.Snapshot().Single().PlayerSession);

        // A keyed player does not merge into an existing keyless session.
        var keyed = registry.GetOrCreate("/p", "k", "f", 100, playerSession: "player-a");
        Assert.NotEqual(first, keyed);
    }

    [Fact]
    public void PruneExpired_RemovesPlayerSessionDedupeMapping()
    {
        var registry = new ActiveReadRegistry();
        var first = registry.GetOrCreate("/p", "k", "f", 100, playerSession: "player-a");

        // Force expiry by pruning with the entry untouched beyond the window:
        // there is no clock injection, so verify via a fresh registry instance
        // that a second GetOrCreate after PruneExpired re-creates rather than
        // resurrecting the expired mapping.
        var expired = registry.PruneExpired(DateTimeOffset.UtcNow.AddSeconds(31));
        Assert.Single(expired);

        var recreated = registry.GetOrCreate("/p", "k", "f", 100, playerSession: "player-a");
        Assert.NotEqual(first, recreated);
    }
}
