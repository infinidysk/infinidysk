using System.Globalization;
using System.Text.Json.Serialization;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Queue;

/// <summary>
/// Websocket payload for <c>QueueItemAdded</c>. JSON names match SAB queue slots.
/// </summary>
public sealed class QueueItemAddedPayload
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("nzo_id")] public string NzoId { get; init; } = null!;
    [JsonPropertyName("priority")] public string Priority { get; init; } = null!;
    [JsonPropertyName("filename")] public string Filename { get; init; } = null!;
    [JsonPropertyName("cat")] public string Category { get; init; } = null!;
    [JsonPropertyName("percentage")] public string Percentage { get; init; } = null!;
    [JsonPropertyName("true_percentage")] public string TruePercentage { get; init; } = null!;
    [JsonPropertyName("status")] public string Status { get; init; } = null!;
    [JsonPropertyName("timeleft")] public string TimeLeft { get; init; } = "0:00:00";
    [JsonPropertyName("mb")] public string SizeInMB { get; init; } = null!;
    [JsonPropertyName("mbleft")] public string SizeLeftInMB { get; init; } = null!;
    [JsonPropertyName("indexer")] public string? Indexer { get; init; }
    [JsonPropertyName("providers")] public List<ProviderUsage>? Providers { get; init; }

    public static QueueItemAddedPayload FromQueueItem(
        QueueItem queueItem,
        int index = 0,
        int progressPercentage = 0,
        string status = "Queued",
        TimeSpan? eta = null,
        IReadOnlyDictionary<string, long>? providerUsage = null,
        IReadOnlyDictionary<string, (string Host, string? Nickname)>? displayByMetricsKey = null)
    {
        var sabProgressPercentage = Math.Clamp(progressPercentage, 0, 100);
        return new QueueItemAddedPayload
        {
            Index = index,
            NzoId = queueItem.Id.ToString(),
            Priority = queueItem.Priority.ToString(),
            Filename = queueItem.FileName,
            Category = queueItem.Category,
            Percentage = sabProgressPercentage.ToString(CultureInfo.InvariantCulture),
            TruePercentage = progressPercentage.ToString(CultureInfo.InvariantCulture),
            Status = status,
            TimeLeft = FormatTimeLeft(eta ?? TimeSpan.Zero),
            SizeInMB = FormatSizeMB(queueItem.TotalSegmentBytes),
            SizeLeftInMB = FormatSizeMB((100 - sabProgressPercentage) * queueItem.TotalSegmentBytes / 100),
            Indexer = queueItem.IndexerName,
            Providers = MapProviders(providerUsage, displayByMetricsKey),
        };
    }

    internal static List<ProviderUsage>? MapProviders(
        IReadOnlyDictionary<string, long>? providerUsage,
        IReadOnlyDictionary<string, (string Host, string? Nickname)>? displayByMetricsKey)
    {
        if (providerUsage is not { Count: > 0 }) return null;
        return providerUsage
            .OrderByDescending(kv => kv.Value)
            .Select(kv =>
            {
                var host = kv.Key;
                string? nickname = null;
                if (displayByMetricsKey is not null &&
                    displayByMetricsKey.TryGetValue(kv.Key, out var display))
                {
                    host = display.Host;
                    nickname = display.Nickname;
                }

                return new ProviderUsage
                {
                    Host = host,
                    Nickname = nickname,
                    Segments = kv.Value,
                };
            })
            .ToList();
    }

    private static string FormatSizeMB(long bytes)
    {
        var megabytes = bytes / (1024.0 * 1024.0);
        return megabytes.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatTimeLeft(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return "0:00:00";
        var totalSeconds = (int)Math.Round(value.TotalSeconds, MidpointRounding.AwayFromZero);
        if (totalSeconds <= 0) return "0:00:00";
        var days = totalSeconds / 86400;
        var hours = totalSeconds % 86400 / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return days > 0
            ? $"{days}:{hours:D2}:{minutes:D2}:{seconds:D2}"
            : $"{hours}:{minutes:D2}:{seconds:D2}";
    }

    public sealed class ProviderUsage
    {
        [JsonPropertyName("host")] public required string Host { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
        [JsonPropertyName("segments")] public required long Segments { get; init; }
    }
}
