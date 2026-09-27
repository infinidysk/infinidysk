using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class ReadActivityWarmingTests
{
    [Fact]
    public void Warming_RequiresSustainedSequentialBytes_AndResetsAfterSeekOrIdle()
    {
        var registry = new ActiveReadRegistry();
        var id = registry.GetOrCreate("/movie", "client", "movie", 1024L * 1024 * 1024);
        var entry = Assert.Single(registry.Snapshot());
        var now = DateTimeOffset.UtcNow;
        const int block = 4 * 1024 * 1024;
        long offset = 0;
        for (var i = 0; i < 16; i++)
        {
            offset += block;
            registry.Touch(id, block, offset, now.AddSeconds(i * 2));
            Assert.Equal(i == 15, entry.QualifiesForWarming(now.AddSeconds(i * 2)));
        }
        registry.Touch(id, block, block, now.AddSeconds(32));
        Assert.False(entry.QualifiesForWarming(now.AddSeconds(32)));
        registry.Touch(id, block, 2 * block, now.AddSeconds(60));
        Assert.False(entry.QualifiesForWarming(now.AddSeconds(60)));
    }

    [Fact]
    public void FastScansAndRepeatedPreviews_DoNotQualify()
    {
        var registry = new ActiveReadRegistry();
        var id = registry.GetOrCreate("/movie", "client", "movie", 1024L * 1024 * 1024);
        var entry = Assert.Single(registry.Snapshot());
        var now = DateTimeOffset.UtcNow;
        const int block = 4 * 1024 * 1024;
        registry.Touch(id, 128L * 1024 * 1024, 128L * 1024 * 1024, now);
        Assert.False(entry.QualifiesForWarming(now));
        for (var i = 0; i < 100; i++) registry.Touch(id, block, block, now.AddSeconds(i));
        Assert.False(entry.QualifiesForWarming(now.AddSeconds(99)));
    }
}
