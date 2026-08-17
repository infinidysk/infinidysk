using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.OpenApi;

internal sealed class AdminOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
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

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("ApiKey", document)] = [],
        });

        // The document is served through a frontend proxy that can be mounted at
        // NZBDAV_URL_BASE. An empty server URL makes Scalar target the origin and
        // path from which the reference was loaded instead of an internal backend URL.
        document.Servers = [new OpenApiServer { Url = "" }];
        return Task.CompletedTask;
    }
}
