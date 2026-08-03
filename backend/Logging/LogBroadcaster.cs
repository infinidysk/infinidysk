using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Logging;

/// <summary>
/// Drains the log sink's channel and forwards new entries over the websocket.
/// Coalesces entries arriving within a 100ms window into a single frame so a
/// burst of logs doesn't fan out into hundreds of tiny socket sends.
/// </summary>
public sealed class LogBroadcaster(
    LogBufferSink sink,
    WebsocketManager websocketManager
) : BackgroundService
{
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const int MaxEntriesPerFrame = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new List<LogEntry>(MaxEntriesPerFrame);
        var reader = sink.StreamReader;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false)) return;

                while (reader.TryRead(out var entry))
                {
                    pending.Add(entry);
                    if (pending.Count >= MaxEntriesPerFrame) break;
                }

                if (pending.Count < MaxEntriesPerFrame)
                {
                    using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    window.CancelAfter(BatchWindow);
                    try
                    {
                        while (pending.Count < MaxEntriesPerFrame &&
                               await reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                        {
                            while (pending.Count < MaxEntriesPerFrame && reader.TryRead(out var more))
                                pending.Add(more);
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // batch window elapsed — flush what we have
                    }
                }

                if (websocketManager.HasSubscribers(WebsocketTopic.LogEntryAdded))
                {
                    var payload = JsonSerializer.Serialize(new { entries = pending }, JsonOptions);
                    await websocketManager.SendMessage(WebsocketTopic.LogEntryAdded, payload).ConfigureAwait(false);
                }
                pending.Clear();
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered() || stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Never let a broadcaster failure poison the logger itself —
                // we deliberately don't call Log.* here.
                pending.Clear();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
