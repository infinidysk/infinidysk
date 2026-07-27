using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.WebDav.Base;

public abstract class BaseStoreReadonlyItem : BaseStoreItem
{
    protected override Task<DavStatusCode> UploadFromStreamAsync(UploadFromStreamRequest request)
    {
        ReadonlyWriteRejectionLog.Rejected("upload item", Name, Name, UniqueKey);
        return Task.FromResult(DavStatusCode.Forbidden);
    }

    protected override Task<StoreItemResult> CopyAsync(CopyRequest request)
    {
        ReadonlyWriteRejectionLog.Rejected("copy item", request.Name, Name, UniqueKey);
        return Task.FromResult(new StoreItemResult(DavStatusCode.Forbidden));
    }
}
