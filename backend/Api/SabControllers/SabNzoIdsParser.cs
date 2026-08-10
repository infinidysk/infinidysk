using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers;

internal static class SabNzoIdsParser
{
    internal sealed record Result(List<Guid> NzoIds);

    internal static List<Guid> ParseQuery(HttpContext context)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var token in context.GetQueryParamValues("value")
                     .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries |
                                                          StringSplitOptions.RemoveEmptyEntries)))
        {
            if (Guid.TryParse(token, out var id) && seen.Add(id))
                ids.Add(id);
        }

        return ids;
    }

    internal static async Task<Result> ParseAsync(HttpContext context, CancellationToken ct)
    {
        var queryIds = ParseQuery(context);
        var bodyIds = await ParseBodyAsync(context, ct).ConfigureAwait(false);
        return new Result(queryIds.Concat(bodyIds).Distinct().ToList());
    }

    private static async Task<List<Guid>> ParseBodyAsync(HttpContext context, CancellationToken ct)
    {
        try
        {
            await using var stream = context.Request.Body;
            var deserialized = await JsonSerializer.DeserializeAsync<RequestBody>(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            return deserialized?.NzoIds ?? [];
        }
        catch
        {
            return [];
        }
    }

    private class RequestBody
    {
        [JsonPropertyName("nzo_ids")]
        public List<Guid> NzoIds { get; set; } = [];
    }
}
