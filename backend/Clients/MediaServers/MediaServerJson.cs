
using System.Text.Json;

namespace NzbWebDAV.Clients.MediaServers;

internal static class MediaServerJson
{
    public static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            return true;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    public static string? String(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? Int64(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    public static int? Int32(JsonElement element, string name)
    {
        var value = Int64(element, name);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    public static bool? Bool(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    public static long? TicksToMilliseconds(long? ticks) => ticks is null ? null : ticks.Value / 10_000L;
}
