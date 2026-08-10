using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.GetServerStats;

public class GetServerStatsResponse
{
    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("month")]
    public long Month { get; set; }

    [JsonPropertyName("week")]
    public long Week { get; set; }

    [JsonPropertyName("day")]
    public long Day { get; set; }

    [JsonPropertyName("servers")]
    public Dictionary<string, ServerStats> Servers { get; set; } = new();

    public class ServerStats
    {
        [JsonPropertyName("total")]
        public long Total { get; set; }

        [JsonPropertyName("month")]
        public long Month { get; set; }

        [JsonPropertyName("week")]
        public long Week { get; set; }

        [JsonPropertyName("day")]
        public long Day { get; set; }

        [JsonPropertyName("daily")]
        public Dictionary<string, long> Daily { get; set; } = new();

        [JsonPropertyName("articles_tried")]
        public Dictionary<string, long> ArticlesTried { get; set; } = new();

        [JsonPropertyName("articles_success")]
        public Dictionary<string, long> ArticlesSuccess { get; set; } = new();
    }
}
