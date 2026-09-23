using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.SearchFileInArr;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class SearchFileInArrControllerTests : IAsyncLifetime
{
    private readonly NzbDavWebApplicationFactory _factory = new();
    private readonly string _root = Path.Join(Path.GetTempPath(), $"files-arr-{Guid.NewGuid():N}");
    private IServiceScope _scope = null!;
    private ConfigManager _config = null!;
    private FilesLibraryIndex _index = null!;
    private DavItem _file = null!;
    private readonly List<string> _requests = [];
    private readonly List<HttpClient> _clients = [];
    private Func<HttpRequestMessage, Task<HttpResponseMessage>> _send = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1}") });
    private Func<string, Task<List<ArrRootFolder>>> _roots = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _scope = _factory.Services.CreateScope();
        _config = _scope.ServiceProvider.GetRequiredService<ConfigManager>();
        _config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _root },
            new ConfigItem { ConfigName = ConfigKeys.ArrInstances, ConfigValue = JsonSerializer.Serialize(new ArrConfig
            {
                RadarrInstances = [new() { Host = "http://first.test", ApiKey = "synthetic", Name = "First" }, new() { Host = "http://second.test", ApiKey = "synthetic", Name = "Second" }],
            }) },
        ]);
        _file = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "movie.mkv", 100, DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile, null, null, null, null);
        await _factory.AddDavItemsAsync(_file);
        await File.WriteAllTextAsync(Path.Join(_root, "movie.strm"), $"http://localhost/view/.ids/{_file.Id}.mkv");
        _index = new FilesLibraryIndex(_config, new ControllableTimeProvider());
        await _index.RefreshAsync(CancellationToken.None);
        _roots = _ => Task.FromResult(new List<ArrRootFolder> { new() { Path = _root } });
    }

    public async Task DisposeAsync()
    {
        _index.Dispose();
        foreach (var client in _clients) client.Dispose();
        _scope.Dispose();
        await _factory.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private SearchFileInArrController Controller(Guid? id = null)
    {
        var http = new DefaultHttpContext { RequestServices = _scope.ServiceProvider };
        http.Request.Headers["x-api-key"] = NzbDavWebApplicationFactory.ApiKey;
        http.Request.ContentType = "multipart/form-data; boundary=synthetic";
        http.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["davItemId"] = (id ?? _file.Id).ToString() });
        return new SearchFileInArrController(_scope.ServiceProvider.GetRequiredService<DavDatabaseClient>(), _config, _index)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            ClientFactory = (_, details) =>
            {
                var client = new HttpClient(new Handler(async request =>
                {
                    _requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("/api/v3/command", request.RequestUri.AbsolutePath);
                    var body = await request.Content!.ReadAsStringAsync();
                    Assert.Contains("MoviesSearch", body, StringComparison.Ordinal);
                    return await _send(request);
                }));
                _clients.Add(client);
                return new ScriptedClient(details.Host, client, () => _roots(details.Host));
            },
        };
    }

    [Fact]
    public async Task Search_RequiresLiveCanonicalFile()
    {
        Assert.IsType<NotFoundObjectResult>(await Controller(Guid.NewGuid()).HandlePostApiRequest());
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Search_RejectsUnlinkedOrUnconfiguredFileWithoutHttp()
    {
        File.Delete(Path.Join(_root, "movie.strm"));
        await _index.RefreshAsync(CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(await Controller().HandlePostApiRequest());
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Search_UnreachablePeerAbortsAllCommands()
    {
        _roots = host => host.Contains("second", StringComparison.Ordinal) ? throw new HttpRequestException("synthetic secret")
            : Task.FromResult(new List<ArrRootFolder> { new() { Path = _root } });
        var result = Assert.IsType<ObjectResult>(await Controller().HandlePostApiRequest());
        Assert.Equal(502, result.StatusCode);
        Assert.Empty(_requests);
        Assert.DoesNotContain("synthetic secret", Assert.IsType<BaseApiResponse>(result.Value).Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_MalformedRootAbortsAllCommands()
    {
        _roots = _ => Task.FromResult(new List<ArrRootFolder> { null! });
        var result = Assert.IsType<ObjectResult>(await Controller().HandlePostApiRequest());
        Assert.Equal(502, result.StatusCode);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Search_KnownFailureLogsWarningWithoutSecretsOrStack()
    {
        var previous = Serilog.Log.Logger;
        var sink = new CollectingLogEventSink();
        using var logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Serilog.Log.Logger = logger;
        try
        {
            _roots = _ => throw new HttpRequestException("synthetic-secret-in-url");
            Assert.Equal(502, Assert.IsType<ObjectResult>(await Controller().HandlePostApiRequest()).StatusCode);
            var entry = Assert.Single(sink.Events, entry => entry.MessageTemplate.Text == "Files Arr search preflight failed for {DavItemId}. Reason: {Reason}");
            Assert.Equal(Serilog.Events.LogEventLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("synthetic-secret", entry.RenderMessage(), StringComparison.Ordinal);
        }
        finally { Serilog.Log.Logger = previous; }
    }

    [Fact]
    public async Task Search_DeduplicatesMultipleLinksAndSupportsMultipleOwners()
    {
        await File.WriteAllTextAsync(Path.Join(_root, "duplicate.strm"), $"http://localhost/view/.ids/{_file.Id}.mkv");
        await _index.RefreshAsync(CancellationToken.None);
        var result = Assert.IsType<SearchFileInArrResponse>(Assert.IsType<OkObjectResult>(await Controller().HandlePostApiRequest()).Value);
        Assert.Equal("requested", result.Outcome);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task Search_RevalidatesChangedLinkAndDisabledInstance()
    {
        _roots = _ =>
        {
            File.Delete(Path.Join(_root, "movie.strm"));
            return Task.FromResult(new List<ArrRootFolder> { new() { Path = _root } });
        };
        Assert.IsType<ConflictObjectResult>(await Controller().HandlePostApiRequest());
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Search_ConcurrentDuplicateReturnsConflict()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _roots = async _ => { started.TrySetResult(); await release.Task; return [new() { Path = _root }]; };
        var first = Controller().HandlePostApiRequest();
        await started.Task;
        Assert.IsType<ConflictObjectResult>(await Controller().HandlePostApiRequest());
        release.SetResult();
        Assert.IsType<OkObjectResult>(await first);
        Assert.IsType<OkObjectResult>(await Controller().HandlePostApiRequest());
    }

    [Fact]
    public async Task Search_PartialAndUnconfirmedResultsAreNotRetried()
    {
        _send = request => request.RequestUri!.Host == "first.test" ? throw new HttpRequestException("lost receipt")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":2}", Encoding.UTF8, "application/json") });
        var result = Assert.IsType<SearchFileInArrResponse>(Assert.IsType<OkObjectResult>(await Controller().HandlePostApiRequest()).Value);
        Assert.Equal("partial", result.Outcome);
        Assert.Contains(result.Results, item => item.State == "unconfirmed");
        Assert.Equal(2, _requests.Count);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class ScriptedClient(string host, HttpClient client, Func<Task<List<ArrRootFolder>>> roots) : ArrClient(host, "synthetic")
    {
        protected override HttpClient Client => client;
        public override Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct = default) => roots();
        public override Task<ArrMediaFileMatch?> FindMediaFileAsync(string symlinkOrStrmPath, CancellationToken ct = default) =>
            Task.FromResult<ArrMediaFileMatch?>(new(ArrMediaKind.Movie, 20, [10]));
    }
}