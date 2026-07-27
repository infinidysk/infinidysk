using System.Text.Json;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services.StreamTrace;

/// <summary>
/// Publishes the current stream-tracing status on the <c>strt</c> websocket topic.
/// Shared by the set-stream-tracing API and the expiry sweeper so late subscribers
/// always see a consistent last-known state.
/// </summary>
public sealed class StreamTraceStatusBroadcaster(WebsocketManager websocketManager)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private string? _lastPayload;

    public Task BroadcastAsync(StreamTraceStatus status) =>
        BroadcastAsync(ToPayload(status));

    public async Task BroadcastAsync(string payload)
    {
        lock (_gate)
        {
            if (payload == _lastPayload) return;
            _lastPayload = payload;
        }

        await websocketManager.SendMessage(WebsocketTopic.StreamTracing, payload).ConfigureAwait(false);
    }

    public static string ToPayload(StreamTraceStatus status) =>
        JsonSerializer.Serialize(new
        {
            enabled = status.Enabled,
            source = status.Source,
            expiresAtUnixMs = status.ExpiresAtUnixMs,
            capacity = status.Capacity,
            eventCount = status.EventCount,
            sessionCount = status.SessionCount,
        }, JsonOptions);
}
