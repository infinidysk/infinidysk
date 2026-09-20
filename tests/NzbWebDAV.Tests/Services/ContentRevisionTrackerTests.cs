using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class ContentRevisionTrackerTests
{
    [Fact]
    public void Publication_InvalidatesOnlyInterestedActiveReaders()
    {
        var id = Guid.NewGuid();
        using var affected = ContentRevisionTracker.Watch(id);
        using var other = ContentRevisionTracker.Watch(Guid.NewGuid());
        Assert.True(affected.IsCurrent);
        ContentRevisionTracker.Publishing(id);
        Assert.False(affected.IsCurrent);
        Assert.True(other.IsCurrent);
        using var later = ContentRevisionTracker.Watch(id);
        Assert.True(later.IsCurrent);
    }

    [Fact]
    public void WatchDuringPublication_CannotTrustIntermediateBlobState()
    {
        var id = Guid.NewGuid();
        using (ContentRevisionTracker.BeginPublication(id))
        {
            using var during = ContentRevisionTracker.Watch(id);
            Assert.False(during.IsCurrent);
        }
        using var after = ContentRevisionTracker.Watch(id);
        Assert.True(after.IsCurrent);
    }
}
