using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class HealthCheckMissingSegmentCacheTests
{
    [Fact]
    public void AddMissingSegmentIds_IsVisibleToCheckCachedMissingSegmentIds()
    {
        var missingId = $"<{Guid.NewGuid():N}@test>";
        var otherId = $"<{Guid.NewGuid():N}@test>";

        HealthCheckService.AddMissingSegmentIds([missingId], 0);

        var ex = Assert.Throws<UsenetArticleNotFoundException>(
            () => HealthCheckService.CheckCachedMissingSegmentIds([missingId]));
        Assert.Equal(missingId, ex.SegmentId);

        // Unrelated ids must not throw — the cache is a shared static with no reset API.
        HealthCheckService.CheckCachedMissingSegmentIds([otherId]);
    }
}
