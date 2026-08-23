using NzbWebDAV.Config;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class ArrMonitoringAggregationTests
{
    [Fact]
    public void GroupResolutions_CollapsesIdenticalTitleAndAction()
    {
        var title = "Gintama.S01.1080p.NF.WEB-DL.DDP2.0.H.264-Kitsune";
        var resolutions = Enumerable.Repeat(
            ((string?)title, ArrConfig.QueueAction.Remove), 367);

        var groups = ArrMonitoringService.GroupResolutions(resolutions);

        Assert.Single(groups);
        Assert.Equal(title, groups[0].Key.Title);
        Assert.Equal(ArrConfig.QueueAction.Remove, groups[0].Key.Action);
        Assert.Equal(367, groups[0].Count);
    }

    [Fact]
    public void GroupResolutions_KeepsDistinctTitlesSeparate()
    {
        var resolutions = new (string? Title, ArrConfig.QueueAction Action)[]
        {
            ("Release-A", ArrConfig.QueueAction.Remove),
            ("Release-A", ArrConfig.QueueAction.Remove),
            ("Release-B", ArrConfig.QueueAction.Remove),
        };

        var groups = ArrMonitoringService.GroupResolutions(resolutions)
            .OrderBy(g => g.Key.Title)
            .ToList();

        Assert.Equal(2, groups.Count);
        Assert.Equal(("Release-A", ArrConfig.QueueAction.Remove), groups[0].Key);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(("Release-B", ArrConfig.QueueAction.Remove), groups[1].Key);
        Assert.Equal(1, groups[1].Count);
    }

    [Fact]
    public void GroupResolutions_KeepsDistinctActionsSeparate()
    {
        var resolutions = new (string? Title, ArrConfig.QueueAction Action)[]
        {
            ("Release-A", ArrConfig.QueueAction.Remove),
            ("Release-A", ArrConfig.QueueAction.RemoveAndBlocklist),
        };

        var groups = ArrMonitoringService.GroupResolutions(resolutions);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void GroupResolutions_MapsNullTitleToUntitled()
    {
        var groups = ArrMonitoringService.GroupResolutions(
        [
            (null, ArrConfig.QueueAction.Remove),
            (null, ArrConfig.QueueAction.Remove),
        ]);

        Assert.Single(groups);
        Assert.Equal("(untitled)", groups[0].Key.Title);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void LogResolutionSummary_EmitsOneWarningPerGroup()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var title = "Gintama.S01.1080p.NF.WEB-DL.DDP2.0.H.264-Kitsune";
            var resolutions = Enumerable.Repeat(
                ((string?)title, ArrConfig.QueueAction.Remove), 367);

            ArrMonitoringService.LogResolutionSummary(resolutions, "http://sonarr:8989");

            // Filter by template property — other tests (and parallel classes) may write
            // Warnings to the process-wide Serilog logger while this sink is installed.
            var warning = Assert.Single(
                sink.Events,
                e => e.Level == LogEventLevel.Warning
                     && e.Properties.ContainsKey("QueueItemTitle"));
            Assert.Equal(367, warning.Properties["Count"].LiteralValue());
            Assert.Equal(title, warning.Properties["QueueItemTitle"].LiteralValue());
            Assert.Equal("http://sonarr:8989", warning.Properties["Host"].LiteralValue());
            Assert.Equal(ArrConfig.QueueAction.Remove, warning.Properties["Action"].LiteralValue());
            Assert.DoesNotContain(
                sink.Events,
                e => e.Level == LogEventLevel.Debug && e.Properties.ContainsKey("QueueItemTitle"));
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    [Fact]
    public void MatchingStatusMessages_ReturnOriginalArrReason()
    {
        var record = new ArrQueueRecord
        {
            StatusMessages =
            [
                new ArrQueueStatusMessage
                {
                    Messages = ["Found archive file, might need to be extracted: release.part01.rar"],
                },
            ],
        };

        var reasons = record.GetMatchingStatusMessages(["Found archive file, might need to be extracted"]);

        Assert.Equal(
            ["Found archive file, might need to be extracted: release.part01.rar"],
            reasons);
    }

    [Fact]
    public void GetActionableStuckRecords_LeavesDownloadingRecordsAlone()
    {
        var queue = new ArrQueue<ArrQueueRecord>
        {
            Records =
            [
                new ArrQueueRecord
                {
                    Id = 1,
                    Status = "downloading",
                    StatusMessages =
                    [
                        new ArrQueueStatusMessage { Messages = ["Found archive file, might need to be extracted"] },
                    ],
                },
                new ArrQueueRecord
                {
                    Id = 2,
                    Status = "completed",
                    StatusMessages =
                    [
                        new ArrQueueStatusMessage { Messages = ["Found archive file, might need to be extracted"] },
                    ],
                },
            ],
        };

        var records = ArrMonitoringService.GetActionableStuckRecords(
            queue,
            [new ArrConfig.QueueRule
            {
                Message = "Found archive file, might need to be extracted",
                Action = ArrConfig.QueueAction.RemoveAndBlocklistAndSearch,
            }]);

        Assert.Equal([2], records.Select(x => x.Id));
    }

    [Fact]
    public void SummarizeReason_FlattensDeduplicatesAndTruncates()
    {
        var reason = ArrMonitoringService.SummarizeReason(
            [
                "No files found\nare eligible for import",
                "No files found\nare eligible for import",
                new string('x', 600),
            ],
            []);

        Assert.StartsWith("No files found are eligible for import; ", reason);
        Assert.EndsWith("…", reason);
        Assert.True(reason.Length <= 512);
    }

    [Fact]
    public void LogResolutionSummary_EmitsExactArrReason()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            ArrMonitoringService.LogResolutionSummary(
                [
                    (
                        "Release-A",
                        ArrConfig.QueueAction.RemoveAndBlocklistAndSearch,
                        "Found archive file, might need to be extracted: release.part01.rar",
                        "Arr media ID"
                    ),
                ],
                "http://radarr:7878");

            var warning = Assert.Single(
                sink.Events,
                e => e.Level == LogEventLevel.Warning && e.Properties.ContainsKey("Reason"));
            Assert.Equal(
                "Found archive file, might need to be extracted: release.part01.rar",
                warning.Properties["Reason"].LiteralValue());
            Assert.Equal("Arr media ID", warning.Properties["IdentitySource"].LiteralValue());
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToList();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }
}

file static class LogEventPropertyValueExtensions
{
    public static object? LiteralValue(this LogEventPropertyValue value) =>
        value is ScalarValue scalar ? scalar.Value : value.ToString();
}
