using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Api.SabControllers.AddUrl;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Api;

/// <summary>
/// The addurl fetch deadline is shared by the header wait and the response-body
/// copy inside SubmitAsync, so a server that answers headers and then stalls
/// cannot ingest unbounded data past the deadline.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class AddUrlDeadlineTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-addurl-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private QueueManager _queueManager = null!;
    private ConfigManager _configManager = null!;
    private WebsocketManager _websocketManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);

        _configManager = new ConfigManager();
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiAddUrlTrustedHosts,
                ConfigValue = "127.0.0.1",
            },
        ]);

        _websocketManager = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = new QueueManager(
            usenet,
            _configManager,
            _websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            startLoop: false);
    }

    public async Task DisposeAsync()
    {
        _queueManager.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task AddUrl_SlowDripBody_HitsSharedFetchDeadline()
    {
        using var server = new SlowDripServer();
        var url = $"http://127.0.0.1:{server.Port}/slow.nzb";

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString =
            new QueryString($"?name={Uri.EscapeDataString(url)}&nzbname=slow.nzb");
        var controller = new AddUrlController(
            httpContext, _dbClient, _queueManager, _configManager, _websocketManager,
            new IndexerHitTracker());

        var request = await AddUrlRequest.New(
            httpContext, _configManager, new IndexerHitTracker(),
            fetchTimeout: TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => controller.AddUrlAsync(request));
        stopwatch.Stop();

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"deadline did not bound the fetch (elapsed {stopwatch.Elapsed})");
        Assert.False(await _context.QueueItems.AsNoTracking().AnyAsync());
        Assert.Empty(Directory.GetFiles(_configRoot, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Answers with valid NZB headers and a first body chunk, then stalls until
    /// disposed — the body never completes within the fetch deadline.
    /// </summary>
    private sealed class SlowDripServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _loop;

        public SlowDripServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(ServeAsync);
        }

        public int Port { get; }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                await using var stream = client.GetStream();
                var headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/x-nzb\r\n" +
                    "Content-Disposition: attachment; filename=\"slow.nzb\"\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(headers, _shutdown.Token);
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes("<nzb><file subject=\"slow\">"), _shutdown.Token);
                await stream.FlushAsync(_shutdown.Token);
                await Task.Delay(Timeout.Infinite, _shutdown.Token);
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
                // test teardown
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* teardown */ }
            _shutdown.Dispose();
        }
    }
}
