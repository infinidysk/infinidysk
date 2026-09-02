using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using NWebDav.Server;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.Tests.WebDav;

// Regression for infinidysk/infinidysk#1308: a single stored name containing
// U+FFFE made XmlWriter throw while serializing PROPFIND, 500-ing the whole listing.
public class DisplayNamePropertyTests
{
    private const string MojibakeName = "Mucha.Lucha.S02E20.Pi\uFFFE\uE0C3\uE0B1ata.mkv";
    private const string ExpectedName = "Mucha.Lucha.S02E20.Pi\uFFFD\uE0C3\uE0B1ata.mkv";

    [Fact]
    public async Task Item_DisplayName_ReplacesXmlInvalidChars()
    {
        var item = new StubStoreItem(MojibakeName);

        var value = (string?)await item.PropertyManager!
            .GetPropertyAsync(item, DavDisplayName<IStoreItem>.PropertyName, skipExpensive: true);

        Assert.Equal(ExpectedName, value);
        AssertSerializable(value!);
    }

    [Fact]
    public async Task Collection_DisplayName_ReplacesXmlInvalidChars()
    {
        var collection = new StubStoreCollection(MojibakeName);

        var value = (string?)await collection.PropertyManager!
            .GetPropertyAsync(collection, DavDisplayName<IStoreItem>.PropertyName, skipExpensive: true);

        Assert.Equal(ExpectedName, value);
        AssertSerializable(value!);
    }

    [Fact]
    public async Task Item_DisplayName_LeavesValidNamesUntouched()
    {
        const string name = "Piñata é à û.mkv";
        var item = new StubStoreItem(name);

        var value = (string?)await item.PropertyManager!
            .GetPropertyAsync(item, DavDisplayName<IStoreItem>.PropertyName, skipExpensive: true);

        Assert.Equal(name, value);
    }

    private static void AssertSerializable(string displayName)
    {
        var document = new XDocument(new XElement(WebDavNamespaces.DavNs + "displayname", displayName));
        using var writer = XmlWriter.Create(Stream.Null);
        document.Save(writer);
    }

    private sealed class StubStoreItem(string name) : BaseStoreReadonlyItem
    {
        private static readonly Stream EmptyReadableStream = Stream.Null;

        public override string Name => name;
        public override string UniqueKey => "stub-item";
        public override long FileSize => 0;
        public override DateTime CreatedAt => DateTime.UnixEpoch;

        public override Task<Stream> GetReadableStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult(EmptyReadableStream);
    }

    private sealed class StubStoreCollection(string name) : BaseStoreReadonlyCollection
    {
        public override string Name => name;
        public override string UniqueKey => "stub-collection";
        public override DateTime CreatedAt => DateTime.UnixEpoch;

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
