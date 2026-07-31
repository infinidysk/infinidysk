using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class HealthCheckRejectedReleaseSeedTests
{
    private static List<string> NewSegmentIds(int count) =>
        Enumerable.Range(0, count).Select(_ => $"<{Guid.NewGuid():N}@test>").ToList();

    [Fact]
    public void SmallRelease_SeedsAllSegments()
    {
        var segments = NewSegmentIds(HealthCheckService.RejectedReleaseSeedSegments - 1);

        var seed = HealthCheckService.SelectRejectedReleaseSeedSegments(segments);

        Assert.Equal(segments, seed);
    }

    [Fact]
    public void LargeRelease_SeedIsBoundedToPrefix()
    {
        var segments = NewSegmentIds(HealthCheckService.RejectedReleaseSeedSegments * 3);

        var seed = HealthCheckService.SelectRejectedReleaseSeedSegments(segments);

        Assert.Equal(HealthCheckService.RejectedReleaseSeedSegments, seed.Count);
        Assert.Equal(segments.Take(HealthCheckService.RejectedReleaseSeedSegments), seed);
    }

    [Fact]
    public void SeededSegments_FailTheQueuePrecheck()
    {
        // A re-grab of a rejected release carries identical message-ids, so any overlap
        // between the seed and the incoming NZB must throw at the step-0 precheck.
        var segments = NewSegmentIds(HealthCheckService.RejectedReleaseSeedSegments + 50);

        HealthCheckService.AddMissingSegmentIds(
            HealthCheckService.SelectRejectedReleaseSeedSegments(segments));

        var ex = Assert.Throws<UsenetArticleNotFoundException>(
            () => HealthCheckService.CheckCachedMissingSegmentIds(segments));
        Assert.Equal(segments[0], ex.SegmentId);

        // Segments beyond the bounded prefix are not seeded — the cache stays bounded.
        HealthCheckService.CheckCachedMissingSegmentIds(
            segments.Skip(HealthCheckService.RejectedReleaseSeedSegments));
    }
}
