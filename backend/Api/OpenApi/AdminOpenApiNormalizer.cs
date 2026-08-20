using System.Text.Json;
using System.Text.Json.Nodes;

namespace NzbWebDAV.Api.OpenApi;

internal static class AdminOpenApiNormalizer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Normalize(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("OpenAPI document must be a JSON object.");
        if (node["info"] is JsonObject info)
            info["version"] = AdminApiContractCatalog.ContractVersion;
        node["servers"] = new JsonArray(new JsonObject { ["url"] = "" });
        var sorted = Sort(node);
        return sorted.ToJsonString(WriteOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\r', '\n') + "\n";
    }

    private static JsonNode Sort(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = pair.Value is null ? null : Sort(pair.Value);
                }

                return sorted;
            }
            case JsonArray array:
            {
                var items = array.Select(item => item is null ? null : Sort(item)).ToList();
                if (items.All(item => item is null or JsonValue))
                {
                    items = items
                        .OrderBy(item => item?.ToJsonString() ?? "", StringComparer.Ordinal)
                        .ToList();
                }

                var sorted = new JsonArray();
                foreach (var item in items)
                    sorted.Add(item);
                return sorted;
            }
            default:
                return node.DeepClone();
        }
    }
}
