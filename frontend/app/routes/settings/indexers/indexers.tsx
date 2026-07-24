import { type Dispatch, type SetStateAction, useState, useCallback, useEffect, useMemo } from "react";
import {
    Alert,
    Badge,
    Button,
    Checkbox,
    HelpText,
    Icon,
    Input,
    Label,
    ManagedSetting,
    Modal,
    Select,
    SettingsCard,
    SettingsIntro,
    SettingsPage,
    Spinner,
    Textarea,
    useIsAnyManaged,
} from "~/components/ui";
import { isMaskedSecret } from "~/utils/config-mask";

type IndexersSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
    savedConfig?: Record<string, string>
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
    UseHealthProxy?: boolean;
    ProxyUrl?: string;
    TimeoutSeconds?: number;
    SearchResultLimit?: number;
    HitLimit?: number;
    DownloadLimit?: number;
    HitLimitResetTime?: number;
    ExtraMovieCategories?: string;
    ExtraTvCategories?: string;
    IgnoreCategoryFilter?: boolean;
    Filter?: ResultFilter;
}

interface IndexerConfig {
    ProxyUrl?: string;
    TimeoutSeconds?: number;
    SearchResultLimit?: number;
    Indexers: ConnectionDetails[];
}

// Hard fallback when neither the indexer nor the global override sets a timeout.
// Mirrors IndexerConfig.DefaultTimeoutSeconds in the backend.
const DEFAULT_TIMEOUT_SECONDS = 30;

// Hard fallback for results gathered per indexer per search; above this the indexer is paged.
// Mirrors IndexerConfig.DefaultSearchResultLimit in the backend.
const DEFAULT_SEARCH_RESULT_LIMIT = 100;

type PatternIssue = { line: number, pattern: string, error: string };

function validateExcludePatterns(raw: string): PatternIssue[] {
    const issues: PatternIssue[] = [];
    const lines = raw.split("\n");
    for (let i = 0; i < lines.length; i++) {
        const trimmed = lines[i].trim();
        if (trimmed.length === 0 || trimmed.startsWith("#")) continue;
        try {
            new RegExp(trimmed, "i");
        } catch (e: any) {
            issues.push({ line: i + 1, pattern: trimmed, error: e?.message ?? "invalid regex" });
        }
    }
    return issues;
}

type ExcludeSyncUrlStatus = {
    url: string,
    count: number,
    fetchedAt: number | null,
    lastChecked: number | null,
    error: string | null,
};

type SyncUrlIssue = { line: number, value: string, error: string };

function validateSyncUrls(raw: string): SyncUrlIssue[] {
    const issues: SyncUrlIssue[] = [];
    const lines = raw.split("\n");
    for (let i = 0; i < lines.length; i++) {
        const trimmed = lines[i].trim();
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

function syncHostLabel(url: string): string {
    try { return new URL(url).host; } catch { return url; }
}

function syncRelativeTime(unixSeconds: number): string {
    const diff = Math.floor(Date.now() / 1000) - unixSeconds;
    if (diff < 60) return "just now";
    if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
    if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
    return `${Math.floor(diff / 86400)}d ago`;
}

function parseConfig(raw: string): IndexerConfig {
    try {
        const parsed = JSON.parse(raw || "{}");
        return {
            ProxyUrl: parsed.ProxyUrl ?? "",
            TimeoutSeconds: typeof parsed.TimeoutSeconds === "number" ? parsed.TimeoutSeconds : undefined,
            SearchResultLimit: typeof parsed.SearchResultLimit === "number" ? parsed.SearchResultLimit : undefined,
            Indexers: parsed.Indexers ?? [],
        };
    } catch {
        return { ProxyUrl: "", Indexers: [] };
    }
}

function serializeConfig(c: IndexerConfig): string {
    const out: IndexerConfig = { Indexers: c.Indexers };
    if (c.ProxyUrl && c.ProxyUrl.trim()) out.ProxyUrl = c.ProxyUrl.trim();
    if (typeof c.TimeoutSeconds === "number" && c.TimeoutSeconds > 0) out.TimeoutSeconds = c.TimeoutSeconds;
    if (typeof c.SearchResultLimit === "number" && c.SearchResultLimit > 0) out.SearchResultLimit = c.SearchResultLimit;
    return JSON.stringify(out);
}

// Positive integer (or empty string = "use fallback"). Rejects decimals and negatives.
function isTimeoutValid(raw: string): boolean {
    if (!raw.trim()) return true;
    const n = Number(raw);
    return Number.isInteger(n) && n > 0 && raw.trim() === n.toString();
}

function isCategoryListValid(raw: string): boolean {
    if (!raw.trim()) return true;
    const parts = raw.split(",").map(p => p.trim()).filter(p => p.length > 0);
    if (parts.length === 0) return true;
    return parts.every(p => /^\d+$/.test(p));
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

export function IndexersSettings({ config, setNewConfig, savedConfig }: IndexersSettingsProps) {
    const indexerConfig = useMemo(() => parseConfig(config["indexers.instances"]), [config]);
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

    const handleDelete = useCallback((index: number) => {
        const next: IndexerConfig = {
            ...indexerConfig,
            Indexers: indexerConfig.Indexers.filter((_, i) => i !== index),
        };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleToggle = useCallback((index: number) => {
        const next: IndexerConfig = {
            ...indexerConfig,
            Indexers: indexerConfig.Indexers.map((x, i) =>
                i === index ? { ...x, Enabled: !x.Enabled } : x
            ),
        };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSave = useCallback((indexer: ConnectionDetails) => {
        const next: IndexerConfig = { ...indexerConfig, Indexers: [...indexerConfig.Indexers] };
        if (editingIndex !== null) {
            next.Indexers[editingIndex] = indexer;
        } else {
            next.Indexers.push(indexer);
        }
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
        handleCloseModal();
    }, [config, indexerConfig, editingIndex, setNewConfig, handleCloseModal]);

    const handleProxyChange = useCallback((value: string) => {
        const next: IndexerConfig = { ...indexerConfig, ProxyUrl: value };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleTimeoutChange = useCallback((value: string) => {
        const trimmed = value.replace(/[^0-9]/g, "");
        const n = trimmed === "" ? undefined : parseInt(trimmed, 10);
        const next: IndexerConfig = { ...indexerConfig, TimeoutSeconds: n && n > 0 ? n : undefined };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleSearchLimitChange = useCallback((value: string) => {
        const trimmed = value.replace(/[^0-9]/g, "");
        const n = trimmed === "" ? undefined : parseInt(trimmed, 10);
        const next: IndexerConfig = { ...indexerConfig, SearchResultLimit: n && n > 0 ? n : undefined };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const excludePatterns = config["search.exclude-patterns"] ?? "";
    const patternIssues = useMemo(() => validateExcludePatterns(excludePatterns), [excludePatterns]);
    const handleExcludePatternsChange = useCallback((value: string) => {
        setNewConfig({ ...config, "search.exclude-patterns": value });
    }, [config, setNewConfig]);

    const excludeSyncUrls = config["search.exclude-sync-urls"] ?? "";
    const excludeSyncRefresh = config["search.exclude-sync-refresh-minutes"] ?? "";
    const syncUrlIssues = useMemo(() => validateSyncUrls(excludeSyncUrls), [excludeSyncUrls]);
    const handleSyncUrlsChange = useCallback((value: string) => {
        setNewConfig({ ...config, "search.exclude-sync-urls": value });
    }, [config, setNewConfig]);
    const handleSyncRefreshChange = useCallback((value: string) => {
        const cleaned = value.replace(/[^0-9]/g, "");
        setNewConfig({ ...config, "search.exclude-sync-refresh-minutes": cleaned });
    }, [config, setNewConfig]);

    const [syncStatus, setSyncStatus] = useState<ExcludeSyncUrlStatus[]>([]);
    const [isSyncing, setIsSyncing] = useState(false);
    const loadSyncStatus = useCallback(async () => {
        try {
            const res = await fetch("/settings/exclude-sync");
            if (res.ok) setSyncStatus((await res.json()).urls ?? []);
        } catch {
            // status is best-effort; ignore transient failures
        }
    }, []);
    // Load on mount, and re-pull after a save changes the synced URLs. The backend
    // refetches on config change, so poll once immediately and once after it settles.
    const savedSyncUrls = savedConfig?.["search.exclude-sync-urls"] ?? "";
    useEffect(() => {
        void loadSyncStatus();
        const timer = setTimeout(() => { void loadSyncStatus(); }, 2000);
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
            const res = await fetch("/settings/exclude-sync", { method: "POST" });
            if (res.ok) setSyncStatus((await res.json()).urls ?? []);
        } catch {
            // ignore; the row shows the backend-reported error on the next status load
        } finally {
            setIsSyncing(false);
        }
    }, [excludeSyncManaged]);

    const defaultSearchUserAgent = config["api.search-user-agent"] ?? "";
    const handleSearchUserAgentChange = useCallback((value: string) => {
        setNewConfig({ ...config, "api.search-user-agent": value });
    }, [config, setNewConfig]);

    const defaultRetrieveUserAgent = config["api.user-agent"] ?? "";
    const handleRetrieveUserAgentChange = useCallback((value: string) => {
        setNewConfig({ ...config, "api.user-agent": value });
    }, [config, setNewConfig]);

    const proxyUrl = indexerConfig.ProxyUrl ?? "";
    const proxyValid = isProxyUrlValid(proxyUrl);
    const globalTimeoutRaw = typeof indexerConfig.TimeoutSeconds === "number" && indexerConfig.TimeoutSeconds > 0
        ? indexerConfig.TimeoutSeconds.toString()
        : "";
    const globalSearchLimitRaw = typeof indexerConfig.SearchResultLimit === "number" && indexerConfig.SearchResultLimit > 0
        ? indexerConfig.SearchResultLimit.toString()
        : "";

    return (
        <SettingsPage className="mb-6">
            <SettingsIntro>
                Configure shared search behavior, filter unwanted results, and manage the Newznab-compatible
                indexers NzbDAV queries.
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
                            onChange={e => handleProxyChange(e.target.value)}
                        />
                    </div>
                    </ManagedSetting>
                    <ManagedSetting configKey="api.search-user-agent" className="sm:col-span-2">
                    <div className="flex flex-col gap-1.5">
                        <Label htmlFor="indexers-default-search-user-agent">
                            Default Search User-Agent <span className="text-[11px] font-normal text-base-content/45">(sent when searching indexers; per-indexer override below)</span>
                        </Label>
                        <Input
                            type="text"
                            id="indexers-default-search-user-agent"
                            className="w-full"
                            placeholder="nzbdav/<version>"
                            value={defaultSearchUserAgent}
                            onChange={e => handleSearchUserAgentChange(e.target.value)}
                        />
                        <HelpText>
                            Sent on indexer search and caps queries. Leave blank to use the default.
                        </HelpText>
                    </div>
                    </ManagedSetting>
                    <ManagedSetting configKey="api.user-agent" className="sm:col-span-2">
                    <div className="flex flex-col gap-1.5">
                        <Label htmlFor="indexers-default-retrieve-user-agent">
                            Default Retrieve User-Agent <span className="text-[11px] font-normal text-base-content/45">(sent when retrieving the .nzb; per-indexer override below)</span>
                        </Label>
                        <Input
                            type="text"
                            id="indexers-default-retrieve-user-agent"
                            className="w-full"
                            placeholder="nzbdav/<version>"
                            value={defaultRetrieveUserAgent}
                            onChange={e => handleRetrieveUserAgentChange(e.target.value)}
                        />
                        <HelpText>
                            Sent when retrieving the .nzb file. Leave blank to use the default.
                        </HelpText>
                    </div>
                    </ManagedSetting>
                    <ManagedSetting configKey="indexers.instances" className="sm:col-span-2 space-y-3.5">
                    <div className="flex flex-col gap-1.5">
                        <Label htmlFor="indexers-default-timeout">
                            Request timeout (seconds) <span className="text-[11px] font-normal text-base-content/45">(leave blank for {DEFAULT_TIMEOUT_SECONDS}s default)</span>
                        </Label>
                        <Input
                            type="text"
                            id="indexers-default-timeout"
                            className={`w-full ${!isTimeoutValid(globalTimeoutRaw) ? "input-error" : ""}`}
                            placeholder={DEFAULT_TIMEOUT_SECONDS.toString()}
                            value={globalTimeoutRaw}
                            onChange={e => handleTimeoutChange(e.target.value)}
                        />
                    </div>
                    <div className="flex flex-col gap-1.5">
                        <Label htmlFor="indexers-default-search-limit">
                            Search results per indexer <span className="text-[11px] font-normal text-base-content/45">(blank = {DEFAULT_SEARCH_RESULT_LIMIT}; higher pages the indexer for more results, using more API calls)</span>
                        </Label>
                        <Input
                            type="text"
                            id="indexers-default-search-limit"
                            className={`w-full ${!isTimeoutValid(globalSearchLimitRaw) ? "input-error" : ""}`}
                            placeholder={DEFAULT_SEARCH_RESULT_LIMIT.toString()}
                            value={globalSearchLimitRaw}
                            onChange={e => handleSearchLimitChange(e.target.value)}
                        />
                    </div>
                    </ManagedSetting>
            </SettingsCard>

            <SettingsCard
                icon="filter_alt"
                title="Result exclusions"
                description="Drop matching releases with local patterns or synchronized external lists."
            >
                    <ManagedSetting configKey="search.exclude-patterns" className="sm:col-span-2">
                    <div className="flex flex-col gap-1.5">
                        <Label htmlFor="indexers-exclude-patterns">
                            Exclude result patterns <span className="text-[11px] font-normal text-base-content/45">(applies to every indexer)</span>
                        </Label>
                        <Textarea
                            id="indexers-exclude-patterns"
                            rows={6}
                            spellCheck={false}
                            className={`w-full font-mono text-xs ${patternIssues.length > 0 ? "input-error" : ""}`}
                            placeholder={"# one regex per line\n# lines starting with # are comments"}
                            value={excludePatterns}
                            onChange={e => handleExcludePatternsChange(e.target.value)} />
                        {patternIssues.length > 0 && (
                            <div className="flex flex-col gap-1 rounded-md border border-error/35 bg-error/10 p-2.5 text-xs">
                                {patternIssues.map((iss, i) => (
                                    <div key={i} className="flex flex-wrap items-baseline gap-1.5 text-base-content">
                                        <span className="shrink-0 font-semibold text-error">Line {iss.line}</span>
                                        <code className="rounded bg-error/10 px-1.5 py-0.5 font-mono text-error">{iss.pattern}</code>
                                        <span className="text-base-content/60">— {iss.error}</span>
                                    </div>
                                ))}
                            </div>
                        )}
                        <HelpText>
                            One JavaScript-style regex per line. Search results whose title matches any pattern
                            are dropped before being returned. Case-insensitive by default — use <code>(?-i:Foo)</code> for
                            case-sensitive. Lines starting with <code>#</code> are comments. Use this to skip
                            releases your setup can't handle, whatever the reason.
                        </HelpText>
                    </div>
                    </ManagedSetting>

                    <ManagedSetting configKeys={[
                        "search.exclude-sync-urls",
                        "search.exclude-sync-refresh-minutes",
                    ]} className="sm:col-span-2">
                    <div className="flex flex-col gap-1.5">
                        <Label htmlFor="indexers-exclude-sync-urls">
                            Synced exclude URLs <span className="text-[11px] font-normal text-base-content/45">(auto-updating; one URL per line)</span>
                        </Label>
                        <Textarea
                            id="indexers-exclude-sync-urls"
                            rows={3}
                            spellCheck={false}
                            className={`w-full font-mono text-xs ${syncUrlIssues.length > 0 ? "input-error" : ""}`}
                            placeholder={"# one URL per line\nhttps://raw.githubusercontent.com/.../excluded-regex.json"}
                            value={excludeSyncUrls}
                            onChange={e => handleSyncUrlsChange(e.target.value)} />
                        {syncUrlIssues.length > 0 && (
                            <div className="flex flex-col gap-1 rounded-md border border-error/35 bg-error/10 p-2.5 text-xs">
                                {syncUrlIssues.map((iss, i) => (
                                    <div key={i} className="flex flex-wrap items-baseline gap-1.5 text-base-content">
                                        <span className="shrink-0 font-semibold text-error">Line {iss.line}</span>
                                        <code className="rounded bg-error/10 px-1.5 py-0.5 font-mono text-error">{iss.value}</code>
                                        <span className="text-base-content/60">— {iss.error}</span>
                                    </div>
                                ))}
                            </div>
                        )}
                        <div className="mt-2.5 flex flex-wrap items-center gap-2.5">
                            <Label htmlFor="indexers-exclude-sync-refresh" className="text-sm font-normal text-base-content/80">
                                Refresh every
                            </Label>
                            <Input
                                type="text"
                                inputMode="numeric"
                                id="indexers-exclude-sync-refresh"
                                className={`w-[90px] ${!isRefreshValid(excludeSyncRefresh) ? "input-error" : ""}`}
                                placeholder="720"
                                value={excludeSyncRefresh}
                                onChange={e => handleSyncRefreshChange(e.target.value)} />
                            <span className="text-[11px] text-base-content/45">minutes</span>
                            <Button
                                variant="primary"
                                size="small"
                                onClick={handleSyncNow}
                                disabled={isSyncing || excludeSyncManaged}
                                title={excludeSyncManaged
                                    ? "Synced exclude URLs are managed by NZBDAV_CONFIG__... — change the container environment and restart"
                                    : undefined}
                            >
                                <Icon name={isSyncing ? "progress_activity" : "sync"} className={`!text-[18px] ${isSyncing ? "animate-spin" : ""}`} />
                                {isSyncing ? "Syncing…" : "Sync now"}
                            </Button>
                        </div>
                        {syncStatus.length > 0 && (
                            <div className="mt-2.5 flex flex-col gap-1">
                                {syncStatus.map((s, i) => (
                                    <div key={i} className="overflow-wrap-anywhere text-[13px] leading-snug">
                                        {s.error
                                            ? <span className="text-error">✗ {syncHostLabel(s.url)} — {s.error}</span>
                                            : <span className="text-success">✓ {syncHostLabel(s.url)} — {s.count} pattern{s.count === 1 ? "" : "s"}{s.lastChecked ? ` · synced ${syncRelativeTime(s.lastChecked)}` : ""}</span>}
                                    </div>
                                ))}
                            </div>
                        )}
                        <HelpText>
                            Point at one or more JSON lists of regex patterns (e.g. TRaSH-derived exclude URLs).
                            Accepts <code>{`{ "values": ["…"] }`}</code> or <code>{`[{ "pattern": "…" }]`}</code>.
                            Synced patterns are fetched on the interval above and take precedence; your manual
                            patterns above are merged in after, with exact duplicates removed. If a URL can't be
                            reached, the last good copy keeps working. Save your changes first, then use <strong>Sync now</strong>.
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
                    <Button size="xsmall" onClick={handleAdd}>Add</Button>
                }
            >
                {indexerConfig.Indexers.length === 0 ? (
                    <p className="rounded border border-base-content/10 bg-base-200/40 px-5 py-5 text-sm italic text-base-content/60">
                        No indexers configured. Add a Newznab-compatible indexer (or aggregator) to enable search.
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
                indexer={editingIndex !== null ? indexerConfig.Indexers[editingIndex] : null}
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
    const host = (() => {
        try { return new URL(indexer.Url).host; }
        catch { return indexer.Url || "—"; }
    })();
    const rateLimit = indexer.MaxRequestsPerMinute && indexer.MaxRequestsPerMinute > 0
        ? `${indexer.MaxRequestsPerMinute} / min`
        : "Unlimited";
    const searchUserAgent = indexer.SearchUserAgent?.trim() || indexer.UserAgent?.trim() || "Default";
    const retrieveUserAgent = indexer.RetrieveUserAgent?.trim() || indexer.UserAgent?.trim() || "Default";
    const proxy = indexer.ProxyUrl?.trim() ? indexer.ProxyUrl : "Default";
    const timeout = indexer.TimeoutSeconds && indexer.TimeoutSeconds > 0
        ? `${indexer.TimeoutSeconds}s`
        : "Default";
    const resultLimit = indexer.SearchResultLimit && indexer.SearchResultLimit > 0
        ? indexer.SearchResultLimit.toString()
        : "Default";
    const formatLimit = (n: number | undefined, perDay: boolean) => {
        if (!n || n <= 0) return "Unlimited";
        return perDay ? `${n} / day` : `${n} / 24h`;
    };
    const hasResetHour = typeof indexer.HitLimitResetTime === "number"
        && indexer.HitLimitResetTime >= 0
        && indexer.HitLimitResetTime <= 23;
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
        <div className={`card border border-base-content/10 bg-base-100 shadow-sm ${isDisabled ? "opacity-60" : ""}`}>
            <div className="card-body gap-3 p-4">
                <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0 flex-1">
                        <div className="break-all text-[15px] font-semibold leading-snug tracking-tight text-base-content">
                            {indexer.Name || "(unnamed)"}
                            {isDisabled && <Badge className="badge-ghost badge-sm ml-2 align-middle">Disabled</Badge>}
                        </div>
                        <div className="break-all text-[10px] font-medium uppercase tracking-wide text-base-content/50">{host}</div>
                    </div>
                    <div className="flex shrink-0 gap-1">
                        <button
                            type="button"
                            className={`btn btn-ghost btn-sm btn-square ${isDisabled ? "text-base-content/40" : "text-success"}`}
                            onClick={onToggle}
                            title={isDisabled ? "Enable Indexer" : "Disable Indexer"}
                            aria-pressed={!isDisabled}
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
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
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
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
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
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
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="12" cy="12" r="10" />
                                    <line x1="2" y1="12" x2="22" y2="12" />
                                    <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Host</span>
                                <span className="truncate text-sm font-medium text-base-content" title={indexer.Url}>{host}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="12" cy="12" r="10" />
                                    <polyline points="12 6 12 12 16 14" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Rate limit</span>
                                <span className="truncate text-sm font-medium text-base-content">{rateLimit}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M9 12l2 2 4-4" />
                                    <path d="M21 12c0 4.97-4.03 9-9 9s-9-4.03-9-9 4.03-9 9-9 9 4.03 9 9z" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Strict matching</span>
                                <span className="truncate text-sm font-medium text-base-content">
                                    {indexer.EnableStrictMatching ? "Enabled" : "Disabled"}
                                </span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="11" cy="11" r="8" />
                                    <line x1="21" y1="21" x2="16.65" y2="16.65" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Search UA</span>
                                <span className="truncate text-sm font-medium text-base-content" title={indexer.SearchUserAgent ?? indexer.UserAgent ?? ""}>{searchUserAgent}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                                    <polyline points="7 10 12 15 17 10" />
                                    <line x1="12" y1="15" x2="12" y2="3" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Retrieve UA</span>
                                <span className="truncate text-sm font-medium text-base-content" title={indexer.RetrieveUserAgent ?? indexer.UserAgent ?? ""}>{retrieveUserAgent}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Result filtering</span>
                                <span className="truncate text-sm font-medium text-base-content">
                                    {indexer.Filter?.Enabled ? "Enabled" : "Disabled"}
                                </span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <rect x="2" y="6" width="20" height="12" rx="2" />
                                    <path d="M6 12h.01M10 12h.01M14 12h.01M18 12h.01" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Proxy</span>
                                <span className="truncate text-sm font-medium text-base-content" title={indexer.ProxyUrl ?? ""}>{proxy}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="12" cy="12" r="10" />
                                    <polyline points="12 6 12 12 16 14" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Timeout</span>
                                <span className="truncate text-sm font-medium text-base-content">{timeout}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <line x1="8" y1="6" x2="21" y2="6" />
                                    <line x1="8" y1="12" x2="21" y2="12" />
                                    <line x1="8" y1="18" x2="21" y2="18" />
                                    <line x1="3" y1="6" x2="3.01" y2="6" />
                                    <line x1="3" y1="12" x2="3.01" y2="12" />
                                    <line x1="3" y1="18" x2="3.01" y2="18" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Result limit</span>
                                <span className="truncate text-sm font-medium text-base-content">{resultLimit}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M3 12h4l3-9 4 18 3-9h4" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">API limit</span>
                                <span className="truncate text-sm font-medium text-base-content">{apiLimit}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                                    <polyline points="7 10 12 15 17 10" />
                                    <line x1="12" y1="15" x2="12" y2="3" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Download limit</span>
                                <span className="truncate text-sm font-medium text-base-content">{downloadLimit}</span>
                            </div>
                        </div>

                        <div className="flex min-w-0 items-center gap-2.5 rounded-md border border-base-content/10 bg-base-200/40 px-2.5 py-2">
                            <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded bg-base-300 text-base-content/60">
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M7 7h.01M7 3h5a2 2 0 0 1 1.41.59l7 7a2 2 0 0 1 0 2.82l-7 7a2 2 0 0 1-2.82 0l-7-7A2 2 0 0 1 3 12V7a4 4 0 0 1 4-4z" />
                                </svg>
                            </div>
                            <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">Categories</span>
                                <span className="truncate text-sm font-medium text-base-content" title={categoriesSummary}>{categoriesSummary}</span>
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
    const [maxRpm, setMaxRpm] = useState("0");
    const [hitLimit, setHitLimit] = useState("");
    const [downloadLimit, setDownloadLimit] = useState("");
    const [hitResetTime, setHitResetTime] = useState("");
    const [enabled, setEnabled] = useState(true);
    const [strict, setStrict] = useState(false);
    const [useHealthProxy, setUseHealthProxy] = useState(false);
    const [extraMovieCategories, setExtraMovieCategories] = useState("");
    const [extraTvCategories, setExtraTvCategories] = useState("");
    const [ignoreCategoryFilter, setIgnoreCategoryFilter] = useState(false);

    const [filterEnabled, setFilterEnabled] = useState(false);
    const [filterAdvancedOpen, setFilterAdvancedOpen] = useState(false);
    const [filterSkipPassworded, setFilterSkipPassworded] = useState(OPTIMISED_DEFAULTS.SkipPassworded);
    const [filterMinGrabs, setFilterMinGrabs] = useState(OPTIMISED_DEFAULTS.MinGrabs.toString());
    const [filterGrabsGraceHours, setFilterGrabsGraceHours] = useState(OPTIMISED_DEFAULTS.GrabsGraceHours.toString());
    const [filterMaxAgeDaysWithoutGrabs, setFilterMaxAgeDaysWithoutGrabs] = useState(OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString());
    const [filterPreferDownloaded, setFilterPreferDownloaded] = useState(OPTIMISED_DEFAULTS.PreferDownloaded);

    const resetFilterToDefaults = useCallback(() => {
        setFilterSkipPassworded(OPTIMISED_DEFAULTS.SkipPassworded);
        setFilterMinGrabs(OPTIMISED_DEFAULTS.MinGrabs.toString());
        setFilterGrabsGraceHours(OPTIMISED_DEFAULTS.GrabsGraceHours.toString());
        setFilterMaxAgeDaysWithoutGrabs(OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString());
        setFilterPreferDownloaded(OPTIMISED_DEFAULTS.PreferDownloaded);
    }, []);

    const [testState, setTestState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');
    const apiKeyIsMasked = isMaskedSecret(apiKey);

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
                    : ""
            );
            setSearchResultLimit(indexer?.SearchResultLimit && indexer.SearchResultLimit > 0 ? indexer.SearchResultLimit.toString() : "");
            setMaxRpm((indexer?.MaxRequestsPerMinute ?? 0).toString());
            setHitLimit(indexer?.HitLimit && indexer.HitLimit > 0 ? indexer.HitLimit.toString() : "");
            setDownloadLimit(indexer?.DownloadLimit && indexer.DownloadLimit > 0 ? indexer.DownloadLimit.toString() : "");
            setHitResetTime(
                typeof indexer?.HitLimitResetTime === "number" && indexer.HitLimitResetTime >= 0 && indexer.HitLimitResetTime <= 23
                    ? indexer.HitLimitResetTime.toString()
                    : ""
            );
            setEnabled(indexer?.Enabled ?? true);
            setStrict(indexer?.EnableStrictMatching ?? false);
            setUseHealthProxy(indexer?.UseHealthProxy ?? false);
            setExtraMovieCategories(indexer?.ExtraMovieCategories ?? "");
            setExtraTvCategories(indexer?.ExtraTvCategories ?? "");
            setIgnoreCategoryFilter(indexer?.IgnoreCategoryFilter ?? false);
            const f = indexer?.Filter ?? OPTIMISED_DEFAULTS;
            setFilterEnabled(f.Enabled);
            setFilterSkipPassworded(f.SkipPassworded);
            setFilterMinGrabs((f.MinGrabs ?? OPTIMISED_DEFAULTS.MinGrabs).toString());
            setFilterGrabsGraceHours((f.GrabsGraceHours ?? OPTIMISED_DEFAULTS.GrabsGraceHours).toString());
            setFilterMaxAgeDaysWithoutGrabs((f.MaxAgeDaysWithoutGrabs ?? OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs).toString());
            setFilterPreferDownloaded(f.PreferDownloaded);
            setFilterAdvancedOpen(false);
            setTestState('idle');
        }
    }, [show, indexer]);

    useEffect(() => { setTestState('idle'); }, [url, apiKey, searchUserAgent, proxyUrl, timeoutSeconds, skipTlsVerification, useHealthProxy]);

    const handleTest = useCallback(async () => {
        if (!url.trim() || !apiKey.trim() || apiKeyIsMasked) return;
        setTestState('testing');
        try {
            const fd = new FormData();
            fd.append('url', url);
            fd.append('apiKey', apiKey);
            if (searchUserAgent.trim()) fd.append('userAgent', searchUserAgent);
            if (proxyUrl.trim()) fd.append('proxyUrl', proxyUrl);
            if (timeoutSeconds.trim()) fd.append('timeoutSeconds', timeoutSeconds);
            fd.append('skipTlsVerification', skipTlsVerification.toString());
            if (useHealthProxy) fd.append('useHealthProxy', 'true');
            const r = await fetch('/api/test-indexer-connection', { method: 'POST', body: fd });
            const data = await r.json();
            setTestState(data.status && data.connected ? 'success' : 'error');
        } catch {
            setTestState('error');
        }
    }, [url, apiKey, apiKeyIsMasked, searchUserAgent, proxyUrl, timeoutSeconds, skipTlsVerification, useHealthProxy]);

    const handleSave = useCallback(() => {
        const rpm = parseInt(maxRpm || "0", 10);
        const timeout = parseInt(timeoutSeconds || "0", 10);
        const srl = parseInt(searchResultLimit || "0", 10);
        const hl = parseInt(hitLimit || "0", 10);
        const dl = parseInt(downloadLimit || "0", 10);
        const hr = hitResetTime.trim() === "" ? NaN : parseInt(hitResetTime, 10);
        const clampNonNegInt = (raw: string, fallback: number) => {
            const n = parseInt(raw || "0", 10);
            return Number.isFinite(n) && n >= 0 ? n : fallback;
        };
        const filterIsClean = !filterEnabled
            && filterSkipPassworded === OPTIMISED_DEFAULTS.SkipPassworded
            && clampNonNegInt(filterMinGrabs, OPTIMISED_DEFAULTS.MinGrabs) === OPTIMISED_DEFAULTS.MinGrabs
            && clampNonNegInt(filterGrabsGraceHours, OPTIMISED_DEFAULTS.GrabsGraceHours) === OPTIMISED_DEFAULTS.GrabsGraceHours
            && clampNonNegInt(filterMaxAgeDaysWithoutGrabs, OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs) === OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs
            && filterPreferDownloaded === OPTIMISED_DEFAULTS.PreferDownloaded;
        const normaliseCategoryList = (raw: string) => {
            const parts = raw.split(",").map(p => p.trim()).filter(p => p.length > 0);
            return parts.length === 0 ? undefined : parts.join(",");
        };
        onSave({
            Name: name.trim(),
            Url: url.trim(),
            ApiKey: apiKey.trim(),
            Enabled: enabled,
            UserAgent: undefined,
            SearchUserAgent: searchUserAgent.trim() || undefined,
            RetrieveUserAgent: retrieveUserAgent.trim() || undefined,
            SkipTlsVerification: url.trim().toLowerCase().startsWith("https://") && skipTlsVerification
                ? true
                : undefined,
            ProxyUrl: proxyUrl.trim() || undefined,
            TimeoutSeconds: Number.isFinite(timeout) && timeout > 0 ? timeout : undefined,
            SearchResultLimit: Number.isFinite(srl) && srl > 0 ? srl : undefined,
            MaxRequestsPerMinute: Number.isFinite(rpm) && rpm > 0 ? rpm : 0,
            HitLimit: Number.isFinite(hl) && hl > 0 ? hl : undefined,
            DownloadLimit: Number.isFinite(dl) && dl > 0 ? dl : undefined,
            HitLimitResetTime: Number.isFinite(hr) && hr >= 0 && hr <= 23 ? hr : undefined,
            EnableStrictMatching: strict,
            UseHealthProxy: useHealthProxy || undefined,
            ExtraMovieCategories: normaliseCategoryList(extraMovieCategories),
            ExtraTvCategories: normaliseCategoryList(extraTvCategories),
            IgnoreCategoryFilter: ignoreCategoryFilter || undefined,
            Filter: filterIsClean ? undefined : {
                Enabled: filterEnabled,
                SkipPassworded: filterSkipPassworded,
                MinGrabs: clampNonNegInt(filterMinGrabs, 0),
                GrabsGraceHours: clampNonNegInt(filterGrabsGraceHours, 6),
                MaxAgeDaysWithoutGrabs: clampNonNegInt(filterMaxAgeDaysWithoutGrabs, 0),
                PreferDownloaded: filterPreferDownloaded,
            },
        });
    }, [name, url, apiKey, searchUserAgent, retrieveUserAgent, skipTlsVerification, proxyUrl, timeoutSeconds, searchResultLimit, maxRpm, hitLimit, downloadLimit, hitResetTime, enabled, strict, useHealthProxy,
        extraMovieCategories, extraTvCategories, ignoreCategoryFilter,
        filterEnabled, filterSkipPassworded, filterMinGrabs, filterGrabsGraceHours,
        filterMaxAgeDaysWithoutGrabs, filterPreferDownloaded, onSave]);

    const isUrlValid = (() => {
        if (!url.trim()) return false;
        try { new URL(url); return true; } catch { return false; }
    })();
    const isHttpsUrl = url.trim().toLowerCase().startsWith("https://");
    const isRpmValid = (() => {
        const n = Number(maxRpm);
        return Number.isInteger(n) && n >= 0 && maxRpm.trim() === n.toString();
    })();
    const isProxyValid = isProxyUrlValid(proxyUrl);
    const isTimeoutFieldValid = isTimeoutValid(timeoutSeconds);
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
    const isFormValid = name.trim() !== "" && isUrlValid && apiKey.trim() !== ""
        && isRpmValid && isProxyValid && isTimeoutFieldValid
        && isHitLimitValid && isSearchResultLimitValid && isDownloadLimitValid && isHitResetValid
        && isExtraMovieCategoriesValid && isExtraTvCategoriesValid;

    return (
        <Modal
            open={show}
            title={indexer ? "Edit Indexer" : "Add Indexer"}
            onClose={onClose}
            className="!max-w-2xl"
            footer={
                <>
                    <Button
                        variant={testState === 'success' ? 'success' : testState === 'error' ? 'danger' : 'secondary'}
                        onClick={handleTest}
                        disabled={!isUrlValid || !apiKey.trim() || apiKeyIsMasked || testState === 'testing'}
                        title={apiKeyIsMasked ? "Enter a new API key to test this connection" : undefined}
                    >
                        {testState === 'testing'
                            ? <Spinner size="sm" />
                            : testState === 'success'
                                ? '✓ Tested'
                                : testState === 'error'
                                    ? '✗ Failed'
                                    : 'Test Connection'}
                    </Button>
                    <Button variant="outline" onClick={onClose}>Cancel</Button>
                    <Button onClick={handleSave} disabled={!isFormValid}>
                        {indexer ? "Save Indexer" : "Add Indexer"}
                    </Button>
                </>
            }
        >
            <div className="grid grid-cols-1 gap-3.5 sm:grid-cols-2">
                        <div className="flex flex-col gap-1.5">
                            <Label htmlFor="indexer-name">Name</Label>
                            <Input
                                type="text"
                                id="indexer-name"
                                className="w-full"
                                placeholder="e.g. My Indexer"
                                value={name}
                                onChange={e => setName(e.target.value)}
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
                                onChange={e => setUrl(e.target.value)}
                            />
                        </div>
                        {isHttpsUrl && (
                            <div className="flex flex-col gap-1.5 sm:col-span-2">
                                <label htmlFor="indexer-skip-tls-verification" className="flex items-center gap-2">
                                    <Checkbox
                                        id="indexer-skip-tls-verification"
                                        checked={skipTlsVerification}
                                        onChange={e => setSkipTlsVerification(e.target.checked)}
                                    />
                                    <span className="text-sm text-base-content/80">Skip TLS certificate verification</span>
                                </label>
                                {skipTlsVerification && (
                                    <Alert variant="warning" className="text-xs">
                                        TLS remains encrypted, but this accepts an untrusted or mismatched certificate.
                                        Only enable it for an indexer you trust.
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
                                onChange={e => setApiKey(e.target.value)}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5 sm:col-span-2">
                            <Label htmlFor="indexer-search-ua">
                                Search User-Agent <span className="text-[11px] font-normal text-base-content/45">(optional; overrides the global Search default)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-search-ua"
                                className="w-full"
                                placeholder="Leave blank to use global default"
                                value={searchUserAgent}
                                onChange={e => setSearchUserAgent(e.target.value)}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5 sm:col-span-2">
                            <Label htmlFor="indexer-retrieve-ua">
                                Retrieve User-Agent <span className="text-[11px] font-normal text-base-content/45">(optional; overrides the global Retrieve default)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-retrieve-ua"
                                className="w-full"
                                placeholder="Leave blank to use global default"
                                value={retrieveUserAgent}
                                onChange={e => setRetrieveUserAgent(e.target.value)}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5 sm:col-span-2">
                            <Label htmlFor="indexer-proxy">
                                HTTP(S) Proxy URL <span className="text-[11px] font-normal text-base-content/45">(optional; overrides the global default)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-proxy"
                                className={`w-full ${!isProxyValid && proxyUrl !== "" ? "input-error" : ""}`}
                                placeholder="Leave blank to use global default"
                                value={proxyUrl}
                                onChange={e => setProxyUrl(e.target.value)}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5">
                            <Label htmlFor="indexer-rpm">
                                Max requests / minute <span className="text-[11px] font-normal text-base-content/45">(0 = unlimited)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-rpm"
                                className={`w-full ${!isRpmValid && maxRpm !== "" ? "input-error" : ""}`}
                                placeholder="0"
                                value={maxRpm}
                                onChange={e => setMaxRpm(e.target.value)}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5">
                            <Label htmlFor="indexer-timeout">
                                Request timeout (seconds) <span className="text-[11px] font-normal text-base-content/45">(blank = use global default)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-timeout"
                                className={`w-full ${!isTimeoutFieldValid && timeoutSeconds !== "" ? "input-error" : ""}`}
                                placeholder="Use global default"
                                value={timeoutSeconds}
                                onChange={e => setTimeoutSeconds(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5">
                            <Label htmlFor="indexer-search-limit">
                                Search result limit <span className="text-[11px] font-normal text-base-content/45">(blank = use global default)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-search-limit"
                                className={`w-full ${!isSearchResultLimitValid ? "input-error" : ""}`}
                                placeholder="Use global default"
                                value={searchResultLimit}
                                onChange={e => setSearchResultLimit(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5">
                            <Label htmlFor="indexer-hit-limit">
                                API hit limit <span className="text-[11px] font-normal text-base-content/45">(blank or 0 = unlimited)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-hit-limit"
                                className={`w-full ${!isHitLimitValid ? "input-error" : ""}`}
                                placeholder="Unlimited"
                                value={hitLimit}
                                onChange={e => setHitLimit(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5">
                            <Label htmlFor="indexer-download-limit">
                                Download limit <span className="text-[11px] font-normal text-base-content/45">(blank or 0 = unlimited)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-download-limit"
                                className={`w-full ${!isDownloadLimitValid ? "input-error" : ""}`}
                                placeholder="Unlimited"
                                value={downloadLimit}
                                onChange={e => setDownloadLimit(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5 sm:col-span-2">
                            <Label htmlFor="indexer-hit-reset-time">
                                Hit reset time <span className="text-[11px] font-normal text-base-content/45">(UTC hour 0-23; blank = rolling 24h window)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-hit-reset-time"
                                className={`w-full ${!isHitResetValid ? "input-error" : ""}`}
                                placeholder="Rolling 24h"
                                value={hitResetTime}
                                onChange={e => setHitResetTime(e.target.value.replace(/[^0-9]/g, ""))}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5 sm:col-span-2">
                            <div className="flex items-center gap-2">
                                <Checkbox
                                                                        id="indexer-enabled"
                                    className="checkbox"
                                    checked={enabled}
                                    onChange={e => setEnabled(e.target.checked)}
                                />
                                <Label htmlFor="indexer-enabled" className="text-sm font-normal text-base-content/80">
                                    Enabled
                                </Label>
                            </div>
                        </div>

                        <div className="flex flex-col gap-1.5 sm:col-span-2">
                            <div className="flex items-center gap-2">
                                <Checkbox
                                                                        id="indexer-strict"
                                    className="checkbox"
                                    checked={strict}
                                    onChange={e => setStrict(e.target.checked)}
                                />
                                <Label htmlFor="indexer-strict" className="text-sm font-normal text-base-content/80">
                                    Strict matching <span className="text-[11px] font-normal text-base-content/45">(drop results whose title doesn't match the request)</span>
                                </Label>
                            </div>
                            <div className="flex items-center gap-2">
                                <input
                                    type="checkbox"
                                    id="indexer-health-proxy"
                                    className="checkbox checkbox-sm"
                                    checked={useHealthProxy}
                                    onChange={e => setUseHealthProxy(e.target.checked)}
                                />
                                <Label htmlFor="indexer-health-proxy" className="text-sm font-normal text-base-content/80">
                                    Zyclops health proxy <span className="text-[11px] font-normal text-base-content/45">(search via the community NZB-health proxy; returns only releases known retrievable on your usenet providers)</span>
                                </Label>
                            </div>
                        </div>

                        <div className="flex flex-col gap-1.5">
                            <Label htmlFor="indexer-extra-movie-cats">
                                Extra movie categories <span className="text-[11px] font-normal text-base-content/45">(comma-separated; appended to the default 2000/2070)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-extra-movie-cats"
                                className={`w-full ${!isExtraMovieCategoriesValid ? "input-error" : ""}`}
                                placeholder="e.g. 2100,2200"
                                value={extraMovieCategories}
                                onChange={e => setExtraMovieCategories(e.target.value)}
                                disabled={ignoreCategoryFilter}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5">
                            <Label htmlFor="indexer-extra-tv-cats">
                                Extra TV categories <span className="text-[11px] font-normal text-base-content/45">(comma-separated; appended to the default 5000/5070)</span>
                            </Label>
                            <Input
                                type="text"
                                id="indexer-extra-tv-cats"
                                className={`w-full ${!isExtraTvCategoriesValid ? "input-error" : ""}`}
                                placeholder="e.g. 5100,5200"
                                value={extraTvCategories}
                                onChange={e => setExtraTvCategories(e.target.value)}
                                disabled={ignoreCategoryFilter}
                            />
                        </div>

                        <div className="flex flex-col gap-1.5 sm:col-span-2">
                            <div className="flex items-center gap-2">
                                <Checkbox
                                                                        id="indexer-ignore-category-filter"
                                    className="checkbox"
                                    checked={ignoreCategoryFilter}
                                    onChange={e => setIgnoreCategoryFilter(e.target.checked)}
                                />
                                <Label htmlFor="indexer-ignore-category-filter" className="text-sm font-normal text-base-content/80">
                                    Ignore category filter <span className="text-[11px] font-normal text-base-content/45">(send no <code>cat=</code> param at all — escape hatch for indexers with fully custom category schemas)</span>
                                </Label>
                            </div>
                        </div>

                        <div className="flex flex-col gap-1.5 sm:col-span-2">
                            <div className="flex items-center gap-2">
                                <Checkbox
                                                                        id="indexer-filter-enabled"
                                    className="checkbox"
                                    checked={filterEnabled}
                                    onChange={e => setFilterEnabled(e.target.checked)}
                                />
                                <Label htmlFor="indexer-filter-enabled" className="text-sm font-normal text-base-content/80">
                                    Result filtering <span className="text-[11px] font-normal text-base-content/45">(uses indexer-supplied metadata to filter and rank this indexer's results; recommended defaults applied when enabled)</span>
                                </Label>
                            </div>
                        </div>

                        {filterEnabled && (
                            <div className="flex flex-col gap-1.5 sm:col-span-2">
                                <button
                                    type="button"
                                    onClick={() => setFilterAdvancedOpen(o => !o)}
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
                                    <div className="flex items-center gap-2">
                                        <Checkbox
                                                                                        id="indexer-filter-pw"
                                            className="checkbox"
                                            checked={filterSkipPassworded}
                                            onChange={e => setFilterSkipPassworded(e.target.checked)}
                                        />
                                        <Label htmlFor="indexer-filter-pw" className="text-sm font-normal text-base-content/80">
                                            Skip password-protected releases <span className="text-[11px] font-normal text-base-content/45">(items the indexer flags as containing a passworded archive)</span>
                                        </Label>
                                    </div>
                                </div>

                                <div className="flex flex-col gap-1.5">
                                    <Label htmlFor="indexer-filter-mingrabs">
                                        Minimum download count <span className="text-[11px] font-normal text-base-content/45">(0 = no minimum)</span>
                                    </Label>
                                    <Input
                                        type="text"
                                        id="indexer-filter-mingrabs"
                                        className="w-full"
                                        placeholder={OPTIMISED_DEFAULTS.MinGrabs.toString()}
                                        value={filterMinGrabs}
                                        onChange={e => setFilterMinGrabs(e.target.value.replace(/[^0-9]/g, ""))}
                                    />
                                </div>

                                <div className="flex flex-col gap-1.5">
                                    <Label htmlFor="indexer-filter-grace">
                                        Grace period for new releases <span className="text-[11px] font-normal text-base-content/45">(hours; 0 = no grace)</span>
                                    </Label>
                                    <Input
                                        type="text"
                                        id="indexer-filter-grace"
                                        className="w-full"
                                        placeholder={OPTIMISED_DEFAULTS.GrabsGraceHours.toString()}
                                        value={filterGrabsGraceHours}
                                        onChange={e => setFilterGrabsGraceHours(e.target.value.replace(/[^0-9]/g, ""))}
                                    />
                                </div>

                                <div className="flex flex-col gap-1.5 sm:col-span-2">
                                    <Label htmlFor="indexer-filter-maxage">
                                        Drop releases older than this many days with zero downloads <span className="text-[11px] font-normal text-base-content/45">(0 = disabled)</span>
                                    </Label>
                                    <Input
                                        type="text"
                                        id="indexer-filter-maxage"
                                        className="w-full"
                                        placeholder={OPTIMISED_DEFAULTS.MaxAgeDaysWithoutGrabs.toString()}
                                        value={filterMaxAgeDaysWithoutGrabs}
                                        onChange={e => setFilterMaxAgeDaysWithoutGrabs(e.target.value.replace(/[^0-9]/g, ""))}
                                    />
                                </div>

                                <div className="flex flex-col gap-1.5 sm:col-span-2">
                                    <div className="flex items-center gap-2">
                                        <Checkbox
                                                                                        id="indexer-filter-prefer"
                                            className="checkbox"
                                            checked={filterPreferDownloaded}
                                            onChange={e => setFilterPreferDownloaded(e.target.checked)}
                                        />
                                        <Label htmlFor="indexer-filter-prefer" className="text-sm font-normal text-base-content/80">
                                            Rank by download count <span className="text-[11px] font-normal text-base-content/45">(sort results by number of downloads, descending; items without a download count sort below those with one)</span>
                                        </Label>
                                    </div>
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

            {testState === 'error' && (
                <Alert variant="danger" className="mt-4 text-xs">
                    Connection test failed
                </Alert>
            )}

            {testState === 'success' && (
                <Alert variant="success" className="mt-4 text-xs">
                    Connection test successful!
                </Alert>
            )}
        </Modal>
    );
}
export function isIndexersSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["indexers.instances"] !== newConfig["indexers.instances"]
        || (config["api.user-agent"] ?? "") !== (newConfig["api.user-agent"] ?? "")
        || (config["api.search-user-agent"] ?? "") !== (newConfig["api.search-user-agent"] ?? "")
        || (config["search.exclude-patterns"] ?? "") !== (newConfig["search.exclude-patterns"] ?? "")
        || (config["search.exclude-sync-urls"] ?? "") !== (newConfig["search.exclude-sync-urls"] ?? "")
        || (config["search.exclude-sync-refresh-minutes"] ?? "") !== (newConfig["search.exclude-sync-refresh-minutes"] ?? "");
}

export function isIndexersSettingsValid(newConfig: Record<string, string>) {
    try {
        const c = parseConfig(newConfig["indexers.instances"]);
        if (!isProxyUrlValid(c.ProxyUrl ?? "")) return false;
        if (c.TimeoutSeconds !== undefined && (!Number.isInteger(c.TimeoutSeconds) || c.TimeoutSeconds <= 0)) return false;
        if (c.SearchResultLimit !== undefined && (!Number.isInteger(c.SearchResultLimit) || c.SearchResultLimit <= 0)) return false;
        for (const i of c.Indexers) {
            if (!i.Name.trim()) return false;
            if (!i.ApiKey.trim()) return false;
            try { new URL(i.Url); } catch { return false; }
            if (!isProxyUrlValid(i.ProxyUrl ?? "")) return false;
            if (i.TimeoutSeconds !== undefined && (!Number.isInteger(i.TimeoutSeconds) || i.TimeoutSeconds <= 0)) return false;
            if (i.SearchResultLimit !== undefined && (!Number.isInteger(i.SearchResultLimit) || i.SearchResultLimit <= 0)) return false;
            if (i.HitLimit !== undefined && (!Number.isInteger(i.HitLimit) || i.HitLimit < 0)) return false;
            if (i.DownloadLimit !== undefined && (!Number.isInteger(i.DownloadLimit) || i.DownloadLimit < 0)) return false;
            if (i.HitLimitResetTime !== undefined
                && (!Number.isInteger(i.HitLimitResetTime) || i.HitLimitResetTime < 0 || i.HitLimitResetTime > 23)) return false;
            if (i.ExtraMovieCategories !== undefined && !isCategoryListValid(i.ExtraMovieCategories)) return false;
            if (i.ExtraTvCategories !== undefined && !isCategoryListValid(i.ExtraTvCategories)) return false;
        }
        if (validateExcludePatterns(newConfig["search.exclude-patterns"] ?? "").length > 0) return false;
        if (validateSyncUrls(newConfig["search.exclude-sync-urls"] ?? "").length > 0) return false;
        const syncRefresh = newConfig["search.exclude-sync-refresh-minutes"] ?? "";
        if (syncRefresh.trim() !== "" && !isRefreshValid(syncRefresh)) return false;
        return true;
    } catch {
        return false;
    }
}
