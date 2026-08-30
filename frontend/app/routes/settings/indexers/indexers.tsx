import {
  type Dispatch,
  type SetStateAction,
  useState,
  useCallback,
  useEffect,
  useMemo,
} from "react";
import {
  Alert,
  Badge,
  Button,
  HelpText,
  Icon,
  Input,
  Label,
  ManagedSetting,
  Modal,
  SettingsCard,
  SettingsIntro,
  SettingsPage,
  Spinner,
  Textarea,
  Toggle,
  Tooltip,
  useIsAnyManaged,
} from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";
import type { ExcludeSyncUrlStatus } from "~/clients/backend-client.server";

type IndexersSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
  savedConfig?: Record<string, string>;
  onSyncedConfig?: (patch: Record<string, string>) => void;
};

interface ResultFilter {
  Enabled: boolean;
  SkipPassworded: boolean;
  MinGrabs: number;
  GrabsGraceHours: number;
  MaxAgeDaysWithoutGrabs: number;
  PreferDownloaded: boolean;
}

// Optimised baseline. Used both as the initial UI state when an indexer has no Filter
// yet AND as the comparison baseline that decides whether to omit the Filter object from
// the saved JSON (so users who never touch this section keep a clean config). The master
// toggle (`Enabled`) starts off — the rest are the values that take effect the moment a
// user flips it on, without them having to think about any sub-setting.
const OPTIMISED_DEFAULTS: ResultFilter = {
  Enabled: false,
  SkipPassworded: true,
  MinGrabs: 1,
  GrabsGraceHours: 6,
  MaxAgeDaysWithoutGrabs: 0,
  PreferDownloaded: true,
};

interface ConnectionDetails {
  Name: string;
  Url: string;
  ApiKey: string;
  Enabled: boolean;
  UserAgent?: string;
  SearchUserAgent?: string;
  RetrieveUserAgent?: string;
  SkipTlsVerification?: boolean;
  MaxRequestsPerMinute?: number;
  EnableStrictMatching?: boolean;
  ProxyUrl?: string;
  TimeoutSeconds?: number;
  SearchResultLimit?: number;
  MaxResponseBytes?: number;
  HitLimit?: number;
  DownloadLimit?: number;
  HitLimitResetTime?: number;
  ExtraMovieCategories?: string;
  ExtraTvCategories?: string;
  IgnoreCategoryFilter?: boolean;
  Filter?: ResultFilter;
  ProwlarrIndexerId?: number;
}

interface IndexerConfig {
  ProxyUrl?: string;
  TimeoutSeconds?: number;
  SearchResultLimit?: number;
  MaxResponseBytes?: number;
  Indexers: ConnectionDetails[];
}

// Mirrors backend TestIndexerConnectionResponse (BaseApiResponse + Connected), camelCase JSON.
interface TestIndexerConnectionResult {
  status?: boolean;
  connected?: boolean;
}

// Hard fallback when neither the indexer nor the global override sets a timeout.
// Mirrors IndexerConfig.DefaultTimeoutSeconds in the backend.
const DEFAULT_TIMEOUT_SECONDS = 30;

// Hard fallback for results gathered per indexer per search; above this the indexer is paged.
// Mirrors IndexerConfig.DefaultSearchResultLimit in the backend.
const DEFAULT_SEARCH_RESULT_LIMIT = 100;

// Mirrors IndexerConfig.DefaultMaxResponseBytes / ExternalMetadataResponseLimits.
const DEFAULT_MAX_RESPONSE_BYTES = 4 * 1024 * 1024;
const MAX_RESPONSE_BYTES_HARD_CLAMP = 16 * 1024 * 1024;

// Mirrors ConfigManager.DefaultProwlarrSyncIntervalMinutes and validation bounds.
const DEFAULT_PROWLARR_SYNC_INTERVAL_MINUTES = 60;
const MIN_PROWLARR_SYNC_INTERVAL_MINUTES = 5;
const MAX_PROWLARR_SYNC_INTERVAL_MINUTES = 10080;

type PatternIssue = { line: number; pattern: string; error: string };

function validateExcludePatterns(raw: string): PatternIssue[] {
  const issues: PatternIssue[] = [];
  const lines = raw.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const trimmed = (lines[i] ?? "").trim();
    if (trimmed.length === 0 || trimmed.startsWith("#")) continue;
    try {
      new RegExp(trimmed, "i");
    } catch (e) {
      issues.push({
        line: i + 1,
        pattern: trimmed,
        error: e instanceof Error ? e.message : "invalid regex",
      });
    }
  }
  return issues;
}

// Response shape of /settings/exclude-sync (backend ExcludeSyncResponse).
type ExcludeSyncResponse = {
  urls?: ExcludeSyncUrlStatus[];
};

type ProwlarrSyncStatus = {
  configured?: boolean;
  syncEnabled?: boolean;
  indexersEnvironmentManaged?: boolean;
  profilesEnvironmentManaged?: boolean;
  lastAttemptAt?: number | null;
  lastSuccessAt?: number | null;
  error?: string | null;
  remoteIndexerCount?: number;
  added?: number;
  updated?: number;
  removed?: number;
  skipped?: number;
  indexerConfigJson?: string | null;
  profileConfigJson?: string | null;
};

type ProwlarrConnectionTestResult = {
  status?: boolean;
  connected?: boolean;
  error?: string | null;
};

type SyncUrlIssue = { line: number; value: string; error: string };

function validateSyncUrls(raw: string): SyncUrlIssue[] {
  const issues: SyncUrlIssue[] = [];
  const lines = raw.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const trimmed = (lines[i] ?? "").trim();
    if (trimmed.length === 0 || trimmed.startsWith("#")) continue;
    try {
      const parsed = new URL(trimmed);
      if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
        issues.push({ line: i + 1, value: trimmed, error: "must be http(s)" });
      }
    } catch {
      issues.push({ line: i + 1, value: trimmed, error: "invalid URL" });
    }
  }
  return issues;
}

function isRefreshValid(raw: string): boolean {
  const trimmed = raw.trim();
  if (trimmed === "") return true; // blank → server default (720)
  const n = Number(trimmed);
  return Number.isInteger(n) && n >= 15 && n <= 10080;
}

function isProwlarrSyncIntervalValid(raw: string): boolean {
  const trimmed = raw.trim();
  if (trimmed === "") return true; // blank → server default (60)
  const n = Number(trimmed);
  return (
    Number.isInteger(n) &&
    n >= MIN_PROWLARR_SYNC_INTERVAL_MINUTES &&
    n <= MAX_PROWLARR_SYNC_INTERVAL_MINUTES &&
    trimmed === n.toString()
  );
}

function isProwlarrUrlValid(raw: string): boolean {
  const trimmed = raw.trim();
  if (trimmed === "") return true;
  try {
    const url = new URL(trimmed);
    return (
      (url.protocol === "http:" || url.protocol === "https:") &&
      !url.username &&
      !url.password &&
      !url.search &&
      !url.hash
    );
  } catch {
    return false;
  }
}

function syncHostLabel(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

function syncRelativeTime(unixSeconds: number): string {
  const diff = Math.floor(Date.now() / 1000) - unixSeconds;
  if (diff < 60) return "just now";
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
  return `${Math.floor(diff / 86400)}d ago`;
}

function prowlarrStatusSummary(status: ProwlarrSyncStatus): string {
  if (status.indexersEnvironmentManaged) {
    return "Prowlarr sync is unavailable because indexers.instances is managed by the environment.";
  }
  if (!status.configured) return "Prowlarr is not configured.";
  if (status.error) {
    const attempted = status.lastAttemptAt ? ` ${syncRelativeTime(status.lastAttemptAt)}` : "";
    return `Last sync failed${attempted}: ${status.error}`;
  }
  if (status.lastSuccessAt) {
    const changes = [
      `${status.added ?? 0} added`,
      `${status.updated ?? 0} updated`,
      `${status.removed ?? 0} removed`,
      `${status.skipped ?? 0} skipped`,
    ].join(" · ");
    return `Last synced ${syncRelativeTime(status.lastSuccessAt)} — ${status.remoteIndexerCount ?? 0} Prowlarr indexers, ${changes}.`;
  }
  return "Prowlarr is configured but has not synced yet.";
}

function parseConfig(raw: string): IndexerConfig {
  try {
    // Config key "indexers.instances" holds the backend IndexerConfig JSON.
    const parsed = JSON.parse(raw || "{}") as Partial<IndexerConfig>;
    const config: IndexerConfig = {
      ProxyUrl: parsed.ProxyUrl ?? "",
      Indexers: parsed.Indexers ?? [],
    };
    if (typeof parsed.TimeoutSeconds === "number") {
      config.TimeoutSeconds = parsed.TimeoutSeconds;
    }
    if (typeof parsed.SearchResultLimit === "number") {
      config.SearchResultLimit = parsed.SearchResultLimit;
    }
    if (typeof parsed.MaxResponseBytes === "number") {
      config.MaxResponseBytes = parsed.MaxResponseBytes;
    }
    return config;
  } catch {
    return { ProxyUrl: "", Indexers: [] };
  }
}

function serializeConfig(c: IndexerConfig): string {
  const out: IndexerConfig = { Indexers: c.Indexers };
  if (c.ProxyUrl && c.ProxyUrl.trim()) out.ProxyUrl = c.ProxyUrl.trim();
  if (typeof c.TimeoutSeconds === "number" && c.TimeoutSeconds > 0)
    out.TimeoutSeconds = c.TimeoutSeconds;
  if (typeof c.SearchResultLimit === "number" && c.SearchResultLimit > 0)
    out.SearchResultLimit = c.SearchResultLimit;
  if (typeof c.MaxResponseBytes === "number" && c.MaxResponseBytes > 0)
    out.MaxResponseBytes = c.MaxResponseBytes;
  return JSON.stringify(out);
}

// Positive integer (or empty string = "use fallback"). Rejects decimals and negatives.
function isTimeoutValid(raw: string): boolean {
  if (!raw.trim()) return true;
  const n = Number(raw);
  return Number.isInteger(n) && n > 0 && raw.trim() === n.toString();
}

function isMaxResponseBytesValid(raw: string): boolean {
  if (!raw.trim()) return true;
  const n = Number(raw);
  return (
    Number.isInteger(n) &&
    n >= 1 &&
    n <= MAX_RESPONSE_BYTES_HARD_CLAMP &&
    raw.trim() === n.toString()
  );
}

function isCategoryListValid(raw: string): boolean {
  if (!raw.trim()) return true;
  const parts = raw
    .split(",")
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
  if (parts.length === 0) return true;
  return parts.every((p) => /^\d+$/.test(p));
}

// http://host:port, https://..., optionally with user:pass@. Empty string = no proxy.
function isProxyUrlValid(raw: string): boolean {
  if (!raw.trim()) return true;
  try {
    const u = new URL(raw);
    return (u.protocol === "http:" || u.protocol === "https:") && u.host !== "";
  } catch {
    return false;
  }
}

export function IndexersSettings({
  config,
  setNewConfig,
  savedConfig,
  onSyncedConfig,
}: IndexersSettingsProps) {
  const indexerConfig = useMemo(() => parseConfig(config["indexers.instances"] ?? ""), [config]);
  const [showModal, setShowModal] = useState(false);
  const [editingIndex, setEditingIndex] = useState<number | null>(null);

  const handleAdd = useCallback(() => {
    setEditingIndex(null);
    setShowModal(true);
  }, []);

  const handleEdit = useCallback((index: number) => {
    setEditingIndex(index);
    setShowModal(true);
  }, []);

  const handleDelete = useCallback(
    (index: number) => {
      const next: IndexerConfig = {
        ...indexerConfig,
        Indexers: indexerConfig.Indexers.filter((_, i) => i !== index),
      };
      setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    },
    [config, indexerConfig, setNewConfig],
  );

  const handleToggle = useCallback(
    (index: number) => {
      const next: IndexerConfig = {
        ...indexerConfig,
        Indexers: indexerConfig.Indexers.map((x, i) =>
          i === index ? { ...x, Enabled: !x.Enabled } : x,
        ),
      };
      setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    },
    [config, indexerConfig, setNewConfig],
  );

  const handleCloseModal = useCallback(() => {
    setShowModal(false);
    setEditingIndex(null);
  }, []);

  const handleSave = useCallback(
    (indexer: ConnectionDetails) => {
      const next: IndexerConfig = { ...indexerConfig, Indexers: [...indexerConfig.Indexers] };
      if (editingIndex !== null) {
        next.Indexers[editingIndex] = indexer;
      } else {
        next.Indexers.push(indexer);
      }
      setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
      handleCloseModal();
    },
    [config, indexerConfig, editingIndex, setNewConfig, handleCloseModal],
  );

  const handleProxyChange = useCallback(
    (value: string) => {
      const next: IndexerConfig = { ...indexerConfig, ProxyUrl: value };
      setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    },
    [config, indexerConfig, setNewConfig],
  );

  const handleTimeoutChange = useCallback(
    (value: string) => {
      const trimmed = value.replace(/[^0-9]/g, "");
      const n = trimmed === "" ? undefined : parseInt(trimmed, 10);
      const next: IndexerConfig = {
        ...indexerConfig,
        ...(n && n > 0 ? { TimeoutSeconds: n } : {}),
      };
      setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    },
    [config, indexerConfig, setNewConfig],
  );

  const handleSearchLimitChange = useCallback(
    (value: string) => {
      const trimmed = value.replace(/[^0-9]/g, "");
      const n = trimmed === "" ? undefined : parseInt(trimmed, 10);
      const next: IndexerConfig = { ...indexerConfig };
      if (n && n > 0) next.SearchResultLimit = n;
      else delete next.SearchResultLimit;
      setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    },
    [config, indexerConfig, setNewConfig],
  );

  const handleMaxResponseBytesChange = useCallback(
    (value: string) => {
      const trimmed = value.replace(/[^0-9]/g, "");
      const n = trimmed === "" ? undefined : parseInt(trimmed, 10);
      const next: IndexerConfig = { ...indexerConfig };
      if (n && n > 0) next.MaxResponseBytes = n;
      else delete next.MaxResponseBytes;
      setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    },
    [config, indexerConfig, setNewConfig],
  );

  const excludePatterns = config["search.exclude-patterns"] ?? "";
  const patternIssues = useMemo(() => validateExcludePatterns(excludePatterns), [excludePatterns]);
  const handleExcludePatternsChange = useCallback(
    (value: string) => {
      setNewConfig({ ...config, "search.exclude-patterns": value });
    },
    [config, setNewConfig],
  );

  const excludeSyncUrls = config["search.exclude-sync-urls"] ?? "";
  const excludeSyncRefresh = config["search.exclude-sync-refresh-minutes"] ?? "";
  const syncUrlIssues = useMemo(() => validateSyncUrls(excludeSyncUrls), [excludeSyncUrls]);
  const handleSyncUrlsChange = useCallback(
    (value: string) => {
      setNewConfig({ ...config, "search.exclude-sync-urls": value });
    },
    [config, setNewConfig],
  );
  const handleSyncRefreshChange = useCallback(
    (value: string) => {
      const cleaned = value.replace(/[^0-9]/g, "");
      setNewConfig({ ...config, "search.exclude-sync-refresh-minutes": cleaned });
    },
    [config, setNewConfig],
  );

  const [syncStatus, setSyncStatus] = useState<ExcludeSyncUrlStatus[]>([]);
  const [isSyncing, setIsSyncing] = useState(false);
  const loadSyncStatus = useCallback(async () => {
    try {
      const res = await fetch(withUrlBase("/settings/exclude-sync"));
      if (res.ok) setSyncStatus(((await res.json()) as ExcludeSyncResponse).urls ?? []);
    } catch {
      // status is best-effort; ignore transient failures
    }
  }, []);
  // Load on mount, and re-pull after a save changes the synced URLs. The backend
  // refetches on config change, so poll once immediately and once after it settles.
  const savedSyncUrls = savedConfig?.["search.exclude-sync-urls"] ?? "";
  useEffect(() => {
    void loadSyncStatus();
    const timer = setTimeout(() => {
      void loadSyncStatus();
    }, 2000);
    return () => clearTimeout(timer);
  }, [savedSyncUrls, loadSyncStatus]);
  const excludeSyncManaged = useIsAnyManaged([
    "search.exclude-sync-urls",
    "search.exclude-sync-refresh-minutes",
  ]);
  const handleSyncNow = useCallback(async () => {
    if (excludeSyncManaged) return;
    setIsSyncing(true);
    try {
      const res = await fetch(withUrlBase("/settings/exclude-sync"), { method: "POST" });
      if (res.ok) setSyncStatus(((await res.json()) as ExcludeSyncResponse).urls ?? []);
    } catch {
      // ignore; the row shows the backend-reported error on the next status load
    } finally {
      setIsSyncing(false);
    }
  }, [excludeSyncManaged]);

  const prowlarrUrl = config["prowlarr.url"] ?? "";
  const prowlarrApiKey = config["prowlarr.api-key"] ?? "";
  const prowlarrSyncEnabled = (config["prowlarr.sync-enabled"] ?? "false") === "true";
  const prowlarrSyncInterval = config["prowlarr.sync-interval-minutes"] ?? "";
  const prowlarrConfigKeys = [
    "prowlarr.url",
    "prowlarr.api-key",
    "prowlarr.sync-enabled",
    "prowlarr.sync-interval-minutes",
  ];
  const prowlarrSettingsDirty =
    savedConfig !== undefined &&
    [...prowlarrConfigKeys, "indexers.instances", "profiles.instances"].some(
      (key) => (config[key] ?? "") !== (savedConfig[key] ?? ""),
    );

  const [prowlarrStatus, setProwlarrStatus] = useState<ProwlarrSyncStatus | null>(null);
  const [isProwlarrSyncing, setIsProwlarrSyncing] = useState(false);
  const [prowlarrTestState, setProwlarrTestState] = useState<
    "idle" | "testing" | "success" | "error"
  >("idle");
  const [prowlarrTestError, setProwlarrTestError] = useState<string | null>(null);
  const loadProwlarrStatus = useCallback(async () => {
    try {
      const res = await fetch(withUrlBase("/api/prowlarr-sync"));
      if (res.ok) setProwlarrStatus((await res.json()) as ProwlarrSyncStatus);
    } catch {
      // Status is best-effort; the next load retries it.
    }
  }, []);
  const savedProwlarrFingerprint = prowlarrConfigKeys
    .map((key) => savedConfig?.[key] ?? "")
    .join("\u0000");
  useEffect(() => {
    void loadProwlarrStatus();
    const timer = setTimeout(() => {
      void loadProwlarrStatus();
    }, 2000);
    return () => clearTimeout(timer);
  }, [savedProwlarrFingerprint, loadProwlarrStatus]);
  useEffect(() => {
    setProwlarrTestState("idle");
    setProwlarrTestError(null);
  }, [prowlarrUrl, prowlarrApiKey]);

  const handleProwlarrFieldChange = useCallback(
    (key: string, value: string) => {
      setNewConfig({ ...config, [key]: value });
    },
    [config, setNewConfig],
  );
  const handleProwlarrTest = useCallback(async () => {
    if (!prowlarrUrl.trim() || !prowlarrApiKey.trim() || !isProwlarrUrlValid(prowlarrUrl)) return;
    setProwlarrTestState("testing");
    setProwlarrTestError(null);
    try {
      const fd = new FormData();
      fd.append("url", prowlarrUrl);
      fd.append("apiKey", prowlarrApiKey);
      const res = await fetch(withUrlBase("/api/test-prowlarr-connection"), {
        method: "POST",
        body: fd,
      });
      const data = (await res.json()) as ProwlarrConnectionTestResult;
      setProwlarrTestState(data.status && data.connected ? "success" : "error");
      setProwlarrTestError(data.connected ? null : (data.error ?? "Connection test failed"));
    } catch {
      setProwlarrTestState("error");
      setProwlarrTestError("Connection test failed");
    }
  }, [prowlarrUrl, prowlarrApiKey]);
  const handleProwlarrSyncNow = useCallback(async () => {
    if (prowlarrSettingsDirty || prowlarrStatus?.indexersEnvironmentManaged) return;
    setIsProwlarrSyncing(true);
    try {
      const res = await fetch(withUrlBase("/api/prowlarr-sync"), { method: "POST" });
      const data = (await res.json()) as ProwlarrSyncStatus;
      setProwlarrStatus(data);
      const patch: Record<string, string> = {};
      if (!data.error && data.indexerConfigJson)
        patch["indexers.instances"] = data.indexerConfigJson;
      if (!data.error && data.profileConfigJson)
        patch["profiles.instances"] = data.profileConfigJson;
      if (Object.keys(patch).length > 0) onSyncedConfig?.(patch);
    } catch {
      await loadProwlarrStatus();
    } finally {
      setIsProwlarrSyncing(false);
    }
  }, [
    prowlarrSettingsDirty,
    prowlarrStatus?.indexersEnvironmentManaged,
    onSyncedConfig,
    loadProwlarrStatus,
  ]);

  const defaultSearchUserAgent = config["api.search-user-agent"] ?? "";
  const handleSearchUserAgentChange = useCallback(
    (value: string) => {
      setNewConfig({ ...config, "api.search-user-agent": value });
    },
    [config, setNewConfig],
  );

  const defaultRetrieveUserAgent = config["api.user-agent"] ?? "";
  const handleRetrieveUserAgentChange = useCallback(
    (value: string) => {
      setNewConfig({ ...config, "api.user-agent": value });
    },
    [config, setNewConfig],
  );

  const proxyUrl = indexerConfig.ProxyUrl ?? "";
  const proxyValid = isProxyUrlValid(proxyUrl);
  const prowlarrUrlValid = isProwlarrUrlValid(prowlarrUrl);
  const prowlarrIntervalValid = isProwlarrSyncIntervalValid(prowlarrSyncInterval);
  const prowlarrReady =
    prowlarrUrl.trim() !== "" && prowlarrApiKey.trim() !== "" && prowlarrUrlValid;
  const globalTimeoutRaw =
    typeof indexerConfig.TimeoutSeconds === "number" && indexerConfig.TimeoutSeconds > 0
      ? indexerConfig.TimeoutSeconds.toString()
      : "";
  const globalSearchLimitRaw =
    typeof indexerConfig.SearchResultLimit === "number" && indexerConfig.SearchResultLimit > 0
      ? indexerConfig.SearchResultLimit.toString()
      : "";
  const globalMaxResponseBytesRaw =
    typeof indexerConfig.MaxResponseBytes === "number" && indexerConfig.MaxResponseBytes > 0
      ? indexerConfig.MaxResponseBytes.toString()
      : "";

  return (
    <SettingsPage className="mb-6">
      <SettingsIntro>
        Configure shared search behavior, filter unwanted results, and manage the Newznab-compatible
        indexers InfiniDysk queries.
      </SettingsIntro>

      <SettingsCard
        icon="tune"
        title="Connection defaults"
        description="Fallback connection and request settings used when an indexer has no override."
        contentClassName="grid grid-cols-1 gap-3.5 sm:grid-cols-2"
      >
        <ManagedSetting configKey="indexers.instances" className="sm:col-span-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="indexers-default-proxy">HTTP(S) Proxy URL</Label>
            <Input
              type="text"
              id="indexers-default-proxy"
              className={`w-full ${!proxyValid ? "input-error" : ""}`}
              placeholder="http://proxy:8888"
              value={proxyUrl}
              onChange={(e) => handleProxyChange(e.target.value)}
            />
          </div>
        </ManagedSetting>
        <ManagedSetting configKey="api.search-user-agent" className="sm:col-span-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="indexers-default-search-user-agent">
              Default Search User-Agent{" "}
              <span className="text-[11px] font-normal text-base-content/45">
                (sent when searching indexers; per-indexer override below)
              </span>
            </Label>
            <Input
              type="text"
              id="indexers-default-search-user-agent"
              className="w-full"
              placeholder="nzbdav/<version>"
              value={defaultSearchUserAgent}
              onChange={(e) => handleSearchUserAgentChange(e.target.value)}
            />
            <HelpText>
              Sent on indexer search and caps queries. Leave blank to use the default.
            </HelpText>
          </div>
        </ManagedSetting>
        <ManagedSetting configKey="api.user-agent" className="sm:col-span-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="indexers-default-retrieve-user-agent">
              Default Retrieve User-Agent{" "}
              <span className="text-[11px] font-normal text-base-content/45">
                (sent when retrieving the .nzb; per-indexer override below)
              </span>
            </Label>
            <Input
              type="text"
              id="indexers-default-retrieve-user-agent"
              className="w-full"
              placeholder="SABnzbd/5.1.0"
              value={defaultRetrieveUserAgent}
              onChange={(e) => handleRetrieveUserAgentChange(e.target.value)}
            />
            <HelpText>
              Sent when retrieving .nzb files, including SAB <code>addurl</code> requests matched to
              an indexer. Per-indexer overrides take precedence. Leave blank to use{" "}
              <code>SABnzbd/5.1.0</code> so indexers that require a SABnzbd client accept grabs.
            </HelpText>
          </div>
        </ManagedSetting>
        <ManagedSetting configKey="indexers.instances" className="sm:col-span-2 space-y-3.5">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="indexers-default-timeout">
              Request timeout (seconds){" "}
              <span className="text-[11px] font-normal text-base-content/45">
                (leave blank for {DEFAULT_TIMEOUT_SECONDS}s default)
              </span>
            </Label>
            <Input
              type="text"
              id="indexers-default-timeout"
              className={`w-full max-w-48 ${!isTimeoutValid(globalTimeoutRaw) ? "input-error" : ""}`}
              placeholder={DEFAULT_TIMEOUT_SECONDS.toString()}
              value={globalTimeoutRaw}
              onChange={(e) => handleTimeoutChange(e.target.value)}
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="indexers-default-search-limit">
              Search results per indexer{" "}
              <span className="text-[11px] font-normal text-base-content/45">
                (blank = {DEFAULT_SEARCH_RESULT_LIMIT}; higher pages the indexer for more results,
                using more API calls)
              </span>
            </Label>
            <Input
              type="text"
              id="indexers-default-search-limit"
              className={`w-full max-w-48 ${!isTimeoutValid(globalSearchLimitRaw) ? "input-error" : ""}`}
              placeholder={DEFAULT_SEARCH_RESULT_LIMIT.toString()}
              value={globalSearchLimitRaw}
              onChange={(e) => handleSearchLimitChange(e.target.value)}
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="indexers-default-max-response-bytes">
              Max indexer response (bytes){" "}
              <span className="text-[11px] font-normal text-base-content/45">
                (blank = {DEFAULT_MAX_RESPONSE_BYTES.toLocaleString()} / 4 MiB; max{" "}
                {MAX_RESPONSE_BYTES_HARD_CLAMP.toLocaleString()})
              </span>
            </Label>
            <Input
              type="text"
              id="indexers-default-max-response-bytes"
              className={`w-full max-w-48 ${!isMaxResponseBytesValid(globalMaxResponseBytesRaw) ? "input-error" : ""}`}
              placeholder={DEFAULT_MAX_RESPONSE_BYTES.toString()}
              value={globalMaxResponseBytesRaw}
              onChange={(e) => handleMaxResponseBytesChange(e.target.value)}
            />
            <HelpText>
              Caps how large a Newznab caps or search XML body may be before it is parsed. Counts
              the bytes the HTTP client delivers (not decompressed; automatic gzip is off).
            </HelpText>
          </div>
        </ManagedSetting>
      </SettingsCard>

      <SettingsCard
        icon="sync_alt"
        title="Prowlarr pull sync"
        description="Import enabled Usenet indexers from Prowlarr and keep their name, proxy URL, API key, and enabled state synchronized."
        contentClassName="grid grid-cols-1 gap-3.5 sm:grid-cols-2"
      >
        <ManagedSetting configKey="prowlarr.url" className="sm:col-span-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="prowlarr-url">Prowlarr URL</Label>
            <Input
              type="text"
              id="prowlarr-url"
              className={`w-full ${!prowlarrUrlValid ? "input-error" : ""}`}
              placeholder="http://prowlarr:9696"
              value={prowlarrUrl}
              onChange={(e) => handleProwlarrFieldChange("prowlarr.url", e.target.value)}
            />
            <HelpText>
              Include the port and URL base when needed. Credentials, query strings, and fragments
              are not supported.
            </HelpText>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="prowlarr.api-key" className="sm:col-span-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="prowlarr-api-key">Prowlarr API key</Label>
            <Input
              type="password"
              id="prowlarr-api-key"
              className="w-full"
              value={prowlarrApiKey}
              onChange={(e) => handleProwlarrFieldChange("prowlarr.api-key", e.target.value)}
            />
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="prowlarr.sync-enabled">
          <Tooltip
            className="tooltip-start"
            content="Periodically refresh the Prowlarr-managed entries below. Manual Sync now works even when this is off."
          >
            <Toggle
              id="prowlarr-sync-enabled"
              className="cursor-pointer gap-2 p-0"
              checked={prowlarrSyncEnabled}
              onChange={(e) =>
                handleProwlarrFieldChange(
                  "prowlarr.sync-enabled",
                  e.target.checked ? "true" : "false",
                )
              }
              label={<span className="text-sm text-base-content">Automatically sync</span>}
            />
          </Tooltip>
        </ManagedSetting>

        <ManagedSetting configKey="prowlarr.sync-interval-minutes">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="prowlarr-sync-interval">
              Sync every{" "}
              <span className="text-[11px] font-normal text-base-content/45">
                (minutes; blank = {DEFAULT_PROWLARR_SYNC_INTERVAL_MINUTES})
              </span>
            </Label>
            <Input
              type="text"
              inputMode="numeric"
              id="prowlarr-sync-interval"
              className={`w-full max-w-48 ${!prowlarrIntervalValid ? "input-error" : ""}`}
              placeholder={DEFAULT_PROWLARR_SYNC_INTERVAL_MINUTES.toString()}
              value={prowlarrSyncInterval}
              onChange={(e) =>
                handleProwlarrFieldChange(
                  "prowlarr.sync-interval-minutes",
                  e.target.value.replace(/[^0-9]/g, ""),
                )
              }
            />
          </div>
        </ManagedSetting>

        <div className="flex flex-wrap items-center gap-2.5 sm:col-span-2">
          <Button
            variant={
              prowlarrTestState === "success"
                ? "success"
                : prowlarrTestState === "error"
                  ? "danger"
                  : "secondary"
            }
            size="small"
            onClick={() => void handleProwlarrTest()}
            disabled={!prowlarrReady || prowlarrTestState === "testing"}
          >
            {prowlarrTestState === "testing" ? (
              <Spinner size="sm" />
            ) : prowlarrTestState === "success" ? (
              "✓ Connected"
            ) : prowlarrTestState === "error" ? (
              "✗ Failed"
            ) : (
              "Test Connection"
            )}
          </Button>
          <Button
            variant="primary"
            size="small"
            onClick={() => void handleProwlarrSyncNow()}
            disabled={
              !prowlarrReady ||
              isProwlarrSyncing ||
              prowlarrSettingsDirty ||
              prowlarrStatus?.indexersEnvironmentManaged === true
            }
            title={
              prowlarrSettingsDirty
                ? "Save settings before syncing Prowlarr indexers"
                : prowlarrStatus?.indexersEnvironmentManaged === true
                  ? "indexers.instances is managed by NZBDAV_CONFIG__INDEXERS__INSTANCES"
                  : undefined
            }
          >
            <Icon
              name={isProwlarrSyncing ? "progress_activity" : "sync"}
              className={`!text-[18px] ${isProwlarrSyncing ? "animate-spin" : ""}`}
            />
            {isProwlarrSyncing ? "Syncing…" : "Sync now"}
          </Button>
        </div>

        {prowlarrTestState === "error" && prowlarrTestError && (
          <Alert variant="danger" className="text-xs sm:col-span-2">
            {prowlarrTestError}
          </Alert>
        )}
        {prowlarrTestState === "success" && (
          <Alert variant="success" className="text-xs sm:col-span-2">
            Prowlarr connection test successful.
          </Alert>
        )}
        {prowlarrStatus && (
          <Alert
            variant={
              prowlarrStatus.indexersEnvironmentManaged || prowlarrStatus.error
                ? "danger"
                : prowlarrStatus.lastSuccessAt
                  ? "success"
                  : "info"
            }
            className="text-xs sm:col-span-2"
          >
            {prowlarrStatusSummary(prowlarrStatus)}
          </Alert>
        )}

        <HelpText className="sm:col-span-2">
          Synced entries point at Prowlarr's per-indexer Newznab proxy and are marked below.
          Prowlarr owns their name, URL, API key, and enabled state; InfiniDysk keeps your rate
          limits, filters, category overrides, proxy, TLS, timeout, and user-agent tuning. Save
          changes before syncing.
        </HelpText>
      </SettingsCard>

      <SettingsCard
        icon="filter_alt"
        title="Result exclusions"
        description="Drop matching releases with local patterns or synchronized external lists."
      >
        <ManagedSetting configKey="search.exclude-patterns" className="sm:col-span-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="indexers-exclude-patterns">
              Exclude result patterns{" "}
              <span className="text-[11px] font-normal text-base-content/45">
                (applies to every indexer)
              </span>
            </Label>
            <Textarea
              id="indexers-exclude-patterns"
              rows={6}
              spellCheck={false}
              className={`w-full font-mono text-xs ${patternIssues.length > 0 ? "input-error" : ""}`}
              placeholder={"# one regex per line\n# lines starting with # are comments"}
              value={excludePatterns}
              onChange={(e) => handleExcludePatternsChange(e.target.value)}
            />
            {patternIssues.length > 0 && (
              <div className="flex flex-col gap-1 rounded-md border border-error/35 bg-error/10 p-2.5 text-xs">
                {patternIssues.map((iss, i) => (
                  <div key={i} className="flex flex-wrap items-baseline gap-1.5 text-base-content">
                    <span className="shrink-0 font-semibold text-error">Line {iss.line}</span>
                    <code className="rounded bg-error/10 px-1.5 py-0.5 font-mono text-error">
                      {iss.pattern}
                    </code>
                    <span className="text-base-content/60">— {iss.error}</span>
                  </div>
                ))}
              </div>
            )}
            <HelpText>
              One JavaScript-style regex per line. Search results whose title matches any pattern
              are dropped before being returned. Case-insensitive by default — use{" "}
              <code>(?-i:Foo)</code> for case-sensitive. Lines starting with <code>#</code> are
              comments. Use this to skip releases your setup can't handle, whatever the reason.
            </HelpText>
          </div>
        </ManagedSetting>

        <ManagedSetting
          configKeys={["search.exclude-sync-urls", "search.exclude-sync-refresh-minutes"]}
          className="sm:col-span-2"
        >
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="indexers-exclude-sync-urls">
              Synced exclude URLs{" "}
              <span className="text-[11px] font-normal text-base-content/45">
                (auto-updating; one URL per line)
              </span>
            </Label>
            <Textarea
              id="indexers-exclude-sync-urls"
              rows={3}
              spellCheck={false}
              className={`w-full font-mono text-xs ${syncUrlIssues.length > 0 ? "input-error" : ""}`}
              placeholder={
                "# one URL per line\nhttps://raw.githubusercontent.com/.../excluded-regex.json"
              }
              value={excludeSyncUrls}
              onChange={(e) => handleSyncUrlsChange(e.target.value)}
            />
            {syncUrlIssues.length > 0 && (
              <div className="flex flex-col gap-1 rounded-md border border-error/35 bg-error/10 p-2.5 text-xs">
                {syncUrlIssues.map((iss, i) => (
                  <div key={i} className="flex flex-wrap items-baseline gap-1.5 text-base-content">
                    <span className="shrink-0 font-semibold text-error">Line {iss.line}</span>
                    <code className="rounded bg-error/10 px-1.5 py-0.5 font-mono text-error">
                      {iss.value}
                    </code>
                    <span className="text-base-content/60">— {iss.error}</span>
                  </div>
                ))}
              </div>
            )}
            <div className="mt-2.5 flex flex-wrap items-center gap-2.5">
              <Label
                htmlFor="indexers-exclude-sync-refresh"
                className="text-sm font-normal text-base-content/80"
              >
                Refresh every
              </Label>
              <Input
                type="text"
                inputMode="numeric"
                id="indexers-exclude-sync-refresh"
                className={`w-[90px] ${!isRefreshValid(excludeSyncRefresh) ? "input-error" : ""}`}
                placeholder="720"
                value={excludeSyncRefresh}
                onChange={(e) => handleSyncRefreshChange(e.target.value)}
              />
              <span className="text-[11px] text-base-content/45">minutes</span>
              <Button
                variant="primary"
                size="small"
                onClick={() => void handleSyncNow()}
                disabled={isSyncing || excludeSyncManaged}
                title={
                  excludeSyncManaged
                    ? "Synced exclude URLs are managed by NZBDAV_CONFIG__... — change the container environment and restart"
                    : undefined
                }
              >
                <Icon
                  name={isSyncing ? "progress_activity" : "sync"}
                  className={`!text-[18px] ${isSyncing ? "animate-spin" : ""}`}
                />
                {isSyncing ? "Syncing…" : "Sync now"}
              </Button>
            </div>
            {syncStatus.length > 0 && (
              <div className="mt-2.5 flex flex-col gap-1">
                {syncStatus.map((s, i) => (
                  <div key={i} className="overflow-wrap-anywhere text-[13px] leading-snug">
                    {s.error ? (
                      <span className="text-error">
                        ✗ {syncHostLabel(s.url)} — {s.error}
                      </span>
                    ) : (
                      <span className="text-success">
                        ✓ {syncHostLabel(s.url)} — {s.count} pattern{s.count === 1 ? "" : "s"}
                        {s.lastChecked ? ` · synced ${syncRelativeTime(s.lastChecked)}` : ""}
                      </span>
                    )}
                  </div>
                ))}
              </div>
            )}
            <HelpText>
              Point at one or more JSON lists of regex patterns (e.g. TRaSH-derived exclude URLs).
              Accepts <code>{`{ "values": ["…"] }`}</code> or <code>{`[{ "pattern": "…" }]`}</code>.
              Synced patterns are fetched on the interval above and take precedence; your manual
              patterns above are merged in after, with exact duplicates removed. If a URL can't be
              reached, the last good copy keeps working. Save your changes first, then use{" "}
              <strong>Sync now</strong>.
            </HelpText>
          </div>
        </ManagedSetting>
      </SettingsCard>

      <ManagedSetting configKey="indexers.instances">
        <SettingsCard
          icon="travel_explore"
          title="Configured indexers"
          description="Add, enable, and tune the services used to discover NZB releases."
          action={
            <Button size="xsmall" onClick={handleAdd}>
              Add
            </Button>
          }
        >
          {indexerConfig.Indexers.length === 0 ? (
            <p className="rounded border border-base-content/10 bg-base-200/40 px-5 py-5 text-sm italic text-base-content/60">
              No indexers configured. Add a Newznab-compatible indexer (or aggregator) to enable
              search.
            </p>
          ) : (
            <div className="mb-7 grid grid-cols-1 gap-4 lg:grid-cols-2">
              {indexerConfig.Indexers.map((indexer, index) => (
                <IndexerCard
                  key={index}
                  indexer={indexer}
                  onEdit={() => handleEdit(index)}
                  onToggle={() => handleToggle(index)}
                  onDelete={() => handleDelete(index)}
                />
              ))}
            </div>
          )}
        </SettingsCard>

        <IndexerModal
          show={showModal}
          indexer={editingIndex !== null ? (indexerConfig.Indexers[editingIndex] ?? null) : null}
          onClose={handleCloseModal}
          onSave={handleSave}
        />
      </ManagedSetting>
    </SettingsPage>
  );
}

type IndexerCardProps = {
  indexer: ConnectionDetails;
  onEdit: () => void;
  onToggle: () => void;
  onDelete: () => void;
};

function IndexerCard({ indexer, onEdit, onToggle, onDelete }: IndexerCardProps) {
  const isDisabled = !indexer.Enabled;
  const isProwlarrManaged = typeof indexer.ProwlarrIndexerId === "number";
  const host = (() => {
    try {
      return new URL(indexer.Url).host;
    } catch {
      return indexer.Url || "—";
    }
  })();
  const rateLimit =
    indexer.MaxRequestsPerMinute && indexer.MaxRequestsPerMinute > 0
      ? `${indexer.MaxRequestsPerMinute} / min`
      : "Unlimited";
  const searchUserAgent = indexer.SearchUserAgent?.trim() || indexer.UserAgent?.trim() || "Default";
  const retrieveUserAgent =
    indexer.RetrieveUserAgent?.trim() || indexer.UserAgent?.trim() || "Default";
  const proxy = indexer.ProxyUrl?.trim() ? indexer.ProxyUrl : "Default";
  const timeout =
    indexer.TimeoutSeconds && indexer.TimeoutSeconds > 0 ? `${indexer.TimeoutSeconds}s` : "Default";
  const resultLimit =
    indexer.SearchResultLimit && indexer.SearchResultLimit > 0
      ? indexer.SearchResultLimit.toString()
      : "Default";
  const formatLimit = (n: number | undefined, perDay: boolean) => {
    if (!n || n <= 0) return "Unlimited";
    return perDay ? `${n} / day` : `${n} / 24h`;
  };
  const hasResetHour =
    typeof indexer.HitLimitResetTime === "number" &&
    indexer.HitLimitResetTime >= 0 &&
    indexer.HitLimitResetTime <= 23;
  const apiLimit = formatLimit(indexer.HitLimit, hasResetHour);
  const downloadLimit = formatLimit(indexer.DownloadLimit, hasResetHour);
  const categoriesSummary = (() => {
    if (indexer.IgnoreCategoryFilter) return "All (no filter)";
    const m = indexer.ExtraMovieCategories?.trim();
    const t = indexer.ExtraTvCategories?.trim();
    if (!m && !t) return "Default";
    const parts: string[] = [];
    if (m) parts.push(`+M ${m}`);
    if (t) parts.push(`+T ${t}`);
    return parts.join(" · ");
  })();

  return (
    <div
      className={`card border border-base-content/10 bg-base-100 shadow-sm ${isDisabled ? "opacity-60" : ""}`}
    >
      <div className="card-body gap-3 p-4">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0 flex-1">
            <div className="break-all text-[15px] font-semibold leading-snug tracking-tight text-base-content">
              {indexer.Name || "(unnamed)"}
              {isProwlarrManaged && (
                <Badge className="badge-info badge-soft badge-sm ml-2 align-middle">Prowlarr</Badge>
              )}
              {isDisabled && (
                <Badge className="badge-ghost badge-sm ml-2 align-middle">Disabled</Badge>
              )}
            </div>
            <div className="break-all text-[10px] font-medium uppercase tracking-wide text-base-content/50">
              {host}
            </div>
          </div>
          <div className="flex shrink-0 gap-1">
            <button
              type="button"
              className={`btn btn-ghost btn-sm btn-square ${isDisabled ? "text-base-content/40" : "text-success"}`}
              onClick={onToggle}
              title={isDisabled ? "Enable Indexer" : "Disable Indexer"}
              aria-pressed={!isDisabled}
            >
              <svg
                width="14"
                height="14"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                strokeLinecap="round"
                strokeLinejoin="round"
              >
                <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                <line x1="12" y1="2" x2="12" y2="12" />
              </svg>
            </button>
            <button
              type="button"
              className="btn btn-ghost btn-sm btn-square"
              onClick={onEdit}
              title="Edit Indexer"
            >
              <svg
                width="14"
                height="14"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
              >
                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
              </svg>
            </button>
            <button
              type="button"
              className="btn btn-ghost btn-sm btn-square hover:text-error"
              onClick={onDelete}
              title="Delete Indexer"
            >
              <svg
                width="14"
                height="14"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
              >
                <polyline points="3 6 5 6 21 6" />
                <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
              </svg>
            </button>
          </div>
        </div>

        <div className="flex flex-col gap-2">
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <circle cx="12" cy="12" r="10" />
                  <line x1="2" y1="12" x2="22" y2="12" />
                  <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Host
                </span>
                <span
                  className="truncate text-sm font-medium text-base-content"
                  title={indexer.Url}
                >
                  {host}
                </span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <circle cx="12" cy="12" r="10" />
                  <polyline points="12 6 12 12 16 14" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Rate limit
                </span>
                <span className="truncate text-sm font-medium text-base-content">{rateLimit}</span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <path d="M9 12l2 2 4-4" />
                  <path d="M21 12c0 4.97-4.03 9-9 9s-9-4.03-9-9 4.03-9 9-9 9 4.03 9 9z" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Strict matching
                </span>
                <span className="truncate text-sm font-medium text-base-content">
                  {indexer.EnableStrictMatching ? "Enabled" : "Disabled"}
                </span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <circle cx="11" cy="11" r="8" />
                  <line x1="21" y1="21" x2="16.65" y2="16.65" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Search UA
                </span>
                <span
                  className="truncate text-sm font-medium text-base-content"
                  title={indexer.SearchUserAgent ?? indexer.UserAgent ?? ""}
                >
                  {searchUserAgent}
                </span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                  <polyline points="7 10 12 15 17 10" />
                  <line x1="12" y1="15" x2="12" y2="3" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Retrieve UA
                </span>
                <span
                  className="truncate text-sm font-medium text-base-content"
                  title={indexer.RetrieveUserAgent ?? indexer.UserAgent ?? ""}
                >
                  {retrieveUserAgent}
                </span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Result filtering
                </span>
                <span className="truncate text-sm font-medium text-base-content">
                  {indexer.Filter?.Enabled ? "Enabled" : "Disabled"}
                </span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <rect x="2" y="6" width="20" height="12" rx="2" />
                  <path d="M6 12h.01M10 12h.01M14 12h.01M18 12h.01" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Proxy
                </span>
                <span
                  className="truncate text-sm font-medium text-base-content"
                  title={indexer.ProxyUrl ?? ""}
                >
                  {proxy}
                </span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <circle cx="12" cy="12" r="10" />
                  <polyline points="12 6 12 12 16 14" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Timeout
                </span>
                <span className="truncate text-sm font-medium text-base-content">{timeout}</span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <line x1="8" y1="6" x2="21" y2="6" />
                  <line x1="8" y1="12" x2="21" y2="12" />
                  <line x1="8" y1="18" x2="21" y2="18" />
                  <line x1="3" y1="6" x2="3.01" y2="6" />
                  <line x1="3" y1="12" x2="3.01" y2="12" />
                  <line x1="3" y1="18" x2="3.01" y2="18" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Result limit
                </span>
                <span className="truncate text-sm font-medium text-base-content">
                  {resultLimit}
                </span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <path d="M3 12h4l3-9 4 18 3-9h4" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  API limit
                </span>
                <span className="truncate text-sm font-medium text-base-content">{apiLimit}</span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                  <polyline points="7 10 12 15 17 10" />
                  <line x1="12" y1="15" x2="12" y2="3" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Download limit
                </span>
                <span className="truncate text-sm font-medium text-base-content">
                  {downloadLimit}
                </span>
              </div>
            </div>

            <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
              <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                <svg
                  width="13"
                  height="13"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <path d="M7 7h.01M7 3h5a2 2 0 0 1 1.41.59l7 7a2 2 0 0 1 0 2.82l-7 7a2 2 0 0 1-2.82 0l-7-7A2 2 0 0 1 3 12V7a4 4 0 0 1 4-4z" />
                </svg>
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
                  Categories
                </span>
                <span
                  className="truncate text-sm font-medium text-base-content"
                  title={categoriesSummary}
                >
                  {categoriesSummary}
                </span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

type IndexerModalProps = {
  show: boolean;
  indexer: ConnectionDetails | null;
  onClose: () => void;
  onSave: (indexer: ConnectionDetails) => void;
};

function IndexerModal({ show, indexer, onClose, onSave }: IndexerModalProps) {
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [skipTlsVerification, setSkipTlsVerification] = useState(false);
  const [searchUserAgent, setSearchUserAgent] = useState("");
  const [retrieveUserAgent, setRetrieveUserAgent] = useState("");
  const [proxyUrl, setProxyUrl] = useState("");
  const [timeoutSeconds, setTimeoutSeconds] = useState("");
  const [searchResultLimit, setSearchResultLimit] = useState("");
  const [maxResponseBytes, setMaxResponseBytes] = useState("");
  const [maxRpm, setMaxRpm] = useState("0");
  const [hitLimit, setHitLimit] = useState("");
  const [downloadLimit, setDownloadLimit] = useState("");
  const [hitResetTime, setHitResetTime] = useState("");
  const [enabled, setEnabled] = useState(true);
  const [strict, setStrict] = useState(false);
  const [extraMovieCategories, setExtraMovieCategories] = useState("");
  const [extraTvCategories, setExtraTvCategories] = useState("");
  const [ignoreCategoryFilter, setIgnoreCategoryFilter] = useState(false);

  const [filterEnabled, setFilterEnabled] = useState(false);
  const [filterAdvancedOpen, setFilterAdvancedOpen] = useState(false);
  const [filterSkipPassworded, setFilterSkipPassworded] = useState(
    OPTIMISED_DEFAULTS.SkipPassworded,
  );
  const [filterMinGrabs, setFilterMinGrabs] = useState(OPTIMISED_DEFAULTS.MinGrabs.toString());
  const [filterGrabsGraceHours, setFilterGrabsGraceHours] = useState(
    OPTIMISED_DEFAULTS.GrabsGraceHours.toString(),
  );
  const [filterMaxAgeDaysWithoutGrabs, setFilterMaxAgeDaysWithoutGrabs] = useState(
    OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString(),
  );
  const [filterPreferDownloaded, setFilterPreferDownloaded] = useState(
    OPTIMISED_DEFAULTS.PreferDownloaded,
  );

  const resetFilterToDefaults = useCallback(() => {
    setFilterSkipPassworded(OPTIMISED_DEFAULTS.SkipPassworded);
    setFilterMinGrabs(OPTIMISED_DEFAULTS.MinGrabs.toString());
    setFilterGrabsGraceHours(OPTIMISED_DEFAULTS.GrabsGraceHours.toString());
    setFilterMaxAgeDaysWithoutGrabs(OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString());
    setFilterPreferDownloaded(OPTIMISED_DEFAULTS.PreferDownloaded);
  }, []);

  const [testState, setTestState] = useState<"idle" | "testing" | "success" | "error">("idle");
  const isProwlarrManaged = typeof indexer?.ProwlarrIndexerId === "number";

  useEffect(() => {
    if (show) {
      setName(indexer?.Name || "");
      setUrl(indexer?.Url || "");
      setApiKey(indexer?.ApiKey || "");
      setSkipTlsVerification(indexer?.SkipTlsVerification ?? false);
      setSearchUserAgent(indexer?.SearchUserAgent || indexer?.UserAgent || "");
      setRetrieveUserAgent(indexer?.RetrieveUserAgent || indexer?.UserAgent || "");
      setProxyUrl(indexer?.ProxyUrl || "");
      setTimeoutSeconds(
        indexer?.TimeoutSeconds && indexer.TimeoutSeconds > 0
          ? indexer.TimeoutSeconds.toString()
          : "",
      );
      setSearchResultLimit(
        indexer?.SearchResultLimit && indexer.SearchResultLimit > 0
          ? indexer.SearchResultLimit.toString()
          : "",
      );
      setMaxResponseBytes(
        indexer?.MaxResponseBytes && indexer.MaxResponseBytes > 0
          ? indexer.MaxResponseBytes.toString()
          : "",
      );
      setMaxRpm((indexer?.MaxRequestsPerMinute ?? 0).toString());
      setHitLimit(indexer?.HitLimit && indexer.HitLimit > 0 ? indexer.HitLimit.toString() : "");
      setDownloadLimit(
        indexer?.DownloadLimit && indexer.DownloadLimit > 0 ? indexer.DownloadLimit.toString() : "",
      );
      setHitResetTime(
        typeof indexer?.HitLimitResetTime === "number" &&
          indexer.HitLimitResetTime >= 0 &&
          indexer.HitLimitResetTime <= 23
          ? indexer.HitLimitResetTime.toString()
          : "",
      );
      setEnabled(indexer?.Enabled ?? true);
      setStrict(indexer?.EnableStrictMatching ?? false);
      setExtraMovieCategories(indexer?.ExtraMovieCategories ?? "");
      setExtraTvCategories(indexer?.ExtraTvCategories ?? "");
      setIgnoreCategoryFilter(indexer?.IgnoreCategoryFilter ?? false);
      const f = indexer?.Filter ?? OPTIMISED_DEFAULTS;
      setFilterEnabled(f.Enabled);
      setFilterSkipPassworded(f.SkipPassworded);
      setFilterMinGrabs((f.MinGrabs ?? OPTIMISED_DEFAULTS.MinGrabs).toString());
      setFilterGrabsGraceHours(
        (f.GrabsGraceHours ?? OPTIMISED_DEFAULTS.GrabsGraceHours).toString(),
      );
      setFilterMaxAgeDaysWithoutGrabs(
        (f.MaxAgeDaysWithoutGrabs ?? OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs).toString(),
      );
      setFilterPreferDownloaded(f.PreferDownloaded);
      setFilterAdvancedOpen(false);
      setTestState("idle");
    }
  }, [show, indexer]);

  useEffect(() => {
    setTestState("idle");
  }, [url, apiKey, searchUserAgent, proxyUrl, timeoutSeconds, maxResponseBytes, skipTlsVerification]);

  const handleTest = useCallback(async () => {
    if (!url.trim() || !apiKey.trim()) return;
    setTestState("testing");
    try {
      const fd = new FormData();
      fd.append("url", url);
      fd.append("apiKey", apiKey);
      if (searchUserAgent.trim()) fd.append("userAgent", searchUserAgent);
      if (proxyUrl.trim()) fd.append("proxyUrl", proxyUrl);
      if (timeoutSeconds.trim()) fd.append("timeoutSeconds", timeoutSeconds);
      if (maxResponseBytes.trim()) fd.append("maxResponseBytes", maxResponseBytes);
      fd.append("skipTlsVerification", skipTlsVerification.toString());
      const r = await fetch(withUrlBase("/api/test-indexer-connection"), {
        method: "POST",
        body: fd,
      });
      // Response of POST /api/test-indexer-connection (backend TestIndexerConnectionResponse).
      const data = (await r.json()) as TestIndexerConnectionResult;
      setTestState(data.status && data.connected ? "success" : "error");
    } catch {
      setTestState("error");
    }
  }, [url, apiKey, searchUserAgent, proxyUrl, timeoutSeconds, maxResponseBytes, skipTlsVerification]);

  const handleSave = useCallback(() => {
    const rpm = parseInt(maxRpm || "0", 10);
    const timeout = parseInt(timeoutSeconds || "0", 10);
    const srl = parseInt(searchResultLimit || "0", 10);
    const maxBytes = parseInt(maxResponseBytes || "0", 10);
    const hl = parseInt(hitLimit || "0", 10);
    const dl = parseInt(downloadLimit || "0", 10);
    const hr = hitResetTime.trim() === "" ? NaN : parseInt(hitResetTime, 10);
    const clampNonNegInt = (raw: string, fallback: number) => {
      const n = parseInt(raw || "0", 10);
      return Number.isFinite(n) && n >= 0 ? n : fallback;
    };
    const filterIsClean =
      !filterEnabled &&
      filterSkipPassworded === OPTIMISED_DEFAULTS.SkipPassworded &&
      clampNonNegInt(filterMinGrabs, OPTIMISED_DEFAULTS.MinGrabs) === OPTIMISED_DEFAULTS.MinGrabs &&
      clampNonNegInt(filterGrabsGraceHours, OPTIMISED_DEFAULTS.GrabsGraceHours) ===
        OPTIMISED_DEFAULTS.GrabsGraceHours &&
      clampNonNegInt(filterMaxAgeDaysWithoutGrabs, OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs) ===
        OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs &&
      filterPreferDownloaded === OPTIMISED_DEFAULTS.PreferDownloaded;
    const normaliseCategoryList = (raw: string) => {
      const parts = raw
        .split(",")
        .map((p) => p.trim())
        .filter((p) => p.length > 0);
      return parts.length === 0 ? undefined : parts.join(",");
    };
    const movieCats = normaliseCategoryList(extraMovieCategories);
    const tvCats = normaliseCategoryList(extraTvCategories);
    onSave({
      Name: name.trim(),
      Url: url.trim(),
      ApiKey: apiKey.trim(),
      Enabled: enabled,
      ...(indexer?.ProwlarrIndexerId != null
        ? { ProwlarrIndexerId: indexer.ProwlarrIndexerId }
        : {}),
      ...(searchUserAgent.trim() ? { SearchUserAgent: searchUserAgent.trim() } : {}),
      ...(retrieveUserAgent.trim() ? { RetrieveUserAgent: retrieveUserAgent.trim() } : {}),
      ...(url.trim().toLowerCase().startsWith("https://") && skipTlsVerification
        ? { SkipTlsVerification: true }
        : {}),
      ...(proxyUrl.trim() ? { ProxyUrl: proxyUrl.trim() } : {}),
      ...(Number.isFinite(timeout) && timeout > 0 ? { TimeoutSeconds: timeout } : {}),
      ...(Number.isFinite(srl) && srl > 0 ? { SearchResultLimit: srl } : {}),
      ...(Number.isFinite(maxBytes) && maxBytes > 0 ? { MaxResponseBytes: maxBytes } : {}),
      MaxRequestsPerMinute: Number.isFinite(rpm) && rpm > 0 ? rpm : 0,
      ...(Number.isFinite(hl) && hl > 0 ? { HitLimit: hl } : {}),
      ...(Number.isFinite(dl) && dl > 0 ? { DownloadLimit: dl } : {}),
      ...(Number.isFinite(hr) && hr >= 0 && hr <= 23 ? { HitLimitResetTime: hr } : {}),
      EnableStrictMatching: strict,
      ...(movieCats ? { ExtraMovieCategories: movieCats } : {}),
      ...(tvCats ? { ExtraTvCategories: tvCats } : {}),
      ...(ignoreCategoryFilter ? { IgnoreCategoryFilter: true } : {}),
      ...(filterIsClean
        ? {}
        : {
            Filter: {
              Enabled: filterEnabled,
              SkipPassworded: filterSkipPassworded,
              MinGrabs: clampNonNegInt(filterMinGrabs, 0),
              GrabsGraceHours: clampNonNegInt(filterGrabsGraceHours, 6),
              MaxAgeDaysWithoutGrabs: clampNonNegInt(filterMaxAgeDaysWithoutGrabs, 0),
              PreferDownloaded: filterPreferDownloaded,
            },
          }),
    });
  }, [
    name,
    url,
    apiKey,
    searchUserAgent,
    retrieveUserAgent,
    skipTlsVerification,
    proxyUrl,
    timeoutSeconds,
    searchResultLimit,
    maxResponseBytes,
    maxRpm,
    hitLimit,
    downloadLimit,
    hitResetTime,
    enabled,
    strict,
    extraMovieCategories,
    extraTvCategories,
    ignoreCategoryFilter,
    filterEnabled,
    filterSkipPassworded,
    filterMinGrabs,
    filterGrabsGraceHours,
    filterMaxAgeDaysWithoutGrabs,
    filterPreferDownloaded,
    indexer?.ProwlarrIndexerId,
    onSave,
  ]);

  const isUrlValid = (() => {
    if (!url.trim()) return false;
    try {
      new URL(url);
      return true;
    } catch {
      return false;
    }
  })();
  const isHttpsUrl = url.trim().toLowerCase().startsWith("https://");
  const isRpmValid = (() => {
    const n = Number(maxRpm);
    return Number.isInteger(n) && n >= 0 && maxRpm.trim() === n.toString();
  })();
  const isProxyValid = isProxyUrlValid(proxyUrl);
  const isTimeoutFieldValid = isTimeoutValid(timeoutSeconds);
  const isMaxResponseBytesFieldValid = isMaxResponseBytesValid(maxResponseBytes);
  const isNonNegIntOrBlank = (raw: string) => {
    if (!raw.trim()) return true;
    const n = Number(raw);
    return Number.isInteger(n) && n >= 0 && raw.trim() === n.toString();
  };
  const isHitLimitValid = isNonNegIntOrBlank(hitLimit);
  const isSearchResultLimitValid = isNonNegIntOrBlank(searchResultLimit);
  const isDownloadLimitValid = isNonNegIntOrBlank(downloadLimit);
  const isHitResetValid = (() => {
    if (!hitResetTime.trim()) return true;
    const n = Number(hitResetTime);
    return Number.isInteger(n) && n >= 0 && n <= 23 && hitResetTime.trim() === n.toString();
  })();
  const isExtraMovieCategoriesValid = isCategoryListValid(extraMovieCategories);
  const isExtraTvCategoriesValid = isCategoryListValid(extraTvCategories);
  const isFormValid =
    name.trim() !== "" &&
    isUrlValid &&
    apiKey.trim() !== "" &&
    isRpmValid &&
    isProxyValid &&
    isTimeoutFieldValid &&
    isMaxResponseBytesFieldValid &&
    isHitLimitValid &&
    isSearchResultLimitValid &&
    isDownloadLimitValid &&
    isHitResetValid &&
    isExtraMovieCategoriesValid &&
    isExtraTvCategoriesValid;

  return (
    <Modal
      open={show}
      title={indexer ? "Edit Indexer" : "Add Indexer"}
      onClose={onClose}
      className="!max-w-2xl"
      footer={
        <>
          <Button
            variant={
              testState === "success" ? "success" : testState === "error" ? "danger" : "secondary"
            }
            onClick={() => void handleTest()}
            disabled={!isUrlValid || !apiKey.trim() || testState === "testing"}
          >
            {testState === "testing" ? (
              <Spinner size="sm" />
            ) : testState === "success" ? (
              "✓ Tested"
            ) : testState === "error" ? (
              "✗ Failed"
            ) : (
              "Test Connection"
            )}
          </Button>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" onClick={handleSave} disabled={!isFormValid}>
            {indexer ? "Save Indexer" : "Add Indexer"}
          </Button>
        </>
      }
    >
      {isProwlarrManaged && (
        <Alert variant="info" className="mb-4 text-xs">
          Prowlarr manages this indexer's name, URL, API key, and enabled state. Your changes to
          those fields are overwritten on the next sync; InfiniDysk-specific tuning is preserved.
        </Alert>
      )}
      <div className="grid grid-cols-1 gap-3.5 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-name">Name</Label>
          <Input
            type="text"
            id="indexer-name"
            className="w-full"
            placeholder="e.g. My Indexer"
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="indexer-url">URL</Label>
          <Input
            type="text"
            id="indexer-url"
            className={`w-full ${!isUrlValid && url !== "" ? "input-error" : ""}`}
            placeholder="https://api.example.com"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
          />
        </div>
        {isHttpsUrl && (
          <div className="flex flex-col gap-1.5 sm:col-span-2">
            <Tooltip content="TLS stays encrypted, but accepts an untrusted or mismatched certificate. Only enable for an indexer you trust.">
              <Toggle
                id="indexer-skip-tls-verification"
                className="cursor-pointer gap-2 p-0"
                checked={skipTlsVerification}
                onChange={(e) => setSkipTlsVerification(e.target.checked)}
                label={
                  <span className="text-sm text-base-content">
                    Skip TLS certificate verification
                  </span>
                }
              />
            </Tooltip>
            {skipTlsVerification && (
              <Alert variant="warning" className="text-xs">
                TLS remains encrypted, but this accepts an untrusted or mismatched certificate. Only
                enable it for an indexer you trust.
              </Alert>
            )}
          </div>
        )}

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="indexer-apikey">API Key</Label>
          <Input
            type="password"
            id="indexer-apikey"
            className="w-full"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="indexer-search-ua">
            Search User-Agent{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (optional; overrides the global Search default)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-search-ua"
            className="w-full"
            placeholder="Leave blank to use global default"
            value={searchUserAgent}
            onChange={(e) => setSearchUserAgent(e.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="indexer-retrieve-ua">
            Retrieve User-Agent{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (optional; overrides the global Retrieve default)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-retrieve-ua"
            className="w-full"
            placeholder="Leave blank to use global default"
            value={retrieveUserAgent}
            onChange={(e) => setRetrieveUserAgent(e.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="indexer-proxy">
            HTTP(S) Proxy URL{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (optional; overrides the global default)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-proxy"
            className={`w-full ${!isProxyValid && proxyUrl !== "" ? "input-error" : ""}`}
            placeholder="Leave blank to use global default"
            value={proxyUrl}
            onChange={(e) => setProxyUrl(e.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-rpm">
            Max requests / minute{" "}
            <span className="text-[11px] font-normal text-base-content/45">(0 = unlimited)</span>
          </Label>
          <Input
            type="text"
            id="indexer-rpm"
            className={`w-full max-w-48 ${!isRpmValid && maxRpm !== "" ? "input-error" : ""}`}
            placeholder="0"
            value={maxRpm}
            onChange={(e) => setMaxRpm(e.target.value)}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-timeout">
            Request timeout (seconds){" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (blank = use global default)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-timeout"
            className={`w-full max-w-48 ${!isTimeoutFieldValid && timeoutSeconds !== "" ? "input-error" : ""}`}
            placeholder="Use global default"
            value={timeoutSeconds}
            onChange={(e) => setTimeoutSeconds(e.target.value.replace(/[^0-9]/g, ""))}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-search-limit">
            Search result limit{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (blank = use global default)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-search-limit"
            className={`w-full max-w-48 ${!isSearchResultLimitValid ? "input-error" : ""}`}
            placeholder="Use global default"
            value={searchResultLimit}
            onChange={(e) => setSearchResultLimit(e.target.value.replace(/[^0-9]/g, ""))}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-max-response-bytes">
            Max indexer response (bytes){" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (blank = use global default)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-max-response-bytes"
            className={`w-full max-w-48 ${!isMaxResponseBytesFieldValid && maxResponseBytes !== "" ? "input-error" : ""}`}
            placeholder="Use global default"
            value={maxResponseBytes}
            onChange={(e) => setMaxResponseBytes(e.target.value.replace(/[^0-9]/g, ""))}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-hit-limit">
            API hit limit{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (blank or 0 = unlimited)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-hit-limit"
            className={`w-full max-w-48 ${!isHitLimitValid ? "input-error" : ""}`}
            placeholder="Unlimited"
            value={hitLimit}
            onChange={(e) => setHitLimit(e.target.value.replace(/[^0-9]/g, ""))}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-download-limit">
            Download limit{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (blank or 0 = unlimited)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-download-limit"
            className={`w-full max-w-48 ${!isDownloadLimitValid ? "input-error" : ""}`}
            placeholder="Unlimited"
            value={downloadLimit}
            onChange={(e) => setDownloadLimit(e.target.value.replace(/[^0-9]/g, ""))}
          />
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="indexer-hit-reset-time">
            Hit reset time{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (UTC hour 0-23; blank = rolling 24h window)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-hit-reset-time"
            className={`w-full max-w-48 ${!isHitResetValid ? "input-error" : ""}`}
            placeholder="Rolling 24h"
            value={hitResetTime}
            onChange={(e) => setHitResetTime(e.target.value.replace(/[^0-9]/g, ""))}
          />
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Tooltip content="Include this indexer in search and grab requests.">
            <Toggle
              id="indexer-enabled"
              className="cursor-pointer gap-2 p-0"
              checked={enabled}
              onChange={(e) => setEnabled(e.target.checked)}
              label={<span className="text-sm text-base-content">Enabled</span>}
            />
          </Tooltip>
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Tooltip content="Drop results whose title doesn't match the request.">
            <Toggle
              id="indexer-strict"
              className="cursor-pointer gap-2 p-0"
              checked={strict}
              onChange={(e) => setStrict(e.target.checked)}
              label={<span className="text-sm text-base-content">Strict matching</span>}
            />
          </Tooltip>
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-extra-movie-cats">
            Extra movie categories{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (comma-separated; appended to the default 2000/2070)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-extra-movie-cats"
            className={`w-full ${!isExtraMovieCategoriesValid ? "input-error" : ""}`}
            placeholder="e.g. 2100,2200"
            value={extraMovieCategories}
            onChange={(e) => setExtraMovieCategories(e.target.value)}
            disabled={ignoreCategoryFilter}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="indexer-extra-tv-cats">
            Extra TV categories{" "}
            <span className="text-[11px] font-normal text-base-content/45">
              (comma-separated; appended to the default 5000/5070)
            </span>
          </Label>
          <Input
            type="text"
            id="indexer-extra-tv-cats"
            className={`w-full ${!isExtraTvCategoriesValid ? "input-error" : ""}`}
            placeholder="e.g. 5100,5200"
            value={extraTvCategories}
            onChange={(e) => setExtraTvCategories(e.target.value)}
            disabled={ignoreCategoryFilter}
          />
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Tooltip content="Send no cat= param — escape hatch for indexers with fully custom category schemas.">
            <Toggle
              id="indexer-ignore-category-filter"
              className="cursor-pointer gap-2 p-0"
              checked={ignoreCategoryFilter}
              onChange={(e) => setIgnoreCategoryFilter(e.target.checked)}
              label={<span className="text-sm text-base-content">Ignore category filter</span>}
            />
          </Tooltip>
        </div>

        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Tooltip content="Use indexer-supplied metadata to filter and rank this indexer's results. Recommended defaults apply when enabled.">
            <Toggle
              id="indexer-filter-enabled"
              className="cursor-pointer gap-2 p-0"
              checked={filterEnabled}
              onChange={(e) => setFilterEnabled(e.target.checked)}
              label={<span className="text-sm text-base-content">Result filtering</span>}
            />
          </Tooltip>
        </div>

        {filterEnabled && (
          <div className="flex flex-col gap-1.5 sm:col-span-2">
            <button
              type="button"
              onClick={() => setFilterAdvancedOpen((o) => !o)}
              style={{
                background: "none",
                border: "none",
                padding: 0,
                color: "inherit",
                cursor: "pointer",
                textDecoration: "underline",
                opacity: 0.85,
                fontSize: "0.9em",
              }}
            >
              {filterAdvancedOpen ? "Hide advanced" : "Show advanced"}
            </button>
          </div>
        )}

        {filterEnabled && filterAdvancedOpen && (
          <>
            <div className="flex flex-col gap-1.5 sm:col-span-2">
              <Tooltip content="Skip items the indexer flags as containing a passworded archive.">
                <Toggle
                  id="indexer-filter-pw"
                  className="cursor-pointer gap-2 p-0"
                  checked={filterSkipPassworded}
                  onChange={(e) => setFilterSkipPassworded(e.target.checked)}
                  label={
                    <span className="text-sm text-base-content">
                      Skip password-protected releases
                    </span>
                  }
                />
              </Tooltip>
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="indexer-filter-mingrabs">
                Minimum download count{" "}
                <span className="text-[11px] font-normal text-base-content/45">
                  (0 = no minimum)
                </span>
              </Label>
              <Input
                type="text"
                id="indexer-filter-mingrabs"
                className="w-full max-w-48"
                placeholder={OPTIMISED_DEFAULTS.MinGrabs.toString()}
                value={filterMinGrabs}
                onChange={(e) => setFilterMinGrabs(e.target.value.replace(/[^0-9]/g, ""))}
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="indexer-filter-grace">
                Grace period for new releases{" "}
                <span className="text-[11px] font-normal text-base-content/45">
                  (hours; 0 = no grace)
                </span>
              </Label>
              <Input
                type="text"
                id="indexer-filter-grace"
                className="w-full max-w-48"
                placeholder={OPTIMISED_DEFAULTS.GrabsGraceHours.toString()}
                value={filterGrabsGraceHours}
                onChange={(e) => setFilterGrabsGraceHours(e.target.value.replace(/[^0-9]/g, ""))}
              />
            </div>

            <div className="flex flex-col gap-1.5 sm:col-span-2">
              <Label htmlFor="indexer-filter-maxage">
                Drop releases older than this many days with zero downloads{" "}
                <span className="text-[11px] font-normal text-base-content/45">(0 = disabled)</span>
              </Label>
              <Input
                type="text"
                id="indexer-filter-maxage"
                className="w-full max-w-48"
                placeholder={OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString()}
                value={filterMaxAgeDaysWithoutGrabs}
                onChange={(e) =>
                  setFilterMaxAgeDaysWithoutGrabs(e.target.value.replace(/[^0-9]/g, ""))
                }
              />
            </div>

            <div className="flex flex-col gap-1.5 sm:col-span-2">
              <Tooltip content="Sort results by download count descending. Items without a count sort below those with one.">
                <Toggle
                  id="indexer-filter-prefer"
                  className="cursor-pointer gap-2 p-0"
                  checked={filterPreferDownloaded}
                  onChange={(e) => setFilterPreferDownloaded(e.target.checked)}
                  label={<span className="text-sm text-base-content">Rank by download count</span>}
                />
              </Tooltip>
            </div>

            <div className="flex flex-col gap-1.5 sm:col-span-2">
              <button
                type="button"
                onClick={resetFilterToDefaults}
                style={{
                  background: "none",
                  border: "none",
                  padding: 0,
                  color: "inherit",
                  cursor: "pointer",
                  textDecoration: "underline",
                  opacity: 0.85,
                  fontSize: "0.9em",
                }}
              >
                Reset to recommended defaults
              </button>
            </div>
          </>
        )}
      </div>

      {testState === "error" && (
        <Alert variant="danger" className="mt-4 text-xs">
          Connection test failed
        </Alert>
      )}

      {testState === "success" && (
        <Alert variant="success" className="mt-4 text-xs">
          Connection test successful!
        </Alert>
      )}
    </Modal>
  );
}
export function isIndexersSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return (
    config["indexers.instances"] !== newConfig["indexers.instances"] ||
    (config["api.user-agent"] ?? "") !== (newConfig["api.user-agent"] ?? "") ||
    (config["api.search-user-agent"] ?? "") !== (newConfig["api.search-user-agent"] ?? "") ||
    (config["search.exclude-patterns"] ?? "") !== (newConfig["search.exclude-patterns"] ?? "") ||
    (config["search.exclude-sync-urls"] ?? "") !== (newConfig["search.exclude-sync-urls"] ?? "") ||
    (config["search.exclude-sync-refresh-minutes"] ?? "") !==
      (newConfig["search.exclude-sync-refresh-minutes"] ?? "") ||
    (config["prowlarr.url"] ?? "") !== (newConfig["prowlarr.url"] ?? "") ||
    (config["prowlarr.api-key"] ?? "") !== (newConfig["prowlarr.api-key"] ?? "") ||
    (config["prowlarr.sync-enabled"] ?? "false") !==
      (newConfig["prowlarr.sync-enabled"] ?? "false") ||
    (config["prowlarr.sync-interval-minutes"] ?? "") !==
      (newConfig["prowlarr.sync-interval-minutes"] ?? "")
  );
}

export function isIndexersSettingsValid(newConfig: Record<string, string>) {
  try {
    const c = parseConfig(newConfig["indexers.instances"] ?? "");
    if (!isProxyUrlValid(c.ProxyUrl ?? "")) return false;
    if (
      c.TimeoutSeconds !== undefined &&
      (!Number.isInteger(c.TimeoutSeconds) || c.TimeoutSeconds <= 0)
    )
      return false;
    if (
      c.SearchResultLimit !== undefined &&
      (!Number.isInteger(c.SearchResultLimit) || c.SearchResultLimit <= 0)
    )
      return false;
    if (
      c.MaxResponseBytes !== undefined &&
      (!Number.isInteger(c.MaxResponseBytes) ||
        c.MaxResponseBytes < 1 ||
        c.MaxResponseBytes > MAX_RESPONSE_BYTES_HARD_CLAMP)
    )
      return false;
    for (const i of c.Indexers) {
      if (!i.Name.trim()) return false;
      if (!i.ApiKey.trim()) return false;
      try {
        new URL(i.Url);
      } catch {
        return false;
      }
      if (!isProxyUrlValid(i.ProxyUrl ?? "")) return false;
      if (
        i.TimeoutSeconds !== undefined &&
        (!Number.isInteger(i.TimeoutSeconds) || i.TimeoutSeconds <= 0)
      )
        return false;
      if (
        i.SearchResultLimit !== undefined &&
        (!Number.isInteger(i.SearchResultLimit) || i.SearchResultLimit <= 0)
      )
        return false;
      if (
        i.MaxResponseBytes !== undefined &&
        (!Number.isInteger(i.MaxResponseBytes) ||
          i.MaxResponseBytes < 1 ||
          i.MaxResponseBytes > MAX_RESPONSE_BYTES_HARD_CLAMP)
      )
        return false;
      if (i.HitLimit !== undefined && (!Number.isInteger(i.HitLimit) || i.HitLimit < 0))
        return false;
      if (
        i.DownloadLimit !== undefined &&
        (!Number.isInteger(i.DownloadLimit) || i.DownloadLimit < 0)
      )
        return false;
      if (
        i.HitLimitResetTime !== undefined &&
        (!Number.isInteger(i.HitLimitResetTime) ||
          i.HitLimitResetTime < 0 ||
          i.HitLimitResetTime > 23)
      )
        return false;
      if (i.ExtraMovieCategories !== undefined && !isCategoryListValid(i.ExtraMovieCategories))
        return false;
      if (i.ExtraTvCategories !== undefined && !isCategoryListValid(i.ExtraTvCategories))
        return false;
    }
    if (validateExcludePatterns(newConfig["search.exclude-patterns"] ?? "").length > 0)
      return false;
    if (validateSyncUrls(newConfig["search.exclude-sync-urls"] ?? "").length > 0) return false;
    const syncRefresh = newConfig["search.exclude-sync-refresh-minutes"] ?? "";
    if (syncRefresh.trim() !== "" && !isRefreshValid(syncRefresh)) return false;

    const prowlarrUrl = newConfig["prowlarr.url"] ?? "";
    const prowlarrApiKey = newConfig["prowlarr.api-key"] ?? "";
    if (!isProwlarrUrlValid(prowlarrUrl)) return false;
    if (!isProwlarrSyncIntervalValid(newConfig["prowlarr.sync-interval-minutes"] ?? ""))
      return false;
    const syncEnabled = newConfig["prowlarr.sync-enabled"] ?? "false";
    if (syncEnabled !== "true" && syncEnabled !== "false") return false;
    if (prowlarrUrl.trim() === "" && prowlarrApiKey.trim() !== "") return false;
    if (prowlarrUrl.trim() !== "" && prowlarrApiKey.trim() === "") return false;
    return true;
  } catch {
    return false;
  }
}
