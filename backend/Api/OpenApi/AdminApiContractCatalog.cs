namespace NzbWebDAV.Api.OpenApi;

/// <summary>
/// Admin operations used by <c>frontend/app/clients/backend-client.server.ts</c>.
/// SAB <c>/api?mode=*</c> calls are excluded. Contract version is independent of
/// the product release version.
/// </summary>
public static class AdminApiContractCatalog
{
    public const string ContractVersion = "2.0.0";
    public const string RelativeContractPath = "contracts/openapi/admin-v1.json";

    public sealed record Operation(string Method, string Path, string OperationId);

    public static IReadOnlyList<Operation> FrontendOperations { get; } =
    [
        new("GET", "/api/is-onboarding", "get-api-is-onboarding"),
        new("GET", "/api/setup-wizard-state", "get-api-setup-wizard-state"),
        new("POST", "/api/setup-wizard/complete", "post-api-setup-wizard-complete"),
        new("POST", "/api/setup-wizard/skip", "post-api-setup-wizard-skip"),
        new("POST", "/api/create-account", "post-api-create-account"),
        new("POST", "/api/authenticate", "post-api-authenticate"),
        new("POST", "/api/search-indexers", "post-api-search-indexers"),
        new("POST", "/api/list-webdav-directory", "post-api-list-webdav-directory"),
        new("POST", "/api/get-config", "post-api-get-config"),
        new("POST", "/api/update-config", "post-api-update-config"),
        new("GET", "/api/get-health-check-queue", "get-api-get-health-check-queue"),
        new("POST", "/api/trigger-health-check", "post-api-trigger-health-check"),
        new("GET", "/api/get-watchdog-entries", "get-api-get-watchdog-entries"),
        new("GET", "/api/exclude-sync", "get-api-exclude-sync"),
        new("POST", "/api/exclude-sync", "post-api-exclude-sync"),
        new("POST", "/api/clear-watchdog-entries", "post-api-clear-watchdog-entries"),
        new("POST", "/api/clear-health-check-history", "post-api-clear-health-check-history"),
        new("POST", "/api/clear-overview-stats", "post-api-clear-overview-stats"),
        new("GET", "/api/get-health-check-history", "get-api-get-health-check-history"),
        new("POST", "/api/remove-missing-payloads", "post-api-remove-missing-payloads"),
        new("POST", "/api/remove-missing-payloads/dry-run", "post-api-remove-missing-payloads-dry-run"),
        new("GET", "/api/remove-missing-payloads/audit", "get-api-remove-missing-payloads-audit"),
        new("GET", "/api/get-overview-stats", "get-api-get-overview-stats"),
        new("GET", "/api/get-logs", "get-api-get-logs"),
        new("GET", "/api/get-stream-traces", "get-api-get-stream-traces"),
        new("POST", "/api/set-stream-tracing", "post-api-set-stream-tracing"),
        new("POST", "/api/discard-stream-traces", "post-api-discard-stream-traces"),
        new("GET", "/api/get-watchtower", "get-api-get-watchtower"),
        new("POST", "/api/watchtower-mutate", "post-api-watchtower-mutate"),
        new("POST", "/api/watchtower-discover-catalogs", "post-api-watchtower-discover-catalogs"),
    ];

    public static IReadOnlySet<string> CanonicalMethods(string path)
    {
        var methods = FrontendOperations
            .Where(operation => string.Equals(operation.Path, path, StringComparison.OrdinalIgnoreCase))
            .Select(operation => operation.Method.ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return methods;
    }
}
