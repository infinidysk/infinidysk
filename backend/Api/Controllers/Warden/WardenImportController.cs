using System.IO.Compression;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-import")]
public class WardenImportController(WardenStore warden) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var form = HttpContext.Request.HasFormContentType ? HttpContext.Request.Form : null;
        var action = form?["action"].ToString() ?? "";

        if (action == "clear")
        {
            var removed = warden.Clear();
            return Ok(new WardenImportResponse { Status = true, Added = 0, Total = warden.Count, Cleared = removed });
        }

        if (form is null || form.Files.Count == 0)
            throw new BadHttpRequestException("No file was uploaded.");

        var target = form["target"].ToString();
        var file = form.Files[0];
        if (file.Length > 256L * 1024 * 1024)
            throw new BadHttpRequestException("Warden upload exceeds the 256 MiB size limit.", StatusCodes.Status413PayloadTooLarge);

        await using var buffered = await BufferAndDecompressAsync(file, ct).ConfigureAwait(false);

        if (target == "separate")
        {
            var name = form["name"].ToString();
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileNameWithoutExtension(file.FileName).Replace(".ndjson", "", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(name)) name = "Imported list";
            var trust = form["trust"].ToString();
            var (sourceId, count) = await warden.ImportAsNewSourceAsync(buffered, name, trust, ct).ConfigureAwait(false);
            return Ok(new WardenImportResponse { Status = true, Added = count, Total = warden.Count, Cleared = 0, SourceId = sourceId });
        }

        var before = warden.LocalCount;
        await warden.MergeIntoLocalAsync(buffered, ct).ConfigureAwait(false);
        var after = warden.LocalCount;
        return Ok(new WardenImportResponse { Status = true, Added = Math.Max(0, after - before), Total = warden.Count, Cleared = 0 });
    }

    private static async Task<Stream> BufferAndDecompressAsync(IFormFile file, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await using (var raw = file.OpenReadStream())
            await raw.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        if (ms.Length >= 2)
        {
            var head = ms.GetBuffer();
            if (head[0] == 0x1f && head[1] == 0x8b)
            {
                var decompressed = new MemoryStream();
                await using (var gz = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true))
                    await CopyWithLimitAsync(gz, decompressed, WardenInputLimits.MaxDecompressedBytes, ct).ConfigureAwait(false);
                decompressed.Position = 0;
                return decompressed;
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static async Task CopyWithLimitAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var copied = 0L;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (copied > limit - read)
                throw new BadHttpRequestException("Warden source exceeds the decompressed size limit.", StatusCodes.Status413PayloadTooLarge);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
        }
    }
}

public class WardenImportResponse : BaseApiResponse
{
    [JsonPropertyName("added")] public required int Added { get; init; }
    [JsonPropertyName("total")] public required int Total { get; init; }
    [JsonPropertyName("cleared")] public required int Cleared { get; init; }
    [JsonPropertyName("sourceId")] public string? SourceId { get; init; }
}
