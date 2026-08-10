using NzbWebDAV.Api.Controllers;

namespace NzbWebDAV.Api.Controllers.DeleteWebdavItemPreview;

public class DeleteWebdavItemPreviewResponse : BaseApiResponse
{
    public int FileCount { get; init; }
    public int DirCount { get; init; }
    public long TotalBytes { get; init; }
    public int LinkedHistoryCount { get; init; }
}
