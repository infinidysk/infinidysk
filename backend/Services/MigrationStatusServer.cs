using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// A minimal HTTP server that runs for the duration of the blocking
/// <c>--db-migration</c> phase. It binds the same port the real backend will
/// later use (via <c>ASPNETCORE_URLS</c>) and serves migration progress at
/// <c>/api/migration-status</c> so the frontend can render a live status page.
/// Every other route (including <c>/health</c>) returns 503 so the entrypoint
/// health poll and frontend loaders keep retrying until the real backend is up.
/// </summary>
public sealed class MigrationStatusServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MigrationStatusServer(WebApplication app) => _app = app;

    public static async Task<MigrationStatusServer?> StartAsync(MigrationProgress progress, CancellationToken ct)
    {
        try
        {
            var builder = WebApplication.CreateBuilder();
            // Keep this process quiet; migration progress is logged via Serilog.
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.None);

            var app = builder.Build();
            app.UseWebSockets();

            app.MapGet("/api/migration-status", (HttpContext context) =>
            {
                context.Response.ContentType = "application/json";
                return context.Response.WriteAsync(progress.ToJson());
            });

            // Hold frontend relay websockets open during migration so the relay
            // does not spam ECONNREFUSED / 503 warnings. On shutdown we close
            // with 1012 so the relay reconnects to the real backend.
            app.Map("/ws", HandleMigrationWebSocket);

            app.MapFallback((HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                return context.Response.WriteAsync("{\"status\":\"migrating\"}");
            });

            await app.StartAsync(ct).ConfigureAwait(false);
            return new MigrationStatusServer(app);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A missing status page must never block the actual migration.
            Log.Warning(ex, "Could not start migration status server; migration progress UI is unavailable");
            return null;
        }
    }

    private static async Task HandleMigrationWebSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var buffer = new byte[1024];

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket
                    .ReceiveAsync(buffer, context.RequestAborted)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                // Ignore all frames (including the frontend API-key frame).
            }
        }
        catch (OperationCanceledException)
        {
            // Status server is shutting down or the client disconnected.
        }
        catch (WebSocketException)
        {
            // Client went away; nothing to do.
        }

        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                // 1012 = Service Restart (not in WebSocketCloseStatus enum).
                // Matches the frontend relay's reconnect path for backend handoff.
                await webSocket
                    .CloseAsync(
                        (WebSocketCloseStatus)1012,
                        "Migration complete",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close during shutdown.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "Error stopping migration status server");
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
