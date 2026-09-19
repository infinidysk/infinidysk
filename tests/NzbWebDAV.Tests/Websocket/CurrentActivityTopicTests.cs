using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Websocket;

public class CurrentActivityTopicTests
{
    [Fact]
    public void CurrentActivity_IsIndependentReplayableStateTopic()
    {
        Assert.True(WebsocketTopic.TryGetByName("ca", out var topic));
        Assert.Same(WebsocketTopic.CurrentActivity, topic);
        Assert.Equal(WebsocketTopic.TopicType.State, topic!.Type);
        Assert.NotSame(WebsocketTopic.ActiveReads, topic);
    }
}
