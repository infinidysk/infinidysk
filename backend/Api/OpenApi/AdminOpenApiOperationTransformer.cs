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
        if (route == "api/browse-files") AddBrowseParameters(operation);
        if (route == "api/get-health-check-history")
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "currentActionNeeded",
                In = ParameterLocation.Query,
                Description = "Return one latest unresolved check per existing Usenet file, excluding files already queued for repair. Filtering precedes pagination.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            });
        }
        operation.Responses ??= [];
        if (route == "api/recheck-file")
        {
            operation.Responses.Remove("200");
            AddProblemResponse(operation, "405", "POST required.");
        }
        if (route == "api/search-file-in-arr")
        {
            if (operation.Responses.TryGetValue("200", out var response)) response.Description = "Command processing completed. Inspect outcome and per-target receipts; partial and unconfirmed do not mean all searches were requested.";
            AddProblemResponse(operation, "502", "Could not verify all Arr targets; no commands requested.");
            AddProblemResponse(operation, "405", "POST required.");
        }
        if (route == "api/delete-webdav-item-preview")
        {
            operation.Parameters ??= [];
            foreach (var name in new[] { "path", "expectedDavItemId", "healthCheckResultId" })
                operation.Parameters.Add(new OpenApiParameter { Name = name, In = ParameterLocation.Query, Required = name == "path",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = name == "path" ? null : "uuid" } });
        }
        if (route == "api/trigger-health-check")
        {
            operation.Responses.Remove("200");
            operation.Responses["202"] = new OpenApiResponse
            {
                Description = "Accepted. The health-check run was queued.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchemaReference("TriggerHealthCheckResponse"),
                    },
                },
            };
        }
        if (route == "api/requeue-action-needed-health-checks")
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "davItemId",
                In = ParameterLocation.Query,
                Description = "Re-check only this file if it still needs action. Omit to re-check all eligible files.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
            });
            operation.Responses["200"] = new OpenApiResponse
            {
                Description = "Action-needed files were queued for another health check.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchemaReference("RequeueActionNeededHealthChecksResponse"),
                    },
                },
            };
            operation.Responses["409"] = new OpenApiResponse
            {
                Description = "Background repairs are disabled.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchemaReference("BaseApiResponse"),
                    },
                },
            };
        }
        if (!operation.Responses.ContainsKey("200") && !operation.Responses.ContainsKey("202"))
            operation.Responses["200"] = new OpenApiResponse { Description = "Success." };
        ApplyMissingPayloadContractOverrides(operation, route, verb);
        ApplyGcDiagnosticsContractOverrides(operation, route, verb);
        AddProblemResponse(operation, "400", "Bad request.");
        AddProblemResponse(operation, "401", "Unauthorized.");
        AddProblemResponse(operation, "403", "Forbidden.");
        AddProblemResponse(operation, "404", "Not found.");
        AddProblemResponse(operation, "409", "Conflict.");
        AddProblemResponse(operation, "500", "Unexpected server error. Detail is sanitized; use traceId.");

        return Task.CompletedTask;
    }

    private static void AddBrowseParameters(OpenApiOperation operation)
    {
        operation.Parameters ??= [];
        void Parameter(string name, OpenApiSchema schema, string? description = null) => operation.Parameters.Add(new OpenApiParameter
        {
            Name = name, In = ParameterLocation.Query, Schema = schema, Description = description,
        });
        void Choice(string name, string fallback, params string[] values) => Parameter(name, new OpenApiSchema
        {
            Type = JsonSchemaType.String, Default = System.Text.Json.Nodes.JsonValue.Create(fallback),
            Enum = values.Select(value => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(value)!).ToList(),
        });
        Parameter("scopePath", new OpenApiSchema { Type = JsonSchemaType.String, Default = System.Text.Json.Nodes.JsonValue.Create("/content") }, "Decoded canonical content directory.");
        Parameter("parentPath", new OpenApiSchema { Type = JsonSchemaType.String }, "Tree branch within scope; defaults to scopePath.");
        Choice("mode", "tree", "tree", "list");
        Parameter("q", new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 256 });
        Parameter("health", new OpenApiSchema { Type = JsonSchemaType.String }, "Comma-separated healthy, degraded, needs-attention, unknown; selections are ORed.");
        Choice("schedule", "all", "all", "due", "scheduled", "recheck-queued", "repair-pending", "never-scheduled", "checking");
        Choice("library", "all", "all", "in-library", "not-in-library", "unknown", "not-configured");
        foreach (var name in new[] { "category", "indexer" }) Parameter(name, new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 255 });
        foreach (var (name, values) in new[] { ("subType", new[] { 201, 202, 203 }), ("repairAction", new[] { 0, 1, 2, 3, 4 }) })
            Parameter(name, new OpenApiSchema { Type = JsonSchemaType.Integer, Enum = values.Select(value => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(value)!).ToList() });
        Parameter("hasNzb", new OpenApiSchema { Type = JsonSchemaType.Boolean });
        foreach (var name in new[] { "minSize", "maxSize" }) Parameter(name, new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64", Minimum = "0" }, "Inclusive byte count.");
        foreach (var name in new[] { "addedAfter", "addedBefore", "postedAfter", "postedBefore", "checkedAfter", "checkedBefore", "playedAfter", "playedBefore" })
            Parameter(name, new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64" }, "Integer Unix seconds; After inclusive, Before exclusive. Added bounds use server-local wall clock.");
        Parameter("offset", new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "0", Default = System.Text.Json.Nodes.JsonValue.Create(0) });
        Parameter("limit", new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "200", Default = System.Text.Json.Nodes.JsonValue.Create(100) });
        Choice("sort", "name", "name", "size", "added", "posted", "last-check", "next-check", "type", "health");
        Choice("direction", "asc", "asc", "desc");
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

    private static void ApplyMissingPayloadContractOverrides(
        OpenApiOperation operation,
        string route,
        string verb)
    {
        var isExecute = verb == "post" && route == "api/remove-missing-payloads";
        var isDryRun = verb == "post" && route == "api/remove-missing-payloads/dry-run";
        var isAudit = verb == "get" && route == "api/remove-missing-payloads/audit";
        if (!isExecute && !isDryRun && !isAudit)
            return;

        if (isExecute)
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "X-InfiniDysk-Cleanup-Preview",
                In = ParameterLocation.Header,
                Required = true,
                Description = "Approval token returned by the matching dry run within its 15-minute lifetime.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        operation.Responses ??= [];
        operation.Responses["200"] = new OpenApiResponse
        {
            Description = "Success.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = MissingPayloadSuccessSchema(isDryRun, isAudit),
                },
            },
        };
    }

    private static OpenApiSchema MissingPayloadSuccessSchema(bool includePreviewToken, bool isAudit)
    {
        var properties = new Dictionary<string, IOpenApiSchema>
        {
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["error"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String | JsonSchemaType.Null,
            },
        };
        if (isAudit)
        {
            properties["report"] = new OpenApiSchema { Type = JsonSchemaType.String };
            return new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = properties,
                Required = new HashSet<string> { "status", "report" },
            };
        }

        properties["message"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
        };
        var required = new HashSet<string> { "status", "message" };
        if (includePreviewToken)
        {
            properties["previewToken"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String | JsonSchemaType.Null,
            };
            required.Add("previewToken");
        }

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = properties,
            Required = required,
        };
    }

    private static void ApplyGcDiagnosticsContractOverrides(
        OpenApiOperation operation,
        string route,
        string verb)
    {
        if (verb != "post" || route != "api/gc-diagnostics")
            return;

        operation.Responses ??= [];
        operation.Responses["429"] = new OpenApiResponse
        {
            Description =
                "Too many requests. Concurrent execution and the 10-minute cooldown both return 429. " +
                "Retry-After is a non-negative integer number of seconds. Cooldown rejections always " +
                "include it; concurrent rejections include it when a remaining wait is known.",
            Headers = new Dictionary<string, IOpenApiHeader>
            {
                ["Retry-After"] = new OpenApiHeader
                {
                    Description = "Non-negative integer number of seconds to wait before retrying.",
                    Required = false,
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Integer,
                        Minimum = "0",
                    },
                },
            },
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
            "api/recheck-file" or "api/search-file-in-arr" => ["davItemId"],
            "api/delete-webdav-item" => ["path", "expectedDavItemId", "healthCheckResultId"],
            "api/search-indexers" => ["q", "limit"],
            "api/test-usenet-connection" =>
                ["host", "user", "pass", "port", "use-ssl", "skip-tls-verification"],
            "api/test-arr-connection" => ["host", "apiKey"],
            "api/test-indexer-connection" =>
                ["url", "apiKey", "userAgent", "proxyUrl", "timeoutSeconds", "skipTlsVerification"],
            "api/test-prowlarr-connection" => ["url", "apiKey"],
            "api/test-rclone-connection" => ["host", "user", "pass"],
            "api/setup-wizard/complete" => ["strategy", "ingestionMethods", "config"],
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
            additionalProperties: false,
            requiredProperties: route switch
            {
                "api/setup-wizard/complete" or "api/recheck-file" or "api/search-file-in-arr" => fields,
                "api/delete-webdav-item" => ["path"],
                _ => null,
            });
    }

    private static OpenApiRequestBody FormBody(
        string description,
        Dictionary<string, IOpenApiSchema>? properties,
        bool additionalProperties,
        IEnumerable<string>? requiredProperties = null)
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
                        Required = requiredProperties?.ToHashSet(StringComparer.Ordinal),
                        AdditionalPropertiesAllowed = additionalProperties,
                        AdditionalProperties = additionalProperties
                            ? new OpenApiSchema { Type = JsonSchemaType.String }
                            : null,
                    },
                },
            },
        };
    }
}
