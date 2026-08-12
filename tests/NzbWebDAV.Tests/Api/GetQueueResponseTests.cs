using System.Globalization;
using System.Text.Json;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api;

public class GetQueueResponseTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(50, "50")]
    [InlineData(100, "100")]
    [InlineData(150, "100")]
    [InlineData(200, "100")]
    public void FromQueueItem_ClampsSabPercentage(int progress, string expected)
    {
        var slot = GetQueueResponse.QueueSlot.FromQueueItem(
            CreateQueueItem(),
            progressPercentage: progress);

        Assert.Equal(expected, slot.Percentage);
        Assert.Equal(progress.ToString(CultureInfo.InvariantCulture), slot.TruePercentage);
    }

    [Fact]
    public void FromQueueItem_ReportsNoBytesLeftAfterDownloadPhase()
    {
        var slot = GetQueueResponse.QueueSlot.FromQueueItem(
            CreateQueueItem(),
            progressPercentage: 150);

        Assert.Equal("0.00", slot.SizeLeftInMB);
    }

    [Fact]
    public void FromQueueItem_UsesEtaWhenProvided()
    {
        var slot = GetQueueResponse.QueueSlot.FromQueueItem(
            CreateQueueItem(),
            progressPercentage: 40,
            status: "Downloading",
            eta: TimeSpan.FromMinutes(16) + TimeSpan.FromSeconds(44));

        Assert.Equal(TimeSpan.FromMinutes(16) + TimeSpan.FromSeconds(44), slot.TimeLeft);
    }

    [Fact]
    public void FromQueueItem_TimeLeftIsZeroWhenEtaUnknown()
    {
        var slot = GetQueueResponse.QueueSlot.FromQueueItem(CreateQueueItem());
        Assert.Equal(TimeSpan.Zero, slot.TimeLeft);
    }

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(16 * 60 + 44, "0:16:44")]
    [InlineData(25 * 3600, "1:01:00:00")]
    public void TimeConverter_WritesSabFormat(int totalSeconds, string expected)
    {
        Assert.Equal(expected, GetQueueResponse.SabnzbdQueueTimeConverter.Format(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Fact]
    public void QueueObject_SerializesSpeedAndTimeleft()
    {
        var response = new GetQueueResponse
        {
            Queue = new GetQueueResponse.QueueObject
            {
                Speed = "1.3 M",
                KbPerSec = "1296.02",
                TimeLeft = TimeSpan.FromMinutes(16) + TimeSpan.FromSeconds(44),
            },
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        var queue = json.RootElement.GetProperty("queue");
        Assert.Equal("1.3 M", queue.GetProperty("speed").GetString());
        Assert.Equal("1296.02", queue.GetProperty("kbpersec").GetString());
        Assert.Equal("0:16:44", queue.GetProperty("timeleft").GetString());
    }

    [Fact]
    public void QueueSlot_SerializesZeroTimeleftAsSabZero()
    {
        var slot = GetQueueResponse.QueueSlot.FromQueueItem(CreateQueueItem());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(slot));
        Assert.Equal("0:00:00", json.RootElement.GetProperty("timeleft").GetString());
    }

    private static QueueItem CreateQueueItem() =>
        new()
        {
            Id = Guid.NewGuid(),
            FileName = "release.nzb",
            JobName = "release",
            Category = "movies",
            TotalSegmentBytes = 2 * 1024 * 1024,
        };
}
