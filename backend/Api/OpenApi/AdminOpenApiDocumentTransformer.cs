using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using NzbWebDAV.Config;

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
        document.Info.Version = ConfigManager.AppVersion;
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
            Required = new HashSet<string> { "type", "title", "status", "traceId" },
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
        return Task.CompletedTask;
    }
}
