using System.Text.Json;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests.Queue;

public class QueueItemAddedPayloadTests
{
    [Fact]
    public void ToJson_MatchesSabQueueSlotForANewItem()
    {
        var item = new QueueItem
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            FileName = "release.nzb",
            JobName = "release",
            Category = "movies",
            TotalSegmentBytes = 2 * 1024 * 1024,
        };

        using var slotJson = JsonDocument.Parse(GetQueueResponse.QueueSlot.FromQueueItem(item).ToJson());
        using var payloadJson = JsonDocument.Parse(QueueItemAddedPayload.FromQueueItem(item).ToJson());

        Assert.Equal(slotJson.RootElement.GetRawText(), payloadJson.RootElement.GetRawText());
    }
}
