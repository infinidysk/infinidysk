
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
}
