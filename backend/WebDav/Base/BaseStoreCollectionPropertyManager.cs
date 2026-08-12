using System.Xml.Linq;
using NWebDav.Server;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;

namespace NzbWebDAV.WebDav.Base;

internal class DavIsCollection<T> : DavString<T> where T : IStoreItem
{
    public static readonly XName PropertyName = WebDavNamespaces.DavNs + "iscollection";
    public override XName Name => PropertyName;
}

public class BaseStoreCollectionPropertyManager() : PropertyManager<BaseStoreCollection>(DavProperties)
{
    // The resourcetype XElement MUST NOT be a shared static. XElement parent
    // ownership in System.Xml.Linq is mutable: adding an XElement to a new
    // parent automatically removes it from the previous parent. NWebDav's
    // PropFindHandler builds an XDocument per request and parents this
    // element into <resourcetype>. With concurrent PROPFINDs, one request's
    // XDocument has its <collection/> element ripped out mid-serialization
    // by another, and XmlWriter then tries to close more elements than it
    // opened — throwing "Token EndElement in state EndRootElement" and
    // returning a half-written 500 to the client. Clone per call to keep
    // each XDocument independent. Adopted from elfhosted/rebased-v3.
    private static XElement NewDavResourceType()
        => new(WebDavNamespaces.DavNs + "collection");

    private static readonly DavProperty<BaseStoreCollection>[] DavProperties =
    [
        new DavDisplayName<BaseStoreCollection>
        {
            Getter = collection => collection.Name
        },
        new DavGetResourceType<BaseStoreCollection>
        {
            Getter = _ => [NewDavResourceType()]
        },
        new DavGetLastModified<BaseStoreCollection>
        {
            Getter = x => WebDavCreatedAtUtil.GetLastModifiedUtc(x.CreatedAt)
        },
        new Win32FileAttributes<BaseStoreCollection>
        {
            Getter = _ => FileAttributes.Directory
        },
        new DavQuotaAvailableBytes<BaseStoreCollection>()
        {
            Getter = _ => long.MaxValue
        },
        new DavQuotaUsedBytes<BaseStoreCollection>()
        {
            Getter = _ => 0
        },
        new DavGetContentLength<BaseStoreCollection>
        {
            Getter = _ => 0
        },
        new DavIsCollection<BaseStoreCollection>
        {
            Getter = _ => "1"
        }
    ];

    public static readonly BaseStoreCollectionPropertyManager Instance = new();
}
