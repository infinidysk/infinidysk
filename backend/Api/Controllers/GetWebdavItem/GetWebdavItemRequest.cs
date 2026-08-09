using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

public class GetWebdavItemRequest
{
    public string Item { get; init; }
    public long? RangeStart { get; init; }
    public long? RangeEnd { get; init; }
    // RFC 7233 suffix-length: "bytes=-N" means the last N bytes of the file.
    // The controller resolves this to a concrete start/end once fileSize is known.
    public long? SuffixLength { get; init; }
    public bool ShouldDownload { get; init; }

    public GetWebdavItemRequest(HttpContext context)
    {
        // normalize path
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith('/')) path = path[1..];
        if (path.StartsWith("view", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith('/')) path = path[1..];
        Item = path;

        // determine whether to download
        ShouldDownload = context.GetQueryParam("download")?.ToLowerInvariant() == "true";

        // authenticate the downloadKey
        var downloadKey = context.Request.Query["downloadKey"];
        var configManager = (ConfigManager)context.Items["configManager"]!;
        if (!VerifyDownloadKey(downloadKey, Item, configManager))
            throw new UnauthorizedAccessException("Invalid download key");

        // Parse Range; on malformed/unparseable input leave ranges null so the
        // controller serves full content (RFC 7233: ignore unsatisfiable Range syntax).
        var rangeHeader = context.Request.Headers["Range"].FirstOrDefault() ?? "";
        if (TryParseRangeHeader(rangeHeader, out var start, out var end, out var suffix))
        {
            RangeStart = start;
            RangeEnd = end;
            SuffixLength = suffix;
        }
    }

    /// <summary>
    /// Parse a single RFC 7233 bytes range. Returns false for missing, non-bytes,
    /// multi-range, or otherwise unparseable headers (caller should ignore Range).
    /// </summary>
    public static bool TryParseRangeHeader(
        string rangeHeader,
        out long? rangeStart,
        out long? rangeEnd,
        out long? suffixLength)
    {
        rangeStart = null;
        rangeEnd = null;
        suffixLength = null;

        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
            return false;

        var spec = rangeHeader["bytes=".Length..];
        // Multi-range or garbage with commas is unparseable for our single-range path.
        if (spec.Contains(',', StringComparison.Ordinal))
            return false;

        if (spec.StartsWith('-'))
        {
            if (!long.TryParse(spec[1..], out var suffix) || suffix < 0)
                return false;
            suffixLength = suffix;
            return true;
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
            return false;

        var startPart = spec[..dash];
        var endPart = spec[(dash + 1)..];

        if (!long.TryParse(startPart, out var start) || start < 0)
            return false;

        long? parsedEnd = null;
        if (endPart.Length > 0)
        {
            if (!long.TryParse(endPart, out var end) || end < 0)
                return false;
            parsedEnd = end;
        }

        rangeStart = start;
        rangeEnd = parsedEnd;
        return true;
    }

    private static bool VerifyDownloadKey(string? downloadKey, string path, ConfigManager configManager)
    {
        if (path.StartsWith(".ids", StringComparison.Ordinal))
        {
            // strm streams link items by id and use a different download key
            var strmKey = configManager.GetStrmKey();
            if (VerifyDownloadKey(downloadKey, strmKey, path))
                return true;
        }

        var apiKey = EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
        return VerifyDownloadKey(downloadKey, apiKey, path);
    }

    private static bool VerifyDownloadKey(string? downloadKey, string apiKey, string path)
    {
        return downloadKey.FixedTimeEquals(GenerateDownloadKey(apiKey, path))
            || downloadKey.FixedTimeEquals(GenerateLegacyDownloadKey(apiKey, path));
    }

    public static string GenerateDownloadKey(string apiKey, string path)
    {
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var hashBytes = HMACSHA256.HashData(keyBytes, pathBytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    private static string GenerateLegacyDownloadKey(string apiKey, string path)
    {
        var input = $"{path}_{apiKey}";
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(inputBytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
