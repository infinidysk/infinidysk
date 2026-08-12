using System.Runtime.CompilerServices;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using NzbWebDAV.Database.Models;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.Tests.WebDav;

public class GetLastModifiedPropertyTests
{
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task Item_GetLastModified_IsTrueGmt(DateTimeKind kind)
    {
        var createdAt = new DateTime(2026, 1, 15, 12, 0, 0, kind);
        var item = new StubStoreItem(createdAt);

        var value = (string?)await item.PropertyManager!
            .GetPropertyAsync(item, DavGetLastModified<IStoreItem>.PropertyName, skipExpensive: true);

        Assert.Equal(createdAt.ToUniversalTime().ToString("R"), value);

        if (TimeZoneInfo.Local.GetUtcOffset(createdAt) != TimeSpan.Zero)
            Assert.NotEqual(createdAt.ToString("R"), value);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task Collection_GetLastModified_IsTrueGmt(DateTimeKind kind)
    {
        var createdAt = new DateTime(2026, 1, 15, 12, 0, 0, kind);
        var collection = new StubStoreCollection(createdAt);

        var value = (string?)await collection.PropertyManager!
            .GetPropertyAsync(collection, DavGetLastModified<IStoreItem>.PropertyName, skipExpensive: true);

        Assert.Equal(createdAt.ToUniversalTime().ToString("R"), value);

        if (TimeZoneInfo.Local.GetUtcOffset(createdAt) != TimeSpan.Zero)
            Assert.NotEqual(createdAt.ToString("R"), value);
    }

    [Fact]
    public async Task Item_GetLastModified_MinValue_UsesUnixEpochFallback()
    {
        var item = new StubStoreItem(default);

        var value = (string?)await item.PropertyManager!
            .GetPropertyAsync(item, DavGetLastModified<IStoreItem>.PropertyName, skipExpensive: true);

        Assert.Equal(DateTime.UnixEpoch.ToString("R"), value);
        Assert.DoesNotContain("0001", value);
    }

    [Fact]
    public async Task Collection_GetLastModified_MinValue_UsesUnixEpochFallback()
    {
        var collection = new StubStoreCollection(default);

        var value = (string?)await collection.PropertyManager!
            .GetPropertyAsync(collection, DavGetLastModified<IStoreItem>.PropertyName, skipExpensive: true);

        Assert.Equal(DateTime.UnixEpoch.ToString("R"), value);
        Assert.DoesNotContain("0001", value);
    }

    [Fact]
    public void StaticDavItems_HaveNonMinValueCreatedAt()
    {
        Assert.NotEqual(default, DavItem.Root.CreatedAt);
        Assert.NotEqual(default, DavItem.NzbFolder.CreatedAt);
        Assert.NotEqual(default, DavItem.ContentFolder.CreatedAt);
        Assert.NotEqual(default, DavItem.SymlinkFolder.CreatedAt);
        Assert.NotEqual(default, DavItem.IdsFolder.CreatedAt);
        Assert.Equal(DateTime.UnixEpoch, DavItem.Root.CreatedAt);
    }

    private sealed class StubStoreItem(DateTime createdAt) : BaseStoreReadonlyItem
    {
        public override string Name => "stub.bin";
        public override string UniqueKey => "stub-item";
        public override long FileSize => 0;
        public override DateTime CreatedAt => createdAt;

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream([]));
    }

    private sealed class StubStoreCollection(DateTime createdAt) : BaseStoreReadonlyCollection
    {
        public override string Name => "stub-dir";
        public override string UniqueKey => "stub-collection";
        public override DateTime CreatedAt => createdAt;

        protected override Task<IStoreItem?> GetItemAsync(GetItemRequest request)
            => Task.FromResult<IStoreItem?>(null);

        protected override async IAsyncEnumerable<IStoreItem> GetAllItemsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
