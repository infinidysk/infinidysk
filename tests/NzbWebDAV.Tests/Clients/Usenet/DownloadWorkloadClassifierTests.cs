using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class DownloadWorkloadClassifierTests
{
    [Fact]
    public void ClassifyForMetrics_WithoutContext_IsBackground()
    {
        using var source = new CancellationTokenSource();

        var result = DownloadWorkloadClassifier.ClassifyForMetrics(source.Token);

        Assert.Equal(SegmentFetch.FetchWorkload.Background, result);
    }

    [Fact]
    public void ClassifyForMetrics_HighPriority_IsStreaming()
    {
        using var source = new CancellationTokenSource();
        using var scope = source.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
        });

        var result = DownloadWorkloadClassifier.ClassifyForMetrics(source.Token);

        Assert.Equal(SegmentFetch.FetchWorkload.Streaming, result);
    }

    [Fact]
    public void ClassifyForMetrics_QueueContext_IsQueue()
    {
        using var source = new CancellationTokenSource();
        using var scope = source.Token.SetContext(CreateQueueContext());

        var result = DownloadWorkloadClassifier.ClassifyForMetrics(source.Token);

        Assert.Equal(SegmentFetch.FetchWorkload.Queue, result);
    }

    [Fact]
    public void ClassifyForMetrics_MaintenanceContext_IsMaintenance()
    {
        using var source = new CancellationTokenSource();
        using var scope = source.Token.SetContext(MaintenanceDownloadContext.Instance);

        var result = DownloadWorkloadClassifier.ClassifyForMetrics(source.Token);

        Assert.Equal(SegmentFetch.FetchWorkload.Maintenance, result);
    }

    [Fact]
    public void ClassifyForMetrics_QueueContextWinsOverHighPriority()
    {
        using var source = new CancellationTokenSource();
        using var priorityScope = source.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
        });
        using var queueScope = source.Token.SetContext(CreateQueueContext());

        var result = DownloadWorkloadClassifier.ClassifyForMetrics(source.Token);

        Assert.Equal(SegmentFetch.FetchWorkload.Queue, result);
    }

    [Fact]
    public void ClassifyForMetrics_MaintenanceContextWinsOverHighPriority()
    {
        using var source = new CancellationTokenSource();
        using var priorityScope = source.Token.SetContext(new DownloadPriorityContext
        {
            Priority = SemaphorePriority.High,
        });
        using var maintenanceScope = source.Token.SetContext(MaintenanceDownloadContext.Instance);

        var result = DownloadWorkloadClassifier.ClassifyForMetrics(source.Token);

        Assert.Equal(SegmentFetch.FetchWorkload.Maintenance, result);
    }

    private static QueueDownloadContext CreateQueueContext() => new()
    {
        IsPrimary = true,
        GetFanOutConcurrency = () => 1,
    };
}