namespace NzbWebDAV.Websocket;

/// <summary>
/// Publish-only websocket surface. Connection and session management stay on
/// <see cref="WebsocketManager"/>.
/// </summary>
public interface IWebsocketPublisher
{
    bool HasSubscribers(WebsocketTopic topic);
    Task SendMessage(WebsocketTopic topic, string message);
}
