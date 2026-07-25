using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueResponse : SabBaseResponse
{
    [JsonPropertyName("queue")]
    public QueueObject Queue { get; init; } = new();

    public class QueueObject
    {
        [JsonPropertyName("paused")]
        public bool Paused { get; init; } = false;

        [JsonPropertyName("slots")]
        public List<QueueSlot> Slots { get; init; } = new();

        [JsonPropertyName("noofslots")]
        public int TotalCount { get; set; }

        // Accepted-and-stored only (see #375 for actual byte/s enforcement). SAB
        // reports "speedlimit" as a percentage of a configured max line speed;
        // NzbDAV has no such setting, so both fields carry the same KB/s value.
        [JsonPropertyName("speedlimit")]
        public string SpeedLimit { get; init; } = "0";

        [JsonPropertyName("speedlimit_abs")]
        public string SpeedLimitAbs { get; init; } = "0";

        // Seconds remaining on a scheduled/timed unpause. NzbDAV's pause has no
        // duration, so this is always "0".
        [JsonPropertyName("pause_int")]
        public string PauseInt { get; init; } = "0";
    }

    public class QueueSlot
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("nzo_id")]
        public string NzoId { get; init; } = null!;

        [JsonPropertyName("priority")]
        public string Priority { get; init; } = null!;

        [JsonPropertyName("filename")]
        public string Filename { get; init; } = null!;

        [JsonPropertyName("cat")]
        public string Category { get; init; } = null!;

        [JsonPropertyName("percentage")]
        public string Percentage { get; init; } = null!;

        [JsonPropertyName("true_percentage")]
        public string TruePercentage { get; init; } = null!;

        [JsonPropertyName("status")]
        public string Status { get; init; } = null!;

        [JsonPropertyName("timeleft")]
        [JsonConverter(typeof(SabnzbdQueueTimeConverter))]
        public TimeSpan TimeLeft { get; init; }

        [JsonPropertyName("mb")]
        public string SizeInMB { get; init; } = null!;

        [JsonPropertyName("mbleft")]
        public string SizeLeftInMB { get; init; } = null!;

        [JsonPropertyName("indexer")]
        public string? Indexer { get; init; }

        [JsonPropertyName("providers")]
        public List<ProviderUsage>? Providers { get; init; }

        public static QueueSlot FromQueueItem
        (
            QueueItem queueItem,
            int index = 0,
            int progressPercentage = 0,
            string status = "Queued",
            IReadOnlyDictionary<string, long>? providerUsage = null,
            IReadOnlyDictionary<string, (string Host, string? Nickname)>? displayByMetricsKey = null
        )
        {
            var sabProgressPercentage = Math.Clamp(progressPercentage, 0, 100);
            return new QueueSlot
            {
                Index = index,
                NzoId = queueItem!.Id.ToString(),
                Priority = queueItem.Priority.ToString(),
                Filename = queueItem.FileName,
                Category = queueItem.Category,
                Percentage = sabProgressPercentage.ToString(CultureInfo.InvariantCulture),
                TruePercentage = progressPercentage.ToString(CultureInfo.InvariantCulture),
                Status = status,
                TimeLeft = TimeSpan.Zero,
                SizeInMB = FormatSizeMB(queueItem.TotalSegmentBytes),
                SizeLeftInMB = FormatSizeMB(
                    (100 - sabProgressPercentage) * queueItem.TotalSegmentBytes / 100),
                Indexer = queueItem.IndexerName,
                Providers = providerUsage is { Count: > 0 }
                    ? providerUsage
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
                        .ToList()
                    : null,
            };
        }

        private static string FormatSizeMB(long bytes)
        {
            var megabytes = bytes / (1024.0 * 1024.0);
            return megabytes.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    public class SabnzbdQueueTimeConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
            throw new NotImplementedException();

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(@"d\:h\:m\:s"));
    }

    public class ProviderUsage
    {
        [JsonPropertyName("host")] public required string Host { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
        [JsonPropertyName("segments")] public required long Segments { get; init; }
    }
}
