using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ActiveReadDavItemIdentityTests
{
    [Fact]
    public void ExactDavItemIdentity_IsStoredAndCanBeEnrichedLater()
    {
        var registry = new ActiveReadRegistry();
        var initialId = Guid.NewGuid();
        var session = registry.GetOrCreate(
            "/.ids/a/file", "client", "file.mkv", 100, davItemId: initialId);

        Assert.Equal(initialId, registry.Snapshot().Single().DavItemId);

        var resolvedId = Guid.NewGuid();
        registry.UpdateInfo(session, "friendly.mkv", 200, resolvedId);
        var entry = registry.Snapshot().Single();
        Assert.Equal(resolvedId, entry.DavItemId);
        Assert.Equal("friendly.mkv", entry.FileName);
        Assert.Equal(200, entry.FileSize);
    }

    [Fact]
    public void Snapshot_IsDetachedFromLaterIdentityEnrichment()
    {
        var registry = new ActiveReadRegistry();
        var initialId = Guid.NewGuid();
        var session = registry.GetOrCreate(
            "/.ids/a/file", "client", "file.mkv", 100,
            playerSession: "player-1", davItemId: initialId);
        var before = registry.Snapshot().Single();

        var resolvedId = Guid.NewGuid();
        registry.UpdateInfo(session, "friendly.mkv", 200, resolvedId);
        registry.Touch(session, 25, currentOffset: 50);

        Assert.Equal(initialId, before.DavItemId);
        Assert.Equal("file.mkv", before.FileName);
        Assert.Equal(100, before.FileSize);
        Assert.Equal(0, before.BytesRead);
        Assert.Equal(0, before.CurrentOffset);

        var after = registry.Snapshot().Single();
        Assert.Equal(resolvedId, after.DavItemId);
        Assert.Equal("friendly.mkv", after.FileName);
        Assert.Equal(200, after.FileSize);
        Assert.Equal(25, after.BytesRead);
        Assert.Equal(50, after.CurrentOffset);
    }

    [Fact]
    public async Task ConcurrentIdentityUpdates_PublishConsistentSnapshots()
    {
        var registry = new ActiveReadRegistry();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var session = registry.GetOrCreate(
            "/.ids/a/file", "client", "first.mkv", 100,
            playerSession: "player-1", davItemId: firstId);

        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 10_000; i++)
            {
                if ((i & 1) == 0)
                    registry.UpdateInfo(session, "first.mkv", 100, firstId);
                else
                    registry.UpdateInfo(session, "second.mkv", 200, secondId);
            }
        });

        start.Set();
        for (var i = 0; i < 10_000; i++)
        {
            var entry = registry.Snapshot().Single();
            var isFirst = entry.FileName == "first.mkv"
                          && entry.FileSize == 100
                          && entry.DavItemId == firstId;
            var isSecond = entry.FileName == "second.mkv"
                           && entry.FileSize == 200
                           && entry.DavItemId == secondId;
            Assert.True(isFirst || isSecond);

            Assert.True(registry.TryResolveDavItemIdForPlayerSession("player-1", out var resolved));
            Assert.True(resolved == firstId || resolved == secondId);
        }

        await writer;
    }

    [Fact]
    public void PlayerSessionIdentity_FailsClosedWhenAssociationIsAmbiguous()
    {
        var registry = new ActiveReadRegistry();
        var first = Guid.NewGuid();
        registry.GetOrCreate(
            "/.ids/one", "client", "one.mkv", 100,
            playerSession: "player-1", davItemId: first);

        Assert.True(registry.TryResolveDavItemIdForPlayerSession("player-1", out var resolved));
        Assert.Equal(first, resolved);

        registry.GetOrCreate(
            "/.ids/two", "client", "two.mkv", 100,
            playerSession: "player-1", davItemId: Guid.NewGuid());

        Assert.False(registry.TryResolveDavItemIdForPlayerSession("player-1", out _));
    }
}
