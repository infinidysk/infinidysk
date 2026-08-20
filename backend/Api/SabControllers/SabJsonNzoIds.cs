using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;

namespace NzbWebDAV.Api.SabControllers;

internal static class SabJsonNzoIds
{
    public static async Task<List<Guid>> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!IsJsonContent(context))
            return [];

        if (context.Request.ContentLength == 0)
            return [];

        try
        {
            var deserialized = await JsonSerializer.DeserializeAsync<RequestBody>(
                    context.Request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return deserialized?.NzoIds ?? [];
        }
        catch (JsonException)
        {
            var errors = new ValidationErrors();
            errors.Add("body", "Request body is not valid JSON.");
            errors.ThrowIfAny();
            return [];
        }
    }

    private static bool IsJsonContent(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        return contentType is not null
               && contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RequestBody
    {
        [JsonPropertyName("nzo_ids")]
        public List<Guid> NzoIds { get; set; } = [];
    }
}
