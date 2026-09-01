namespace NzbWebDAV.Exceptions;

/// <summary>
/// A blob-store record exists on disk but failed to decompress/deserialize
/// (truncated write, unclean shutdown, restore without a matching blob).
/// The payload itself is present but unreadable — distinct from
/// <see cref="MissingFilePayloadException"/> — and never implicates the
/// release's Usenet articles, so this must never trigger Arr repair.
/// </summary>
public class CorruptedBlobPayloadException : Exception
{
    public CorruptedBlobPayloadException(Guid blobId, string blobPath, Type payloadType, Exception inner)
        : base($"The local streaming metadata blob '{blobId}' ({payloadType.Name}) at '{blobPath}' " +
               "is unreadable. Restore a backup of the blobs/ folder that matches the database, " +
               "or remove and re-download the release.",
            inner)
    {
        BlobId = blobId;
        BlobPath = blobPath;
        PayloadType = payloadType;
    }

    public Guid BlobId { get; }
    public string BlobPath { get; }
    public Type PayloadType { get; }
}
