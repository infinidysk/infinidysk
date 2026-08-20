using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Tests.Clients;

public class ArrHistoryJsonTests
{
    [Theory]
    [InlineData("""{"records":[{"eventType":"downloadFolderImported"}]}""", 3)]
    [InlineData("""{"records":[{"eventType":3}]}""", 3)]
    [InlineData("""{"records":[{"eventType":"3"}]}""", 3)]
    [InlineData("""{"records":[{"eventType":"futureEventType"}]}""", 0)]
    public void Deserialize_AcceptsStringNumericAndUnknownEventTypes(string json, int expectedEventType)
    {
        var history = JsonSerializer.Deserialize<ArrHistory>(json);

        Assert.Equal(expectedEventType, Assert.Single(history!.Records).EventType);
    }
}
