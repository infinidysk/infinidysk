using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Exceptions;

/// <summary>
/// The DavItem exists but its streaming payload (blob-store record or legacy
/// database row) is gone — typically after a database-only restore. The media
/// itself is not implicated, so this must never trigger Arr repair.
/// </summary>
public class MissingFilePayloadException : Exception
{
    public MissingFilePayloadException(DavItem item, DavItem.ItemSubType storeKind)
        : base($"The streaming payload for file '{item.Path}' is missing " +
               $"(DavItem id: {item.Id}, payload id: {item.FileBlobId?.ToString() ?? "none"}, store: {storeKind}).")
    {
        DavItemId = item.Id;
        FileBlobId = item.FileBlobId;
        FilePath = item.Path;
        StoreKind = storeKind;
    }

    public Guid DavItemId { get; }
    public Guid? FileBlobId { get; }
    public string FilePath { get; }
    public DavItem.ItemSubType StoreKind { get; }
}
