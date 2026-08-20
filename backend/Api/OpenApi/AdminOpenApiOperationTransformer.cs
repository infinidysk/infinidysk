using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NzbWebDAV.Api.OpenApi;

internal sealed class AdminOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor is not ControllerActionDescriptor descriptor)
            return Task.CompletedTask;

        var route = context.Description.RelativePath?.TrimStart('/') ?? descriptor.ControllerName;
        var verb = context.Description.HttpMethod?.ToLowerInvariant() ?? "request";
        var routeName = route
            .Replace('/', '-')
            .Replace('{', '-')
            .Replace('}', '-')
            .Replace("--", "-", StringComparison.Ordinal)
            .Trim('-');

        operation.OperationId = $"{verb}-{routeName}";
        operation.Summary = HumanizeControllerName(descriptor.ControllerName);
        AddKnownFormRequestBody(operation, route, verb);
        operation.Responses ??= [];
        if (!operation.Responses.ContainsKey("200"))
            operation.Responses["200"] = new OpenApiResponse { Description = "Success." };
        AddProblemResponse(operation, "400", "Bad request.");
        AddProblemResponse(operation, "401", "Unauthorized.");
        AddProblemResponse(operation, "403", "Forbidden.");
        AddProblemResponse(operation, "404", "Not found.");
        AddProblemResponse(operation, "409", "Conflict.");
        AddProblemResponse(operation, "500", "Unexpected server error. Detail is sanitized; use traceId.");

        return Task.CompletedTask;
    }

    private static void AddProblemResponse(OpenApiOperation operation, string status, string description)
    {
        operation.Responses ??= [];
        if (operation.Responses.ContainsKey(status)) return;
        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchemaReference("ProblemDetails"),
                },
            },
        };
    }

    private static string HumanizeControllerName(string controllerName)
    {
        return string.Concat(controllerName.Select((character, index) =>
            index > 0 && char.IsUpper(character) && !char.IsUpper(controllerName[index - 1])
                ? $" {character}"
                : character.ToString()));
    }

    private static void AddKnownFormRequestBody(OpenApiOperation operation, string route, string verb)
    {
        if (verb != "post") return;

        string[]? fields = route switch
        {
            "api/authenticate" or "api/create-account" => ["username", "password", "type"],
            "api/get-config" => ["config-keys"],
            "api/list-webdav-directory" => ["directory"],
            "api/search-indexers" => ["q", "limit"],
            "api/test-usenet-connection" =>
                ["host", "user", "pass", "port", "use-ssl", "skip-tls-verification"],
            "api/test-arr-connection" => ["host", "apiKey"],
            "api/test-indexer-connection" =>
                ["url", "apiKey", "userAgent", "proxyUrl", "timeoutSeconds", "skipTlsVerification"],
            "api/test-prowlarr-connection" => ["url", "apiKey"],
            "api/test-rclone-connection" => ["host", "user", "pass"],
            "api/set-stream-tracing" => ["enabled", "minutes", "capacity"],
            "api/watchtower-discover-catalogs" => ["url"],
            _ => null,
        };

        if (fields is null)
        {
            if (route is "api/update-config" or "api/watchtower-mutate")
            {
                operation.RequestBody = FormBody(
                    "Submit setting names and values as multipart form fields.",
                    properties: null,
                    additionalProperties: true);
            }

            return;
        }

        operation.RequestBody = FormBody(
            "Submit the fields as multipart form data.",
            fields.ToDictionary(
                field => field,
                _ => (IOpenApiSchema)new OpenApiSchema { Type = JsonSchemaType.String }),
            additionalProperties: false);
    }

    private static OpenApiRequestBody FormBody(
        string description,
        Dictionary<string, IOpenApiSchema>? properties,
        bool additionalProperties)
    {
        return new OpenApiRequestBody
        {
            Required = true,
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Properties = properties ?? new Dictionary<string, IOpenApiSchema>(),
                        AdditionalProperties = additionalProperties
                            ? new OpenApiSchema { Type = JsonSchemaType.String }
                            : null,
                    },
                },
            },
        };
    }
}
