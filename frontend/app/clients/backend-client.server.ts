import { z } from "zod";
import { adminApi } from "~/clients/admin-operations";
import { toStreamTracingStatus, type StreamTracingStatus } from "~/utils/stream-tracing-status";

export type { StreamTracingStatus };

export type ListOptions = {
  search?: string | undefined;
  category?: string | undefined;
  status?: string | undefined;
  sort?: string | undefined;
  direction?: "asc" | "desc" | undefined;
};

function listParams(
  mode: "queue" | "history",
  limit: number,
  start: number,
  options: ListOptions,
  limitKey: "limit" | "pageSize",
): string {
  const params = new URLSearchParams({ mode, start: String(start), [limitKey]: String(limit) });
  if (options.search) params.set("search", options.search);
  if (options.category) params.set("cat", options.category);
  if (options.status) params.set("status", options.status);
  if (options.sort) params.set("sort", options.sort);
  if (options.direction) params.set("dir", options.direction);
  return params.toString();
}

export class WebdavDirectoryNotFoundError extends Error {
  public constructor(public readonly directory: string) {
    super("The WebDAV directory does not exist.");
    this.name = "WebdavDirectoryNotFoundError";
  }
}

/** Thrown when the backend is unreachable or still in the migration handoff. */
export class BackendUnavailableError extends Error {
  public constructor(
    message = "Backend temporarily unavailable",
    public readonly code?: string,
    options?: ErrorOptions,
  ) {
    super(message, options);
    this.name = "BackendUnavailableError";
  }
}

/** Structured failure from RFC 7807 ProblemDetails, SAB nested problems, or legacy JSON. */
export class BackendApiError extends Error {
  public constructor(
    message: string,
    public readonly status: number,
    public readonly title: string,
    public readonly detail: string,
    public readonly traceId?: string,
    public readonly fieldErrors?: Record<string, string[]>,
    options?: ErrorOptions,
  ) {
    super(message, options);
    this.name = "BackendApiError";
  }
}

/** Thrown when a 2xx backend body does not match the expected runtime schema. */
export class BackendContractError extends Error {
  public constructor(message: string, options?: ErrorOptions) {
    super(message, options);
    this.name = "BackendContractError";
  }
}

const backendObject: z.ZodType<Record<string, unknown>> = z.looseObject({});

export function parseBackendSuccess<T>(
  errorPrefix: string,
  json: unknown,
  schema: z.ZodType<T>,
): T {
  const result = schema.safeParse(json);
  if (!result.success) {
    throw new BackendContractError(
      `${errorPrefix}: backend response did not match the expected contract`,
    );
  }
  return result.data;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function asString(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function asFieldErrors(value: unknown): Record<string, string[]> | undefined {
  const record = asRecord(value);
  if (!record) return undefined;
  const errors: Record<string, string[]> = {};
  for (const [key, raw] of Object.entries(record)) {
    if (!Array.isArray(raw) || !raw.every((item) => typeof item === "string")) continue;
    errors[key] = raw;
  }
  return Object.keys(errors).length > 0 ? errors : undefined;
}

function stripMarkup(value: string): string {
  return value
    .replace(/<[^>]+>/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

export function parseBackendFailure(
  errorPrefix: string,
  status: number,
  body: unknown,
  correlationHeader?: string | null,
): BackendApiError {
  const record = asRecord(body);
  const nested = record ? asRecord(record["problem"]) : null;
  const problem = nested ?? (record && typeof record["status"] === "number" ? record : null);

  if (problem) {
    const detail =
      asString(problem["detail"]) ??
      asString(record?.["error"]) ??
      asString(problem["title"]) ??
      `HTTP ${status}`;
    const title = asString(problem["title"]) ?? "Request failed";
    const traceId = asString(problem["traceId"]) ?? correlationHeader ?? undefined;
    const fieldErrors = asFieldErrors(problem["errors"]);
    const suffix = traceId ? `${detail} (trace ${traceId})` : detail;
    return new BackendApiError(
      `${errorPrefix}: ${suffix}`,
      typeof problem["status"] === "number" ? problem["status"] : status,
      title,
      detail,
      traceId,
      fieldErrors,
    );
  }

  if (record && asString(record["error"])) {
    const detail = asString(record["error"])!;
    return new BackendApiError(
      `${errorPrefix}: ${detail}`,
      status,
      "Request failed",
      detail,
      correlationHeader ?? undefined,
    );
  }

  if (typeof body === "string" && body.trim().length > 0) {
    const detail = stripMarkup(body) || `HTTP ${status}`;
    return new BackendApiError(
      `${errorPrefix}: ${detail}`,
      status,
      "Request failed",
      detail,
      correlationHeader ?? undefined,
    );
  }

  return new BackendApiError(
    `${errorPrefix}: HTTP ${status}`,
    status,
    "Request failed",
    `HTTP ${status}`,
    correlationHeader ?? undefined,
  );
}

async function readFailureBody(response: Response): Promise<unknown> {
  const raw = await response.text();
  if (raw.length === 0) return null;
  try {
    return JSON.parse(raw) as unknown;
  } catch {
    return raw;
  }
}

/** Walks cause/AggregateError chains for undici / Node network failure codes. */
function extractNetworkErrorCode(error: unknown): string | undefined {
  const candidates: unknown[] = [error];
  if (error && typeof error === "object") {
    const withCause = error as { cause?: unknown; errors?: unknown[] };
    if (withCause.cause) candidates.push(withCause.cause);
    if (Array.isArray(withCause.errors)) candidates.push(...withCause.errors);
    // One more level: TypeError("fetch failed") → AggregateError → ECONNREFUSED
    if (withCause.cause && typeof withCause.cause === "object") {
      const nested = withCause.cause as { cause?: unknown; errors?: unknown[] };
      if (nested.cause) candidates.push(nested.cause);
      if (Array.isArray(nested.errors)) candidates.push(...nested.errors);
    }
  }

  for (const candidate of candidates) {
    if (!candidate || typeof candidate !== "object") continue;
    const code = (candidate as { code?: string }).code;
    if (typeof code === "string" && code.length > 0) return code;
  }
  return undefined;
}

/** Builds a FormData body from a list of [name, value] entries. */
function form(...entries: [string, string | Blob, string?][]): FormData {
  const data = new FormData();
  for (const [name, value, filename] of entries) {
    if (filename !== undefined) data.append(name, value as Blob, filename);
    else data.append(name, value);
  }
  return data;
}

/**
 * Single entry point for every backend call: prepends BACKEND_URL, attaches the
 * shared api key, and converts a non-2xx response into an Error whose message is
 * prefixed with `errorPrefix` and suffixed with the backend's reported error.
 */
async function call<T = Record<string, unknown>>(
  path: string,
  errorPrefix: string,
  init?: RequestInit,
  schema: z.ZodType<T> = backendObject as z.ZodType<T>,
): Promise<T> {
  let response: Response;
  try {
    response = await fetch(process.env["BACKEND_URL"] + path, {
      ...init,
      headers: {
        "x-api-key": process.env["FRONTEND_BACKEND_API_KEY"] || "",
        ...(init?.headers ?? {}),
      },
    });
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    const code = extractNetworkErrorCode(error);
    throw new BackendUnavailableError(
      `${errorPrefix}: ${detail}${code ? ` (${code})` : ""}`,
      code,
      { cause: error },
    );
  }

  if (!response.ok) {
    const body = await readFailureBody(response);
    const migrating = asRecord(body)?.["status"] === "migrating";
    if (response.status === 503 || migrating) {
      throw new BackendUnavailableError(
        `${errorPrefix}: backend is starting or migrating`,
        "MIGRATING",
      );
    }

    throw parseBackendFailure(
      errorPrefix,
      response.status,
      body,
      response.headers.get("x-correlation-id"),
    );
  }

  const json: unknown = await response.json();
  return parseBackendSuccess(errorPrefix, json, schema);
}

class BackendClient {
  public async isOnboarding(): Promise<boolean> {
    const data = await call<{ isOnboarding: boolean }>(
      adminApi.isOnboarding,
      "Failed to fetch onboarding status",
      {
        method: "GET",
        headers: { "Content-Type": "application/json" },
      },
    );
    return data.isOnboarding;
  }

  public async createAccount(username: string, password: string): Promise<boolean> {
    const data = await call<{ status: boolean }>(
      adminApi.createAccount,
      "Failed to create account",
      {
        method: "POST",
        body: form(["username", username], ["password", password], ["type", "admin"]),
      },
    );
    return data.status;
  }

  public async authenticate(username: string, password: string): Promise<boolean> {
    const data = await call<{ authenticated: boolean }>(
      adminApi.authenticate,
      "Failed to authenticate",
      {
        method: "POST",
        body: form(["username", username], ["password", password], ["type", "admin"]),
      },
    );
    return data.authenticated;
  }

  public async getQueue(
    limit: number,
    start: number = 0,
    options: ListOptions = {},
  ): Promise<QueueResponse> {
    const params = listParams("queue", limit, start, options, "limit");
    const data = await call<{ queue: QueueResponse }>(`/api?${params}`, "Failed to get queue");
    return data.queue;
  }

  public async getHistory(
    limit: number,
    start: number = 0,
    options: ListOptions = {},
  ): Promise<HistoryResponse> {
    const params = listParams("history", limit, start, options, "pageSize");
    const data = await call<{ history: HistoryResponse }>(
      `/api?${params}`,
      "Failed to get history",
    );
    return data.history;
  }

  public async addNzb(nzbFile: File): Promise<string> {
    const config = await this.getConfig(["api.manual-category"]);
    const category =
      config.find((item) => item.configName === "api.manual-category")?.configValue ||
      "uncategorized";
    const params = new URLSearchParams({
      mode: "addfile",
      cat: category,
      priority: "0",
      pp: "0",
    });
    const data = await call<{ nzo_ids?: string[] }>(
      `/api?${params.toString()}`,
      "Failed to add nzb file",
      {
        method: "POST",
        body: form(["nzbFile", nzbFile, nzbFile.name]),
      },
    );
    if (!data.nzo_ids || data.nzo_ids.length != 1) {
      throw new Error(`Failed to add nzb file: unexpected response format`);
    }
    return data.nzo_ids[0]!;
  }

  public async searchIndexers(q: string, limit: number = 100): Promise<SearchIndexersResponse> {
    return await call<SearchIndexersResponse>(
      adminApi.searchIndexers,
      "Failed to search indexers",
      {
        method: "POST",
        body: form(["q", q], ["limit", String(limit)]),
      },
    );
  }

  public async addNzbFromUrl(nzbUrl: string, nzbName: string): Promise<string> {
    const config = await this.getConfig(["api.manual-category"]);
    const category =
      config.find((item) => item.configName === "api.manual-category")?.configValue ||
      "uncategorized";
    const params = new URLSearchParams({
      mode: "addurl",
      cat: category,
      priority: "0",
      pp: "0",
      name: nzbUrl,
      nzbname: nzbName,
    });
    const data = await call<{ nzo_ids?: string[] }>(
      `/api?${params.toString()}`,
      "Failed to add nzb url",
      {
        method: "POST",
      },
    );
    if (!data.nzo_ids || data.nzo_ids.length !== 1) {
      throw new Error("Failed to add nzb url: unexpected response format");
    }
    return data.nzo_ids[0]!;
  }

  public async listWebdavDirectory(directory: string): Promise<DirectoryItem[]> {
    try {
      const data = await call<{ items: DirectoryItem[] }>(
        adminApi.listWebdavDirectory,
        "Failed to list webdav directory",
        {
          method: "POST",
          body: form(["directory", directory]),
        },
      );
      return data.items;
    } catch (error) {
      if (error instanceof Error && error.message.endsWith(": The directory does not exist.")) {
        throw new WebdavDirectoryNotFoundError(directory);
      }
      throw error;
    }
  }

  public async getConfig(keys: string[]): Promise<ConfigItem[]> {
    const data = await call<{ configItems?: ConfigItem[] }>(
      adminApi.getConfig,
      "Failed to get config items",
      {
        method: "POST",
        body: form(...keys.map((key) => ["config-keys", key] as [string, string])),
      },
    );
    return data.configItems || [];
  }

  public async updateConfig(configItems: ConfigItem[]): Promise<boolean> {
    const data = await call<{ status: boolean }>(
      adminApi.updateConfig,
      "Failed to update config items",
      {
        method: "POST",
        body: form(
          ...configItems.map((item) => [item.configName, item.configValue] as [string, string]),
        ),
      },
    );
    return data.status;
  }

  public async getHealthCheckQueue(pageSize?: number): Promise<HealthCheckQueueResponse> {
    const query = pageSize !== undefined ? `?pageSize=${pageSize}` : "";
    return await call<HealthCheckQueueResponse>(
      `${adminApi.getHealthCheckQueue}${query}`,
      "Failed to get health check queue",
      {
        method: "GET",
      },
    );
  }

  public async getWatchdogEntries(limit: number = 200): Promise<WatchdogEntry[]> {
    const data = await call<{ entries?: WatchdogEntry[] }>(
      `${adminApi.getWatchdogEntries}?limit=${limit}`,
      "Failed to get watchdog entries",
      {
        method: "GET",
      },
    );
    return data.entries ?? [];
  }

  public async getExcludeSyncStatus(): Promise<ExcludeSyncUrlStatus[]> {
    const data = await call<{ urls?: ExcludeSyncUrlStatus[] }>(
      adminApi.excludeSync,
      "Failed to get exclude-sync status",
      {
        method: "GET",
      },
    );
    return data.urls || [];
  }

  public async refreshExcludeSync(): Promise<ExcludeSyncUrlStatus[]> {
    const data = await call<{ urls?: ExcludeSyncUrlStatus[] }>(
      adminApi.excludeSync,
      "Failed to refresh exclude-sync",
      {
        method: "POST",
      },
    );
    return data.urls || [];
  }

  public async clearWatchdogEntries(): Promise<number> {
    const data = await call<{ deleted?: number }>(
      adminApi.clearWatchdogEntries,
      "Failed to clear watchdog entries",
      {
        method: "POST",
      },
    );
    return data.deleted ?? 0;
  }

  public async clearHealthCheckHistory(): Promise<{
    deletedResults: number;
    deletedStats: number;
  }> {
    const data = await call<{ deletedResults?: number; deletedStats?: number }>(
      adminApi.clearHealthCheckHistory,
      "Failed to clear health-check history",
      {
        method: "POST",
      },
    );
    return {
      deletedResults: data.deletedResults ?? 0,
      deletedStats: data.deletedStats ?? 0,
    };
  }

  public async clearOverviewStats(providerId?: string): Promise<number> {
    const query = providerId ? `?provider=${encodeURIComponent(providerId)}` : "";
    const data = await call<{ deletedRows?: number }>(
      `${adminApi.clearOverviewStats}${query}`,
      "Failed to clear overview statistics",
      {
        method: "POST",
      },
    );
    return data.deletedRows ?? 0;
  }

  public async getHealthCheckHistory(
    params: GetHealthCheckHistoryParams = {},
  ): Promise<HealthCheckHistoryResponse> {
    const qs = new URLSearchParams();
    if (params.page !== undefined) qs.set("page", String(params.page));
    if (params.pageSize !== undefined) qs.set("pageSize", String(params.pageSize));
    if (params.repairStatus) qs.set("repairStatus", params.repairStatus);
    if (params.result) qs.set("result", params.result);
    const query = qs.toString();
    return await call<HealthCheckHistoryResponse>(
      `${adminApi.getHealthCheckHistory}${query ? `?${query}` : ""}`,
      "Failed to get health check history",
      {
        method: "GET",
      },
    );
  }

  public async getOverviewStats(
    window: OverviewWindow = "24h",
    sections: OverviewSections = "all",
  ): Promise<OverviewStatsResponse> {
    return await call<OverviewStatsResponse>(
      `${adminApi.getOverviewStats}?window=${window}&sections=${sections}`,
      "Failed to get overview stats",
      { method: "GET" },
    );
  }

  public async getLogs(params: GetLogsParams = {}): Promise<GetLogsResponse> {
    const qs = new URLSearchParams();
    if (params.limit !== undefined) qs.set("limit", String(params.limit));
    if (params.levels && params.levels.length > 0) qs.set("levels", params.levels.join(","));
    if (params.source) qs.set("source", params.source);
    if (params.search) qs.set("search", params.search);
    if (params.beforeSequence !== undefined)
      qs.set("beforeSequence", String(params.beforeSequence));
    const query = qs.toString();
    return await call<GetLogsResponse>(
      `${adminApi.getLogs}${query ? `?${query}` : ""}`,
      "Failed to get logs",
      {
        method: "GET",
      },
    );
  }

  public async getStreamTracingStatus(): Promise<StreamTracingStatus> {
    const data = await call<Record<string, unknown>>(
      `${adminApi.getStreamTraces}?limit=1`,
      "Failed to get stream tracing status",
      {
        method: "GET",
      },
    );
    return toStreamTracingStatus(data);
  }

  public async setStreamTracing(
    enabled: boolean,
    minutes: number = 30,
    capacity: number = 100_000,
  ): Promise<StreamTracingStatus> {
    const data = await call<Record<string, unknown>>(
      adminApi.setStreamTracing,
      "Failed to update stream tracing",
      {
        method: "POST",
        body: form(
          ["enabled", enabled ? "true" : "false"],
          ["minutes", String(minutes)],
          ["capacity", String(capacity)],
        ),
      },
    );
    return toStreamTracingStatus(data);
  }

  public async discardStreamTraces(): Promise<StreamTracingStatus> {
    const data = await call<Record<string, unknown>>(
      adminApi.discardStreamTraces,
      "Failed to discard stream traces",
      {
        method: "POST",
      },
    );
    return toStreamTracingStatus(data);
  }

  public async getWatchtower(params: WatchtowerQuery = {}): Promise<WatchtowerData> {
    const qs = new URLSearchParams();
    if (params.state) qs.set("state", params.state);
    if (params.q) qs.set("q", params.q);
    if (params.sort) qs.set("sort", params.sort);
    if (params.offset) qs.set("offset", String(params.offset));
    if (params.limit) qs.set("limit", String(params.limit));
    if (params.expander) qs.set("expander", params.expander);
    if (params.statsOnly) qs.set("statsOnly", "1");
    const query = qs.toString();
    return await call<WatchtowerData>(
      `${adminApi.getWatchtower}${query ? `?${query}` : ""}`,
      "Failed to get watchtower",
      {
        method: "GET",
      },
    );
  }

  public async watchtowerMutate(fields: Record<string, string>): Promise<boolean> {
    const data = await call<{ status: boolean }>(
      adminApi.watchtowerMutate,
      "Watchtower action failed",
      {
        method: "POST",
        body: form(...Object.entries(fields).map(([k, v]) => [k, v] as [string, string])),
      },
    );
    return data.status;
  }

  public async discoverStremioCatalogs(manifestUrl: string): Promise<DiscoverCatalogsResponse> {
    return await call<DiscoverCatalogsResponse>(
      adminApi.discoverStremioCatalogs,
      "Failed to discover catalogs",
      {
        method: "POST",
        body: form(["url", manifestUrl]),
      },
    );
  }
}

export const backendClient = new BackendClient();

export type QueueResponse = {
  slots: QueueSlot[];
  noofslots: number;
};

export type QueueSlot = {
  index?: number;
  nzo_id: string;
  priority: string;
  filename: string;
  cat: string;
  percentage: string;
  true_percentage: string;
  status: string;
  mb: string;
  mbleft: string;
  indexer?: string | null | undefined;
  providers?: ProviderUsage[] | null | undefined;
};

export type ProviderUsage = {
  host: string;
  nickname?: string | null | undefined;
  segments: number;
};

export type HistoryResponse = {
  slots: HistorySlot[];
  noofslots: number;
};

export type HistorySlot = {
  nzo_id: string;
  nzb_name: string;
  name: string;
  category: string;
  status: string;
  bytes: number;
  storage: string;
  download_time: number;
  completed: number;
  fail_message: string;
  nzb_blob_id?: string;
  indexer?: string | null | undefined;
  providers?: ProviderUsage[] | null | undefined;
};

export type WatchdogOutcome =
  | "PreVerifyAvailable"
  | "PreVerifyDead"
  | "PreVerifyTimeout"
  | "Cancelled"
  | "EnqueueFailed"
  | "QueueFailed"
  | "QueueCompleted"
  | "BudgetTimeout"
  | "ExcludedByPattern";

export type WatchdogEntry = {
  clickId: string;
  attemptedAtUnix: number;
  contentType: string;
  requestedTitle: string;
  candidateTitle: string;
  indexerName: string;
  size: number;
  rankIndex: number;
  outcome: WatchdogOutcome;
  failReason: string | null;
  durationMs: number;
  isWinner: boolean;
  providerHost?: string | null | undefined;
  providerNickname?: string | null | undefined;
};

export type DirectoryItem = {
  name: string;
  isDirectory: boolean;
  size: number | null | undefined;
  nzbBlobId?: string;
};

export type ConfigItem = {
  configName: string;
  configValue: string;
  /** Present when the setting is owned by an NZBDAV_CONFIG__... environment variable. */
  environmentVariableName?: string | null | undefined;
};

export type ExcludeSyncUrlStatus = {
  url: string;
  count: number;
  fetchedAt: number | null;
  lastChecked: number | null;
  error: string | null;
};

export type WatchtowerQuery = {
  state?: string;
  q?: string;
  sort?: string;
  offset?: number;
  limit?: number;
  expander?: string;
  statsOnly?: boolean;
};

export type WatchtowerData = {
  status: boolean;
  enabled: boolean;
  sources: WatchtowerSource[];
  items: WatchtowerItem[];
  shows: WatchtowerItem[];
  total: number;
  hasMore: boolean;
  stats: WatchtowerStats;
};

export type WatchtowerSource = {
  id: string;
  kind: string;
  name: string;
  url?: string | null | undefined;
  enabled: boolean;
  cap: number;
  seriesScope?: string | null | undefined;
  lastSyncedAtUnix?: number | null | undefined;
  lastSyncError?: string | null | undefined;
};

export type WatchtowerItem = {
  key: string;
  type: string;
  contentId: string;
  title: string;
  state: string;
  provenanceCount: number;
  expanderKey?: string | null | undefined;
  childTotal?: number | null | undefined;
  childReady?: number | null | undefined;
  childUnavailable?: number | null | undefined;
  shortlistCount: number;
  winnerTitle?: string | null | undefined;
  winnerSize: number;
  lastVerifiedAtUnix?: number | null | undefined;
  nextCheckAtUnix?: number | null | undefined;
  failReason?: string | null | undefined;
};

export type WatchtowerStats = {
  total: number;
  ready: number;
  scouting: number;
  unavailable: number;
  parked: number;
  expanders: number;
};

export type DiscoveredCatalog = {
  type: string;
  id: string;
  name: string;
  url: string;
  extraRequired?: string | null | undefined;
};

export type DiscoverCatalogsResponse = {
  status: boolean;
  error?: string;
  addonName?: string | null | undefined;
  catalogs: DiscoveredCatalog[];
};

export type SearchIndexersResponse = {
  status: boolean;
  error?: string;
  results: SearchIndexerResult[];
  indexers: IndexerStatus[];
};

export type SearchIndexerResult = {
  indexer: string;
  title: string;
  nzbUrl: string;
  size: number;
  posted: string | null;
};

export type IndexerStatus = {
  name: string;
  ok: boolean;
  resultCount: number;
  error: string | null;
  elapsedMs: number;
};

export type TestUsenetConnectionRequest = {
  host: string;
  port: string;
  useSsl: string;
  skipTlsVerification: string;
  user: string;
  pass: string;
};

export type HealthCheckQueueResponse = {
  uncheckedCount: number;
  items: HealthCheckQueueItem[];
};

export type HealthCheckQueueItem = {
  id: string;
  name: string;
  path: string;
  releaseDate: string | null;
  lastHealthCheck: string | null;
  nextHealthCheck: string | null;
  progress: number;
};

export type HealthCheckHistoryResponse = {
  stats: HealthCheckStats[];
  items: HealthCheckResult[];
  totalCount: number;
};

export type GetHealthCheckHistoryParams = {
  page?: number;
  pageSize?: number;
  repairStatus?: string;
  result?: string;
};

export type HealthCheckStats = {
  result: HealthResult;
  repairStatus: RepairAction;
  count: number;
};

export type HealthCheckResult = {
  id: string;
  createdAt: string;
  davItemId: string;
  path: string;
  nzbFileName: string | null;
  jobName: string | null;
  result: HealthResult;
  repairStatus: RepairAction;
  message: string | null;
};

export enum HealthResult {
  Healthy = 0,
  Unhealthy = 1,
  Degraded = 2,
}

export enum RepairAction {
  None = 0,
  Repaired = 1,
  Deleted = 2,
  ActionNeeded = 3,
  RepairedViaPar2 = 4,
}

export type OverviewWindow = "1h" | "24h" | "7d" | "30d" | "all";
export type OverviewSections = "all" | "window" | "detail" | "static" | (string & {});

export type OverviewStatsResponse = {
  window: OverviewWindow;
  includedSections?: string[];
  tiles: {
    activeReads: number;
    articlesPerMinute: number;
    errorsPerMinute: number;
    bytesServedPerMinute: number;
    inFlightArticleBytes?: number;
    inFlightArticleBudgetBytes?: number;
    inFlightArticleThrottleEvents?: number;
  };
  throughput: ThroughputPoint[];
  throughputBucketSizeMs: number;
  totalArticles: number;
  totalMisses: number;
  totalErrors: number;
  totalBytesFetched: number;
  providers: ProviderRow[];
  catalogue: {
    fileCount: number;
    totalBytes: number;
    largestFileBytes: number;
    addedLast7Days: number;
  };
  sessions: {
    count: number;
    totalBytesServed: number;
    avgDurationMs: number;
    longestDurationMs: number;
    biggestReadBytes: number;
  };
  heatmap: {
    maxCell: number;
    mode: HeatmapMode;
    windowStartMs: number;
    windowEndMs: number;
    bucketSizeMs: number;
    cells: HeatmapCell[];
  };
  latency: {
    p50Ms: number;
    p95Ms: number;
    p99Ms: number;
    samples: number;
    buckets: LatencyBucket[];
  };
  errors: ErrorSlice[];
  indexers: IndexerRow[];
  indexerApiUsage: IndexerApiUsageRow[];
  lifetime: {
    bytesFetched: number;
    bytesRead: number;
    articles: number;
    readSessions: number;
    readSeconds: number;
    firstSeenAt: number | null;
  };
  records: {
    bestDayBytes: number;
    bestDayAt: number | null;
    bestHourBytes: number;
    bestHourAt: number | null;
  };
  failover: FailoverBlock;
  metricsHealth: {
    queued: number;
    dropped: number;
    lastSuccessfulFlushAtMs: number;
    lastFlushError: string | null;
  };
};

export type ArrInstanceStatus = "pending" | "healthy" | "degraded" | "offline";

export type ArrHealthSummary = {
  instancesOnline: number;
  instancesTotal: number;
  importsCompleted: number;
  medianHandoffMs: number | null;
  p95HandoffMs: number | null;
  awaitingImport: number;
  awaitingShown: number;
  degraded: number;
};

export type ArrHealthInstanceRow = {
  key: string;
  name: string;
  appType: string;
  host: string;
  status: ArrInstanceStatus;
  imports: number;
  medianHandoffMs: number | null;
  p95HandoffMs: number | null;
  queueCount: number;
  awaitingCount: number;
  hasWarnings: boolean;
  hasErrors: boolean;
  lastImportAtMs: number | null;
  lastError: string | null;
};

export type ArrAwaitingItem = {
  title: string | null;
  downloadId: string | null;
  instanceKey: string;
  instanceName: string;
  waitingMs: number | null;
  isUnusual: boolean;
  trackedDownloadState: string | null;
  statusReason: string | null;
};

export type ArrHealthResponse = {
  configured: boolean;
  summary: ArrHealthSummary;
  instances: ArrHealthInstanceRow[];
  awaiting: ArrAwaitingItem[];
};

export type FailoverBlock = {
  articlesRecovered: number;
  previousArticlesRecovered: number | null;
  segmentsCovered: number;
  readsSaved: number;
  readSessions: number;
  totalArticles: number;
  bucketSizeMs: number;
  rescuedBy: FailoverProvider[];
  rescuedFrom: FailoverFrom[];
  reasons: FailoverReason[];
  buckets: FailoverBucket[];
};

export type FailoverProvider = {
  provider: string;
  nickname?: string | null | undefined;
  saves: number;
};

export type FailoverFrom = {
  provider: string;
  nickname?: string | null | undefined;
  misses: number;
};

export type FailoverReason = {
  status: string;
  count: number;
};

export type FailoverBucket = {
  bucket: number;
  counts: number[];
};

export type ThroughputPoint = {
  bucket: number;
  articles: number;
  misses: number;
  errors: number;
  bytesServed: number;
  bytesFetched: number;
};

export type ProviderCircuitState = "closed" | "open" | "halfOpen";

export type ProviderRow = {
  provider: string;
  nickname?: string | null | undefined;
  articles: number;
  bytesFetched: number;
  errors: number;
  retries: number;
  speedMbPerSec?: number | null | undefined;
  speedSpark?: number[];
  avgDurationMs: number;
  errorRate: number;
  spark: number[];
  errorSpark?: number[];
  retrySpark?: number[];
  outageSpark?: number[];
  circuitState?: ProviderCircuitState;
  cooldownRemainingSeconds?: number | null | undefined;
  lastFailureReason?: string | null | undefined;
  tripCount?: number | undefined;
  failureCount?: number | undefined;
  articleMissCount?: number | undefined;
};

export type ProviderCircuitBreakerRow = {
  provider: string;
  nickname?: string | null | undefined;
  providerType?: string;
  circuitState: ProviderCircuitState;
  cooldownRemainingSeconds?: number | null | undefined;
  lastFailureReason?: string | null | undefined;
  tripCount?: number;
  failureCount?: number;
  articleMissCount?: number;
};

export type HeatmapMode = "day" | "week" | "month" | "year";

export type HeatmapCell = {
  bucket: number;
  count: number;
};

export type LatencyBucket = {
  loMs: number;
  hiMs: number;
  count: number;
};

export type ErrorSlice = {
  status: string;
  count: number;
};

export type IndexerRow = {
  name: string;
  completed: number;
  failed: number;
  bytesCompleted: number;
  avgSeconds: number;
  successRate: number;
};

export type IndexerApiUsageRow = {
  name: string;
  apiHits: number;
  apiHitLimit: number | null;
  downloadHits: number;
  downloadHitLimit: number | null;
  resetAtMs: number;
  resetHourUtc: number | null;
};

export type ActiveReadsMessage = {
  reads: ActiveRead[];
};

export type ActiveRead = {
  id: string;
  fileName: string;
  path: string;
  startedAt: number;
  lastActivityAt: number;
  bytesRead: number;
  bytesFetched?: number;
  currentOffset: number;
  fileSize: number | null;
  clientIp?: string | null | undefined;
  clientUserAgent?: string | null | undefined;
  playerSession?: string | null | undefined;
  providers: { host: string; nickname?: string | null | undefined; segments: number }[];
};

export type LiveStatsMessage = {
  activeReads: number;
  articlesPerMinute: number;
  errorsPerMinute: number;
  bytesServedPerMinute: number;
  inFlightArticleBytes?: number;
  inFlightArticleBudgetBytes?: number;
  inFlightArticleThrottleEvents?: number;
  ts: number;
  providerBreakers?: ProviderCircuitBreakerRow[];
};

export type LogLevel = "Verbose" | "Debug" | "Information" | "Warning" | "Error" | "Fatal";

export type LogEntry = {
  seq: number;
  ts: number;
  level: LogLevel;
  msg: string;
  source: string | null;
  exception: string | null;
  traceId?: string | null;
};

export type GetLogsParams = {
  limit?: number;
  levels?: LogLevel[];
  source?: string;
  search?: string;
  beforeSequence?: number;
};

export type GetLogsResponse = {
  status: boolean;
  error?: string;
  entries: LogEntry[];
  countsByLevel: Record<string, number>;
  oldestSequence: number;
  newestSequence: number;
  capacity: number;
};

export type LogBroadcastMessage = {
  entries: LogEntry[];
};
