
using System.Text.Json;

namespace NzbWebDAV.Clients.MediaServers;

internal static class MediaServerHttp
{
    private const int MaxSessionResponseBytes = 4 * 1024 * 1024;

    public static Uri Endpoint(string baseUrl, string relativePath) =>
        new(baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/'), UriKind.Absolute);

    public static async Task<JsonDocument> SendJsonAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxSessionResponseBytes)
            throw new InvalidDataException("Media-server session response exceeded the size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            if (buffer.Length + read > MaxSessionResponseBytes)
                throw new InvalidDataException("Media-server session response exceeded the size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
