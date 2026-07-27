using System.Text.Json.Serialization;
using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Services.StreamTrace;

public sealed record StreamTraceEvent
{
    [JsonPropertyName("seq")] public required long Sequence { get; init; }
    [JsonPropertyName("at")] public required long AtUnixMs { get; init; }
    [JsonPropertyName("sessionId")] public required Guid SessionId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("rangeStart")] public long? RangeStart { get; init; }
    [JsonPropertyName("rangeEnd")] public long? RangeEnd { get; init; }
    [JsonPropertyName("fileSize")] public long? FileSize { get; init; }
    [JsonPropertyName("userAgent")] public string? UserAgent { get; init; }
    [JsonPropertyName("clientIp")] public string? ClientIp { get; init; }

    [JsonPropertyName("offset")] public long? Offset { get; init; }

    [JsonPropertyName("provider")] public string? Provider { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("durationMs")] public int? DurationMs { get; init; }
    [JsonPropertyName("retries")] public int? Retries { get; init; }
    [JsonPropertyName("segmentId")] public string? SegmentId { get; init; }

    [JsonPropertyName("bytes")] public long? Bytes { get; init; }
    [JsonPropertyName("endReason")] public string? EndReason { get; init; }
    [JsonPropertyName("bytesServed")] public long? BytesServed { get; init; }
    [JsonPropertyName("fromProvider")] public string? FromProvider { get; init; }
    [JsonPropertyName("toProvider")] public string? ToProvider { get; init; }
    [JsonPropertyName("attempt")] public int? Attempt { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }

    // Stall attribution, emitted on RangeEnd and reset per range. These overlap by
    // design — segments are fetched concurrently — so they are shares of a range's
    // wall clock, not a partition of it.
    [JsonPropertyName("connWaitMs")] public long? ConnectionWaitMs { get; init; }
    [JsonPropertyName("providerWaitMs")] public long? ProviderWaitMs { get; init; }
    [JsonPropertyName("bodyDrainMs")] public long? BodyDrainMs { get; init; }
    [JsonPropertyName("consumerWaitMs")] public long? ConsumerWaitMs { get; init; }
    [JsonPropertyName("clientWriteMs")] public long? ClientWriteMs { get; init; }
    [JsonPropertyName("connOpened")] public long? ConnectionsOpened { get; init; }
    [JsonPropertyName("connReused")] public long? ConnectionsReused { get; init; }

    public static string StatusName(SegmentFetch.FetchStatus status) => status.ToString();

    public static string EndReasonName(ReadSession.EndReasonCode reason) => reason.ToString();

    /// <summary>
    /// Truncate a Message-ID for traces (enough to correlate, not full payload noise).
    /// </summary>
    public static string? TruncateSegmentId(string? segmentId, int maxLen = 48)
    {
        if (string.IsNullOrEmpty(segmentId)) return null;
        return segmentId.Length <= maxLen ? segmentId : segmentId[..maxLen] + "…";
    }
}
