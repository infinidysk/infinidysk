import type { paths } from "~/generated/admin-api";

export type AdminApiPath = keyof paths;
export type AdminHttpMethod = "get" | "post";

export type AdminFrontendOperation = {
  method: AdminHttpMethod;
  path: AdminApiPath;
  operationId: string;
};

/**
 * Admin REST operations used by `backend-client.server.ts`.
 * SAB `/api?mode=*` calls are excluded. Compile-time `satisfies` fails if a
 * path is missing from the generated OpenAPI types.
 */
export const adminFrontendOperations = [
  { method: "get", path: "/api/is-onboarding", operationId: "get-api-is-onboarding" },
  { method: "post", path: "/api/create-account", operationId: "post-api-create-account" },
  { method: "post", path: "/api/authenticate", operationId: "post-api-authenticate" },
  { method: "post", path: "/api/search-indexers", operationId: "post-api-search-indexers" },
  { method: "post", path: "/api/list-webdav-directory", operationId: "post-api-list-webdav-directory" },
  { method: "post", path: "/api/get-config", operationId: "post-api-get-config" },
  { method: "post", path: "/api/update-config", operationId: "post-api-update-config" },
  { method: "get", path: "/api/get-health-check-queue", operationId: "get-api-get-health-check-queue" },
  { method: "get", path: "/api/get-watchdog-entries", operationId: "get-api-get-watchdog-entries" },
  { method: "get", path: "/api/exclude-sync", operationId: "get-api-exclude-sync" },
  { method: "post", path: "/api/exclude-sync", operationId: "post-api-exclude-sync" },
  { method: "post", path: "/api/clear-watchdog-entries", operationId: "post-api-clear-watchdog-entries" },
  { method: "post", path: "/api/clear-health-check-history", operationId: "post-api-clear-health-check-history" },
  { method: "post", path: "/api/clear-overview-stats", operationId: "post-api-clear-overview-stats" },
  { method: "get", path: "/api/get-health-check-history", operationId: "get-api-get-health-check-history" },
  { method: "get", path: "/api/get-overview-stats", operationId: "get-api-get-overview-stats" },
  { method: "get", path: "/api/get-logs", operationId: "get-api-get-logs" },
  { method: "get", path: "/api/get-stream-traces", operationId: "get-api-get-stream-traces" },
  { method: "post", path: "/api/set-stream-tracing", operationId: "post-api-set-stream-tracing" },
  { method: "post", path: "/api/discard-stream-traces", operationId: "post-api-discard-stream-traces" },
  { method: "get", path: "/api/get-watchtower", operationId: "get-api-get-watchtower" },
  { method: "post", path: "/api/watchtower-mutate", operationId: "post-api-watchtower-mutate" },
  { method: "post", path: "/api/watchtower-discover-catalogs", operationId: "post-api-watchtower-discover-catalogs" },
] as const satisfies readonly AdminFrontendOperation[];

export const adminApi = {
  isOnboarding: "/api/is-onboarding",
  createAccount: "/api/create-account",
  authenticate: "/api/authenticate",
  searchIndexers: "/api/search-indexers",
  listWebdavDirectory: "/api/list-webdav-directory",
  getConfig: "/api/get-config",
  updateConfig: "/api/update-config",
  getHealthCheckQueue: "/api/get-health-check-queue",
  getWatchdogEntries: "/api/get-watchdog-entries",
  excludeSync: "/api/exclude-sync",
  clearWatchdogEntries: "/api/clear-watchdog-entries",
  clearHealthCheckHistory: "/api/clear-health-check-history",
  clearOverviewStats: "/api/clear-overview-stats",
  getHealthCheckHistory: "/api/get-health-check-history",
  getOverviewStats: "/api/get-overview-stats",
  getLogs: "/api/get-logs",
  getStreamTraces: "/api/get-stream-traces",
  setStreamTracing: "/api/set-stream-tracing",
  discardStreamTraces: "/api/discard-stream-traces",
  getWatchtower: "/api/get-watchtower",
  watchtowerMutate: "/api/watchtower-mutate",
  discoverStremioCatalogs: "/api/watchtower-discover-catalogs",
} as const satisfies Record<string, AdminApiPath>;
