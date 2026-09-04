using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;

namespace NzbWebDAV.Api.OpenApi;

internal sealed class AdminOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AdminOpenApiDocumentTransformer(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Title = "InfiniDysk Admin API";
        document.Info.Version = AdminApiContractCatalog.ContractVersion;
        document.Info.Description =
            "Contributor-facing reference for the InfiniDysk admin REST API. "
            + "SABnzbd compatibility, WebDAV, streaming, adapters, and file-transfer routes are intentionally excluded.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = ParameterLocation.Header,
            Description = "Admin API key. Use FRONTEND_BACKEND_API_KEY for local development.",
        };

        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas["ProblemDetails"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description =
                "RFC 7807 problem document. Failures also return the X-Correlation-ID header, " +
                "which matches the traceId property and Serilog TraceId log context.",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["errors"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalProperties = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Array,
                        Items = new OpenApiSchema { Type = JsonSchemaType.String },
                    },
                },
            },
            Required = new HashSet<string> { "type", "title", "status", "detail", "traceId" },
        };

        document.Components.Schemas["TriggerHealthCheckResponse"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "Accepted response for POST /api/trigger-health-check.",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
                ["error"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["queuedCount"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                ["alreadyRunning"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            },
            Required = new HashSet<string> { "status", "queuedCount", "alreadyRunning" },
        };

        document.Components.Schemas["RequeueActionNeededHealthChecksResponse"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "Success response for POST /api/requeue-action-needed-health-checks.",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
                ["error"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["requeuedCount"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
            },
            Required = new HashSet<string> { "status", "requeuedCount" },
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("ApiKey", document)] = [],
        });

        // The document is served through a frontend proxy that can be mounted at
        // NZBDAV_URL_BASE. An empty server URL makes Scalar target the origin and
        // path from which the reference was loaded instead of an internal backend URL.
        var forwardedPrefix = _httpContextAccessor.HttpContext?
            .Request.Headers["X-Forwarded-Prefix"]
            .FirstOrDefault();
        document.Servers =
        [
            new OpenApiServer
            {
                Url = string.IsNullOrWhiteSpace(forwardedPrefix)
                    ? ""
                    : forwardedPrefix.TrimEnd('/') + "/",
            },
        ];
        CollapseNonCanonicalVerbs(document);
        return Task.CompletedTask;
    }

    private static void CollapseNonCanonicalVerbs(OpenApiDocument document)
    {
        if (document.Paths is null) return;
        foreach (var (path, item) in document.Paths)
        {
            var canonical = AdminApiContractCatalog.CanonicalMethods(path);
            if (canonical.Count == 0 || item.Operations is null || item.Operations.Count <= 1)
                continue;

            foreach (var method in item.Operations.Keys.ToList())
            {
                if (!canonical.Contains(method.Method))
                    item.Operations.Remove(method);
            }
        }
    }
}
