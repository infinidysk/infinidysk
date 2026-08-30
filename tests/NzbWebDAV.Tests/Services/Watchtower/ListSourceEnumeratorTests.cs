using System.Net;
using System.Text;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services.Watchtower;

[Collection(nameof(GlobalLoggerCollection))]
public class ListSourceEnumeratorTests
{
    private const string UserInfo = "DO_NOT_LOG_USERINFO";
    private const string QueryToken = "DO_NOT_LOG_QUERY_TOKEN";
    private const string BodyMarker = "DO_NOT_LOG_BODY_MARKER";
    private const string SecretUrl = $"https://{UserInfo}@lists.example/list?token={QueryToken}";

    [Fact]
    public async Task EnumerateAsync_Manual_MakesNoRequest()
    {
        using var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("no HTTP"));
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => 4096);

        var refs = await enumerator.EnumerateAsync(new ListSource
        {
            Kind = ListSource.KindManual,
            Name = "Manual",
        }, CancellationToken.None);

        Assert.Empty(refs);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task StremioCatalog_ValidBody_AtOrBelowLimit_IsAccepted(int headroom)
    {
        var body = StremioPage("tt1", "A");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length + headroom);

        var refs = await enumerator.EnumerateAsync(CatalogSource(), CancellationToken.None);

        var item = Assert.Single(refs);
        Assert.Equal("movie", item.Type);
        Assert.Equal("tt1", item.ContentId);
        Assert.Equal("A", item.Title);
    }

    [Fact]
    public async Task StremioCatalog_OneByteOverLimit_Rejects()
    {
        var body = StremioPage("tt1", "A");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length - 1);

        var ex = await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => enumerator.EnumerateAsync(CatalogSource(), CancellationToken.None));
        Assert.Equal(body.Length - 1, ex.MaxBytes);
        Assert.DoesNotContain(UserInfo, ex.Message);
        Assert.DoesNotContain(QueryToken, ex.Message);
    }

    [Fact]
    public async Task DiscoverCatalogs_ValidManifest_ParsesChoices()
    {
        var body = ManifestJson();
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length);

        var result = await enumerator.DiscoverCatalogsAsync("https://addon.example/manifest.json", CancellationToken.None);

        Assert.Equal("Addon", result.AddonName);
        var choice = Assert.Single(result.Catalogs);
        Assert.Equal("movie", choice.Type);
        Assert.Equal("top", choice.Id);
    }

    [Fact]
    public async Task DiscoverCatalogs_Oversize_RejectsWithoutUrl()
    {
        var body = ManifestJson();
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length - 1);

        var ex = await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => enumerator.DiscoverCatalogsAsync(SecretUrl, CancellationToken.None));
        Assert.DoesNotContain(UserInfo, ex.Message);
        Assert.DoesNotContain(QueryToken, ex.Message);
        Assert.DoesNotContain("https://", ex.Message);
    }

    [Fact]
    public async Task DiscoverCatalogs_FetchFailure_DoesNotEchoUrl()
    {
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => 4096);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => enumerator.DiscoverCatalogsAsync(SecretUrl, CancellationToken.None));
        Assert.Equal("Could not fetch the addon manifest.", ex.Message);
        Assert.DoesNotContain(UserInfo, ex.Message);
        Assert.DoesNotContain(QueryToken, ex.Message);
    }

    [Fact]
    public async Task HttpGet_TransportFailure_DebugLogOmitsUrlAndSecrets()
    {
        using var handler = new ScriptedHandler((_, _) => throw new HttpRequestException($"fail {UserInfo} {QueryToken}"));
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => 4096);
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None));
            Assert.Equal("List request failed or returned an empty response.", ex.Message);
        }
        finally
        {
            Log.Logger = previous;
        }

        var debug = Assert.Single(sink.Events, e => e.Level == LogEventLevel.Debug);
        var rendered = debug.RenderMessage();
        Assert.Contains("remote list fetch failed", rendered);
        Assert.Contains("HttpRequestException", rendered);
        Assert.DoesNotContain(UserInfo, rendered);
        Assert.DoesNotContain(QueryToken, rendered);
        Assert.DoesNotContain("https://", rendered);
        Assert.Null(debug.Exception);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task UrlList_JsonArray_AtOrBelowLimit_IsAccepted(int headroom)
    {
        var body = Encoding.UTF8.GetBytes("""["tt0111161"]""");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length + headroom);

        var refs = await enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None);
        var item = Assert.Single(refs);
        Assert.Equal("movie", item.Type);
        Assert.Equal("tt0111161", item.ContentId);
    }

    [Fact]
    public async Task UrlList_JsonItemsObject_Parses()
    {
        var body = Encoding.UTF8.GetBytes("""{"items":[{"id":"tt2","type":"series","name":"Show"}]}""");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length);

        var refs = await enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None);
        var item = Assert.Single(refs);
        Assert.Equal("series", item.Type);
        Assert.Equal("tt2", item.ContentId);
        Assert.Equal("Show", item.Title);
    }

    [Fact]
    public async Task UrlList_PlainText_AtLimit_IsAccepted()
    {
        var body = Encoding.UTF8.GetBytes("tt0111161\n# comment\nseries:tt90\n");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length);

        var refs = await enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None);
        Assert.Equal(2, refs.Count);
        Assert.Equal("tt0111161", refs[0].ContentId);
        Assert.Equal("series", refs[1].Type);
        Assert.Equal("tt90", refs[1].ContentId);
    }

    [Fact]
    public async Task UrlList_PlainText_OneByteOver_Rejects()
    {
        var body = Encoding.UTF8.GetBytes("tt0111161\n");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length - 1);

        await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None));
    }

    [Fact]
    public async Task UrlList_BlankAndCommentsOnly_ReturnsEmpty()
    {
        var body = Encoding.UTF8.GetBytes("\n# only comments\n  \n");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length);

        var refs = await enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None);
        Assert.Empty(refs);
    }

    [Fact]
    public async Task UrlList_MalformedJsonLooking_FailsClosed()
    {
        var body = Encoding.UTF8.GetBytes("""{"items": [""");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => 4096);

        var ex = await Assert.ThrowsAsync<RemoteResponseFormatException>(
            () => enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None));
        Assert.Equal("The list response was not valid JSON.", ex.Message);
        Assert.DoesNotContain(BodyMarker, ex.Message);
    }

    [Fact]
    public async Task UrlList_ValidJsonWithoutArray_ReturnsEmptyWithoutLineFallback()
    {
        var body = Encoding.UTF8.GetBytes("""{"not":"a-list"}""");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length);

        var refs = await enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None);
        Assert.Empty(refs);
    }

    [Fact]
    public async Task StremioCatalog_MalformedJson_IsControlledFormatError()
    {
        var body = Encoding.UTF8.GetBytes("""{"metas":""");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => 4096);

        var ex = await Assert.ThrowsAsync<RemoteResponseFormatException>(
            () => enumerator.EnumerateAsync(CatalogSource(), CancellationToken.None));
        Assert.Equal("The addon response was not valid JSON.", ex.Message);
    }

    [Fact]
    public async Task StremioCatalog_ValidJsonWithoutMetas_IsCatalogGuidance()
    {
        var body = Encoding.UTF8.GetBytes("""{"catalogs":[]}""");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => enumerator.EnumerateAsync(CatalogSource(), CancellationToken.None));
        Assert.Contains("addon manifest", ex.Message);
    }

    [Fact]
    public async Task DiscoverCatalogs_ValidJsonWithoutCatalogs_IsGuidance()
    {
        var body = Encoding.UTF8.GetBytes("""{"name":"X"}""");
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => enumerator.DiscoverCatalogsAsync("https://addon.example/manifest.json", CancellationToken.None));
        Assert.Equal("No catalogs were found in this addon manifest.", ex.Message);
    }

    [Fact]
    public async Task StremioCatalog_NonEofOversize_RejectsBeforeEagerBuffering()
    {
        var body = StremioPage("tt1", "A");
        var limit = body.Length;
        var prefix = PadUtf8(body, limit + 1);
        var stream = new PrefixThenBlockStream(prefix);
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamHttpContent(stream),
        });
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => limit);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var ex = await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => enumerator.EnumerateAsync(CatalogSource(), guard.Token));
        Assert.False(stream.EnteredWait);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task StremioCatalog_MultibyteUtf8Title_IsBoundedByBytes()
    {
        var body = StremioPage("tt1", "Café 🎬");
        Assert.True(Encoding.UTF8.GetCharCount(body) < body.Length);

        using (var handler = OkHandler(body))
        using (var http = new HttpClient(handler))
        {
            var refs = await new ListSourceEnumerator(http, () => body.Length)
                .EnumerateAsync(CatalogSource(), CancellationToken.None);
            Assert.Equal("Café 🎬", Assert.Single(refs).Title);
        }

        using var overHandler = OkHandler(body);
        using var overHttp = new HttpClient(overHandler);
        await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => new ListSourceEnumerator(overHttp, () => body.Length - 1)
                .EnumerateAsync(CatalogSource(), CancellationToken.None));
    }

    [Fact]
    public async Task UrlList_CallerCancellationDuringBodyRead_IsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var stream = new BlockingReadStream();
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamHttpContent(stream),
        });
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => 1024);

        var task = enumerator.EnumerateAsync(UrlListSource(), cts.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UrlList_ChunkedUndeclared_ExactLimitSucceeds()
    {
        var body = Encoding.UTF8.GetBytes("tt0111161\n");
        var stream = new TrackingStream(body);
        using var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamHttpContent(stream),
            };
            response.Headers.TransferEncodingChunked = true;
            return response;
        });
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => body.Length);

        var refs = await enumerator.EnumerateAsync(UrlListSource(), CancellationToken.None);
        Assert.Equal("tt0111161", Assert.Single(refs).ContentId);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task StremioCatalog_NonSuccessStatus_IsFetchFailed()
    {
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var http = new HttpClient(handler);
        var enumerator = new ListSourceEnumerator(http, () => 4096);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => enumerator.EnumerateAsync(CatalogSource(), CancellationToken.None));
        Assert.Equal("Catalog request failed or returned an empty response.", ex.Message);
    }

    private static ListSource CatalogSource() => new()
    {
        Kind = ListSource.KindStremioCatalog,
        Name = "My Catalog",
        Url = SecretUrl,
        Cap = 10,
    };

    private static ListSource UrlListSource() => new()
    {
        Kind = ListSource.KindUrlList,
        Name = "My List",
        Url = SecretUrl,
    };

    private static ScriptedHandler OkHandler(byte[] body) =>
        new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        });

    private static byte[] StremioPage(string id, string name) =>
        Encoding.UTF8.GetBytes(
            $$"""{"metas":[{"type":"movie","id":"{{id}}","name":"{{name}}"}]}""");

    private static byte[] ManifestJson() =>
        Encoding.UTF8.GetBytes(
            """{"name":"Addon","catalogs":[{"type":"movie","id":"top","name":"Top"}]}""");

    private static byte[] PadUtf8(byte[] body, int targetLength)
    {
        if (body.Length > targetLength)
            throw new InvalidOperationException("body already larger than target");
        if (body.Length == targetLength) return body;
        var padded = new byte[targetLength];
        Buffer.BlockCopy(body, 0, padded, 0, body.Length);
        Array.Fill(padded, (byte)' ', body.Length, targetLength - body.Length);
        return padded;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }
}
