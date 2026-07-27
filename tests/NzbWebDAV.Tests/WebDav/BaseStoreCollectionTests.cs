using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.WebDav;

public sealed class BaseStoreCollectionTests
{
    private const int MethodNotAllowed = 405;

    [Fact]
    public async Task ReadonlyCollection_RejectsEmptyPutWithoutAddingPhantomFile()
    {
        var collection = new ReadonlyCollection();

        var result = await collection.CreateItemAsync(
            "phantom.txt", Stream.Null, overwrite: false, CancellationToken.None);

        Assert.Equal(DavStatusCode.Forbidden, result.Result);
        Assert.Null(await collection.GetItemAsync("phantom.txt", CancellationToken.None));

        var items = new List<IStoreItem>();
        await foreach (var item in collection.GetItemsAsync(CancellationToken.None))
            items.Add(item);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ReadonlyCollection_RejectsNonEmptyPut()
    {
        var collection = new ReadonlyCollection();
        await using var stream = new MemoryStream([1]);

        var result = await collection.CreateItemAsync(
            "file.txt", stream, overwrite: false, CancellationToken.None);

        Assert.Equal(DavStatusCode.Forbidden, result.Result);
    }

    [Fact]
    public void Collection_RejectsInfiniteDepth()
    {
        var collection = new ReadonlyCollection();

        Assert.Equal(InfiniteDepthMode.Rejected, collection.InfiniteDepthMode);
    }

    [Fact]
    public async Task ReadonlyCollection_RejectsMkcolForAnUnmappedNameWithForbidden()
    {
        var collection = new ReadonlyCollection();

        var result = await collection.CreateCollectionAsync(
            "new-folder", overwrite: false, CancellationToken.None);

        Assert.Equal(DavStatusCode.Forbidden, result.Result);
    }

    [Fact]
    public async Task ReadonlyCollection_RejectsMkcolForAnExistingNameWithMethodNotAllowed()
    {
        // RFC 4918 9.3.1: MKCOL may only run against an unmapped URL. Returning 403 for a
        // directory that already exists reads as a retryable permission problem, which kept
        // metadata-writing clients re-attempting the same MKCOL forever (issue #680).
        var collection = new ReadonlyCollection { ExistingItemName = "release-name-1" };

        var result = await collection.CreateCollectionAsync(
            "release-name-1", overwrite: false, CancellationToken.None);

        Assert.Equal(MethodNotAllowed, (int)result.Result);
    }

    [Fact]
    public async Task ReadonlyCollection_AggregatesRepeatedWriteRejectionsIntoOneWarning()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            // A distinct UniqueKey keeps this case independent of the process-wide throttle
            // state left behind by other tests.
            var collection = new ReadonlyCollection
            {
                Key = $"flood-{Guid.NewGuid()}",
            };

            for (var i = 0; i < 25; i++)
            {
                await collection.CreateItemAsync(
                    $"metadata-{i}.nfo", Stream.Null, overwrite: true, CancellationToken.None);
            }

            var warnings = sink.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
            var debugs = sink.Events.Where(e => e.Level == LogEventLevel.Debug).ToList();

            Assert.Single(warnings);
            Assert.Equal(25, debugs.Count);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToList();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    private sealed class ReadonlyCollection : BaseStoreReadonlyCollection
    {
        public string Key { get; init; } = "base-store-collection-tests";

        /// <summary>Name that <see cref="GetItemAsync"/> resolves, standing in for an existing child.</summary>
        public string? ExistingItemName { get; init; }

        public override string Name => "root";
        public override string UniqueKey => Key;
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        protected override Task<IStoreItem?> GetItemAsync(GetItemRequest request)
        {
            if (ExistingItemName is not null && request.Name == ExistingItemName)
                return Task.FromResult<IStoreItem?>(new ExistingCollection(request.Name));

            return Task.FromResult<IStoreItem?>(null);
        }

        protected override async IAsyncEnumerable<IStoreItem> GetAllItemsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ExistingCollection(string name) : BaseStoreReadonlyCollection
    {
        public override string Name => name;
        public override string UniqueKey => $"existing/{name}";
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        protected override Task<IStoreItem?> GetItemAsync(GetItemRequest request)
            => Task.FromResult<IStoreItem?>(null);

        protected override async IAsyncEnumerable<IStoreItem> GetAllItemsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
