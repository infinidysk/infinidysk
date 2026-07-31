using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class HealthCheckRepairRateLimitTests
{
    // The tracker is a shared static with no reset API, so every test uses a unique path.
    private static string NewPath() => $"/media/tv/Show/Season 01/{Guid.NewGuid():N}.mkv";

    [Fact]
    public void UnknownPath_IsNotRateLimited()
    {
        Assert.False(HealthCheckService.IsRepairRateLimited(NewPath(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RemovalsBelowLimit_AreNotRateLimited()
    {
        var path = NewPath();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < HealthCheckService.RepairRecurrenceLimit - 1; i++)
            HealthCheckService.RecordRepairRemoval(path, now);

        Assert.False(HealthCheckService.IsRepairRateLimited(path, now));
    }

    [Fact]
    public void RemovalsAtLimit_AreRateLimited()
    {
        var path = NewPath();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < HealthCheckService.RepairRecurrenceLimit; i++)
            HealthCheckService.RecordRepairRemoval(path, now);

        Assert.True(HealthCheckService.IsRepairRateLimited(path, now));
    }

    [Fact]
    public void RemovalsOutsideWindow_AreForgotten()
    {
        var path = NewPath();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < HealthCheckService.RepairRecurrenceLimit; i++)
            HealthCheckService.RecordRepairRemoval(path, start);

        Assert.True(HealthCheckService.IsRepairRateLimited(path, start));
        Assert.False(HealthCheckService.IsRepairRateLimited(
            path, start + HealthCheckService.RepairRecurrenceWindow));
    }

    [Fact]
    public void RateLimitIsPerPath()
    {
        var limitedPath = NewPath();
        var otherPath = NewPath();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < HealthCheckService.RepairRecurrenceLimit; i++)
            HealthCheckService.RecordRepairRemoval(limitedPath, now);

        Assert.True(HealthCheckService.IsRepairRateLimited(limitedPath, now));
        Assert.False(HealthCheckService.IsRepairRateLimited(otherPath, now));
    }
}
