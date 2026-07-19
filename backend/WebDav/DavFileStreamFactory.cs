using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams;

namespace NzbWebDAV.WebDav;

/// <summary>
/// Builds the decoded/decrypted read stream for a usenet-backed DavItem, given its SubType.
/// Shared by the WebDAV read path (DatabaseStoreNzbFile/RarFile/MultipartFile) and by the
/// prefetch-cache warmer (PrefetchCacheService), so both read from usenet the exact same way.
/// </summary>
public static class DavFileStreamFactory
{
    public static async Task<Stream> GetStreamAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken ct = default
    )
    {
        return davItem.SubType switch
        {
            DavItem.ItemSubType.NzbFile =>
                await GetNzbFileStreamAsync(davItem, dbClient, usenetClient, articleBufferSize, ct)
                    .ConfigureAwait(false),
            DavItem.ItemSubType.RarFile =>
                await GetRarFileStreamAsync(davItem, dbClient, usenetClient, articleBufferSize, ct)
                    .ConfigureAwait(false),
            DavItem.ItemSubType.MultipartFile =>
                await GetMultipartFileStreamAsync(davItem, dbClient, usenetClient, articleBufferSize, ct)
                    .ConfigureAwait(false),
            _ => throw new ArgumentException($"Cannot stream DavItem of SubType `{davItem.SubType}`.")
        };
    }

    private static async Task<Stream> GetNzbFileStreamAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken ct
    )
    {
        var file = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
        if (file is null) throw new FileNotFoundException($"Could not find nzb file with id: {davItem.Id}");
        return usenetClient.GetFileStream(file.SegmentIds, davItem.FileSize!.Value, articleBufferSize);
    }

    private static async Task<Stream> GetRarFileStreamAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken ct
    )
    {
        var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
        if (rarFile is null) throw new FileNotFoundException($"Could not find rar file with id: {davItem.Id}");
        return new DavMultipartFileStream(rarFile.ToDavMultipartFileMeta().FileParts, usenetClient, articleBufferSize);
    }

    private static async Task<Stream> GetMultipartFileStreamAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken ct
    )
    {
        var multipartFile = await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
        if (multipartFile is null)
            throw new FileNotFoundException($"Could not find multipart file with id: {davItem.Id}");

        var packedStream = new DavMultipartFileStream(
            multipartFile.Metadata.FileParts,
            usenetClient,
            articleBufferSize
        );

        return multipartFile.Metadata.AesParams != null
            ? new AesDecoderStream(packedStream, multipartFile.Metadata.AesParams)
            : packedStream;
    }
}
