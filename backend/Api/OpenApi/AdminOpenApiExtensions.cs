using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.UsenetMigration;

namespace NzbWebDAV.Api.OpenApi;

internal static class AdminOpenApiExtensions
{
    internal const string DocumentName = "admin";

    private static readonly HashSet<string> ExcludedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "api/db.sqlite",
        "api/db-backup-download",
        "api/db-backup-upload",
        "api/download-logs",
        "api/download-nzb",
        "api/download-support-pack",
        "api/warden-export",
        "api/warden-import",
        "api/warden-sources-export",
        "api/warden-sources-import",
    };

    internal static bool IsEnabled(IHostEnvironment environment)
    {
        var configured = Utils.EnvironmentUtil.GetEnvironmentVariable("ENABLE_API_DOCS");
        return configured is null
            ? environment.IsDevelopment()
            : Utils.EnvironmentUtil.IsVariableTrue("ENABLE_API_DOCS");
    }

    internal static void Configure(OpenApiOptions options)
    {
        options.ShouldInclude = ShouldInclude;
        options.AddDocumentTransformer<AdminOpenApiDocumentTransformer>();
        options.AddOperationTransformer<AdminOpenApiOperationTransformer>();
    }

    private static bool ShouldInclude(ApiDescription description)
    {
        if (!string.Equals(description.GroupName, DocumentName, StringComparison.OrdinalIgnoreCase))
            return false;

        var relativePath = description.RelativePath?.TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath) || ExcludedPaths.Contains(relativePath))
            return false;

        return description.ActionDescriptor is ControllerActionDescriptor descriptor
            && (typeof(BaseApiController).IsAssignableFrom(descriptor.ControllerTypeInfo)
                || typeof(UsenetMigrationBaseController).IsAssignableFrom(descriptor.ControllerTypeInfo));
    }
}
