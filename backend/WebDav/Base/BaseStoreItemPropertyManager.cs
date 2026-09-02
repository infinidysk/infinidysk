using System.Xml.Linq;
using NWebDav.Server;
using NWebDav.Server.Props;
using NzbWebDAV.Utils;

namespace NzbWebDAV.WebDav.Base;

public class BaseStoreItemPropertyManager() : PropertyManager<BaseStoreItem>(DavProperties)
{
    private static readonly DavProperty<BaseStoreItem>[] DavProperties =
    [
        new DavDisplayName<BaseStoreItem>
        {
            Getter = item => XmlTextUtil.ReplaceInvalidXmlChars(item.Name)
        },
        new DavGetContentLength<BaseStoreItem>
        {
            Getter = item => item.FileSize
        },
        new DavGetContentType<BaseStoreItem>
        {
            Getter = item => ContentTypeUtil.GetContentType(item.Name)
        },
        new DavGetLastModified<BaseStoreItem>
        {
            Getter = x => WebDavCreatedAtUtil.GetLastModifiedUtc(x.CreatedAt)
        },
        new Win32FileAttributes<BaseStoreItem>
        {
            Getter = _ => FileAttributes.Normal
        },
        new DavGetResourceType<BaseStoreItem>
        {
            Getter = _ => []
        },
        new DavIsCollection<BaseStoreItem>
        {
            Getter = _ => "0"
        }
    ];

    public static readonly BaseStoreItemPropertyManager Instance = new();
}
