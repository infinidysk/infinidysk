using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.WebDav;

public class BaseStoreStreamFileTests
{
    [Fact]
    public async Task GetReadableStreamAsync_DisposesScopeOnResponseCompleted()
    {
        var (context, response) = NewContext();
        using var cts = new CancellationTokenSource();
        var file = new TestStoreFile(context, new ConfigManager(), payload: [1, 2, 3]);

        await file.GetReadableStreamAsync(cts.Token);

        Assert.NotNull(cts.Token.GetContext<DownloadPriorityContext>());
        Assert.NotNull(cts.Token.GetContext<StreamingTimeoutContext>());
        await response.FireCompletedAsync();
        Assert.Null(cts.Token.GetContext<DownloadPriorityContext>());
        Assert.Null(cts.Token.GetContext<StreamingTimeoutContext>());
    }

    [Fact]
    public async Task GetDetachedReadableStreamAsync_DoesNotRegisterResponseCleanup()
    {
        var (context, response) = NewContext();
        using var cts = new CancellationTokenSource();
        var davItem = new DavItem { Id = Guid.NewGuid(), Name = "movie.mkv" };
        var file = new TestStoreFile(context, new ConfigManager(), payload: [1, 2, 3], davItem);

        var lease = await file.GetDetachedReadableStreamAsync(cts.Token);
        Assert.Same(davItem, lease.DavItem);
        Assert.Equal(0, response.CompletedCallbackCount);
        Assert.NotNull(cts.Token.GetContext<DownloadPriorityContext>());

        await response.FireCompletedAsync();
        Assert.NotNull(cts.Token.GetContext<DownloadPriorityContext>());
        Assert.NotNull(cts.Token.GetContext<StreamingTimeoutContext>());

        await lease.Ownership.DisposeAsync();
        Assert.Null(cts.Token.GetContext<DownloadPriorityContext>());
        Assert.Null(cts.Token.GetContext<StreamingTimeoutContext>());
        await lease.Stream.DisposeAsync();
    }

    [Fact]
    public async Task DetachedAndResponsePaths_UseFreshDisposables()
    {
        var (context, response) = NewContext();
        using var requestCts = new CancellationTokenSource();
        using var entryCts = new CancellationTokenSource();
        var file = new TestStoreFile(context, new ConfigManager(), payload: [9]);

        await file.GetReadableStreamAsync(requestCts.Token);
        var lease = await file.GetDetachedReadableStreamAsync(entryCts.Token);
        var requestCtx = requestCts.Token.GetContext<DownloadPriorityContext>();
        var entryCtx = entryCts.Token.GetContext<DownloadPriorityContext>();
        Assert.NotNull(requestCtx);
        Assert.NotNull(entryCtx);
        Assert.NotSame(requestCtx, entryCtx);

        await response.FireCompletedAsync();
        Assert.Null(requestCts.Token.GetContext<DownloadPriorityContext>());
        Assert.NotNull(entryCts.Token.GetContext<DownloadPriorityContext>());

        await lease.Ownership.DisposeAsync();
        Assert.Null(entryCts.Token.GetContext<DownloadPriorityContext>());
        await lease.Stream.DisposeAsync();
    }

    [Fact]
    public async Task DetachedOpenFailure_DisposesOwnershipHandle()
    {
        var (context, _) = NewContext();
        using var cts = new CancellationTokenSource();
        var file = new ThrowingStoreFile(context, new ConfigManager());

        await Assert.ThrowsAsync<IOException>(() => file.GetDetachedReadableStreamAsync(cts.Token));
        Assert.Null(cts.Token.GetContext<DownloadPriorityContext>());
        Assert.Null(cts.Token.GetContext<StreamingTimeoutContext>());
    }

    private static (DefaultHttpContext Context, CapturingResponseFeature Response) NewContext()
    {
        var features = new FeatureCollection();
        var response = new CapturingResponseFeature();
        features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        return (new DefaultHttpContext(features), response);
    }

    private sealed class CapturingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _completed = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }
        public int CompletedCallbackCount => _completed.Count;

        public void OnStarting(Func<object, Task> callback, object state) { }

        public void OnCompleted(Func<object, Task> callback, object state) =>
            _completed.Add((callback, state));

        public async Task FireCompletedAsync()
        {
            HasStarted = true;
            foreach (var (callback, state) in _completed)
                await callback(state);
        }
    }

    private sealed class TestStoreFile(
        HttpContext context,
        ConfigManager config,
        byte[] payload,
        DavItem? davItem = null) : BaseStoreStreamFile(context, config)
    {
        public override string Name => "movie.mkv";
        public override string UniqueKey => "movie";
        public override long FileSize => payload.Length;
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        protected override Task<Stream> GetStreamAsync(CancellationToken cancellationToken)
        {
            if (davItem is not null)
                Context.Items["DavItem"] = davItem;
            return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
        }
    }

    private sealed class ThrowingStoreFile(HttpContext context, ConfigManager config)
        : BaseStoreStreamFile(context, config)
    {
        public override string Name => "broken.mkv";
        public override string UniqueKey => "broken";
        public override long FileSize => 1;
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        protected override Task<Stream> GetStreamAsync(CancellationToken cancellationToken) =>
            throw new IOException("open failed");
    }
}
