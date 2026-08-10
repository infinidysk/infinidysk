using System.Text.Json;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Services;

public class WtPointer
{
    public string NzbUrl { get; set; } = null!;
    public string IndexerName { get; set; } = null!;
    public string IndexerUserAgent { get; set; } = "NzbDav";
    public string? ProxyUrl { get; set; }
    public string Title { get; set; } = null!;
    public long Size { get; set; }
    public int? Grabs { get; set; }
    public string? Poster { get; set; }
    public DateTimeOffset? UsenetDate { get; set; }

    public string Verdict { get; set; } = "unknown";
    public long? LastVerifiedAtUnix { get; set; }
}

public class WtContentRef
{
    public string Type { get; set; } = null!;
    public string ContentId { get; set; } = null!;
    public string? Title { get; set; }
}

public static class WtJson
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static List<WtPointer> ReadPointers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<WtPointer>();
        try { return JsonSerializer.Deserialize<List<WtPointer>>(json, Opts) ?? new List<WtPointer>(); }
        catch (JsonException) { return new List<WtPointer>(); }
    }

    public static string WritePointers(IEnumerable<WtPointer> pointers)
        => JsonSerializer.Serialize(pointers, Opts);

    public static List<string> ReadStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, Opts) ?? new List<string>(); }
        catch (JsonException) { return new List<string>(); }
    }

    public static string WriteStrings(IEnumerable<string> values)
        => JsonSerializer.Serialize(values, Opts);
}
