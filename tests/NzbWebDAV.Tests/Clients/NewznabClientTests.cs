using System.Net;
using System.Text;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Clients;

public class NewznabClientTests
{
    private const string ApiKey = "DO_NOT_LOG_INDEXER_KEY";
    private const string BodyMarker = "DO_NOT_LOG_BODY_MARKER";

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task QueryAsync_ValidBody_AtOrBelowLimit_IsAccepted(int headroom)
    {
        var body = SearchXml();
        var limit = body.Length + headroom;
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var client = Create(http, limit);

        var items = await client.QueryAsync(SearchParams());

        var item = Assert.Single(items);
        Assert.Equal("Example Release", item.Title);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task QueryAsync_OneByteOverLimit_RejectsWithoutPartialResult()
    {
        var body = SearchXml();
        var limit = body.Length - 1;
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var client = Create(http, limit);

        var ex = await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => client.QueryAsync(SearchParams()));

        Assert.Equal(limit, ex.MaxBytes);
        Assert.DoesNotContain(ApiKey, ex.Message);
        Assert.DoesNotContain(BodyMarker, ex.Message);
    }

    [Fact]
    public async Task TestAsync_ValidCaps_AtLimit_Succeeds()
    {
        var body = CapsXml();
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var client = Create(http, body.Length);

        Assert.True(await client.TestAsync());
    }

    [Fact]
    public async Task TestAsync_OneByteOverLimit_Rejects()
    {
        var body = CapsXml();
        using var handler = OkHandler(body);
        using var http = new HttpClient(handler);
        var client = Create(http, body.Length - 1);

        await Assert.ThrowsAsync<RemoteResponseTooLargeException>(() => client.TestAsync());
    }

    [Fact]
    public async Task QueryAsync_DeclaredLengthAboveLimit_RejectsBeforeStreamRead()
    {
        var limit = 64;
        using var content = new FailIfOpenedContent(limit + 1);
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var http = new HttpClient(handler);
        var client = Create(http, limit);

        var ex = await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => client.QueryAsync(SearchParams()));

        Assert.Equal(limit, ex.MaxBytes);
        Assert.Equal(limit + 1, ex.DeclaredBytes);
        Assert.False(content.StreamOpened);
    }

    [Fact]
    public async Task QueryAsync_DeclaredAtLimit_ActualShorter_Parses()
    {
        var body = SearchXml();
        var limit = body.Length + 32;
        var stream = new TrackingStream(body);
        using var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamHttpContent(stream, declaredLength: limit),
            };
            return response;
        });
        using var http = new HttpClient(handler);
        var client = Create(http, limit);

        var items = await client.QueryAsync(SearchParams());
        Assert.Equal("Example Release", Assert.Single(items).Title);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task QueryAsync_DeclaredBelowLimit_ActualOver_RejectsByStreamCount()
    {
        var body = SearchXml();
        var limit = body.Length;
        var over = PadUtf8(body, limit + 1);
        var stream = new TrackingStream(over);
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamHttpContent(stream, declaredLength: limit - 1),
        });
        using var http = new HttpClient(handler);
        var client = Create(http, limit);

        await Assert.ThrowsAsync<RemoteResponseTooLargeException>(() => client.QueryAsync(SearchParams()));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task QueryAsync_ChunkedUndeclared_ExactLimitSucceeds()
    {
        var body = SearchXml();
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
        var client = Create(http, body.Length);

        var items = await client.QueryAsync(SearchParams());
        Assert.Equal("Example Release", Assert.Single(items).Title);
    }

    [Fact]
    public async Task QueryAsync_NonEofOversize_RejectsBeforeEagerBuffering()
    {
        var body = SearchXml();
        var limit = body.Length;
        var prefix = PadUtf8(body, limit + 1);
        var stream = new PrefixThenBlockStream(prefix);
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamHttpContent(stream),
        });
        using var http = new HttpClient(handler);
        var client = Create(http, limit);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var ex = await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => client.QueryAsync(SearchParams(), guard.Token));

        Assert.Equal(limit, ex.MaxBytes);
        Assert.False(stream.EnteredWait);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task QueryAsync_MultibyteUtf8Title_IsBoundedByBytes()
    {
        var body = SearchXml("Café 🎬");
        Assert.True(Encoding.UTF8.GetCharCount(body) < body.Length);

        using (var handler = OkHandler(body))
        using (var http = new HttpClient(handler))
        {
            var items = await Create(http, body.Length).QueryAsync(SearchParams());
            Assert.Equal("Café 🎬", Assert.Single(items).Title);
        }

        using var overHandler = OkHandler(body);
        using var overHttp = new HttpClient(overHandler);
        await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => Create(overHttp, body.Length - 1).QueryAsync(SearchParams()));
    }

    [Fact]
    public async Task QueryAsync_CallerCancellationDuringBodyRead_IsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var stream = new BlockingReadStream();
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamHttpContent(stream),
        });
        using var http = new HttpClient(handler);
        var client = Create(http, 1024);

        var task = client.QueryAsync(SearchParams(), cts.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(cts.IsCancellationRequested);
        Assert.IsNotType<RemoteResponseTooLargeException>(ex);
        Assert.IsNotType<RemoteResponseFormatException>(ex);
        Assert.Equal(1, handler.RequestCount);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task QueryAsync_AlreadyCancelled_DoesNotParse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var handler = OkHandler(SearchXml());
        using var http = new HttpClient(handler);
        var client = Create(http, 4096);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.QueryAsync(SearchParams(), cts.Token));
    }

    [Fact]
    public async Task QueryAsync_MalformedXml_IsControlledFormatError()
    {
        var xml = Encoding.UTF8.GetBytes("""<?xml version="1.0"?><rss><channel><item><title>x</item></channel></rss>""");
        using var handler = OkHandler(xml);
        using var http = new HttpClient(handler);
        var client = Create(http, 4096);

        var ex = await Assert.ThrowsAsync<RemoteResponseFormatException>(
            () => client.QueryAsync(SearchParams()));

        Assert.Equal("Indexer returned invalid XML.", ex.Message);
        Assert.DoesNotContain("<item>", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
        Assert.DoesNotContain(BodyMarker, ex.Message);
    }

    [Fact]
    public async Task QueryAsync_NewznabErrorElement_IsProtocolErrorNotFormat()
    {
        var xml = Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><error code="100" description="Missing parameter"/>""");
        using var handler = OkHandler(xml);
        using var http = new HttpClient(handler);
        var client = Create(http, 4096);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(SearchParams()));
        Assert.Contains("[100]", ex.Message);
        Assert.Contains("Missing parameter", ex.Message);
        Assert.IsNotType<RemoteResponseFormatException>(ex);
    }

    [Fact]
    public async Task QueryAsync_Doctype_IsFormatErrorWithoutResolverAccess()
    {
        var xml = Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><!DOCTYPE caps SYSTEM "http://127.0.0.1:1/should-not-resolve"><caps></caps>""");
        using var handler = OkHandler(xml);
        using var http = new HttpClient(handler);
        var client = Create(http, 4096);

        var ex = await Assert.ThrowsAsync<RemoteResponseFormatException>(() => client.QueryAsync(SearchParams()));
        Assert.Equal("Indexer returned invalid XML.", ex.Message);
        Assert.DoesNotContain("127.0.0.1", ex.Message);
    }

    [Fact]
    public async Task QueryAsync_NonSuccessStatus_DoesNotRetry()
    {
        using var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var http = new HttpClient(handler);
        var client = Create(http, 4096);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryAsync(SearchParams()));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RetriesOnceOnFirstTransportFailure()
    {
        var body = SearchXml();
        var calls = 0;
        using var retryHandler = new ScriptedHandler((_, _) =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("transient");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        });
        using var http = new HttpClient(retryHandler);
        var client = Create(http, body.Length + 16);

        var items = await client.QueryAsync(SearchParams());
        Assert.Equal("Example Release", Assert.Single(items).Title);
        Assert.Equal(2, retryHandler.RequestCount);
    }

    [Fact]
    public async Task QueryAsync_Oversize_DoesNotEmbedApiKeyOrBody()
    {
        using var handler = OkHandler(SearchXml(BodyMarker));
        using var http = new HttpClient(handler);
        var client = Create(http, 8);

        var ex = await Assert.ThrowsAsync<RemoteResponseTooLargeException>(
            () => client.QueryAsync(SearchParams()));
        Assert.DoesNotContain(ApiKey, ex.ToString());
        Assert.DoesNotContain(BodyMarker, ex.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    private static NewznabClient Create(HttpClient http, long maxResponseBytes) =>
        new(http, "http://indexer.example/api", ApiKey, maxResponseBytes);

    private static Dictionary<string, string> SearchParams() => new()
    {
        ["t"] = "search",
        ["q"] = "query",
        ["limit"] = "100",
    };

    private static ScriptedHandler OkHandler(byte[] body) =>
        new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        });

    private static byte[] CapsXml() =>
        Encoding.UTF8.GetBytes("""<?xml version="1.0"?><caps><server version="1.0" title="t"/></caps>""");

    private static byte[] SearchXml(string title = "Example Release") =>
        Encoding.UTF8.GetBytes(
            $"""<?xml version="1.0"?><rss version="2.0" xmlns:newznab="http://www.newznab.com/DTD/2010/feeds/attributes/"><channel><item><title>{title}</title><guid>g1</guid><link>http://nzb.example/a.nzb</link><enclosure url="http://nzb.example/a.nzb" length="10"/></item></channel></rss>""");

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
}
