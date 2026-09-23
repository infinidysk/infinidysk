import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Link } from "react-router";
import { withUrlBase } from "~/utils/url-base";
import { settingsPath } from "~/navigation/settings-tabs";
import { Icon } from "~/components/ui";

type Traffic = {
  hitBlocks: number;
  hitBytes: number;
  missBlocks: number;
  missBytes: number;
  committedBytes: number;
};
type Folder = {
  id: string;
  name: string;
  path: string;
  storageType: string;
  priority: number;
  enabled: boolean;
  readOnly: boolean;
  maxBytes: number;
  minFreeBytes: number;
  online: boolean | null;
  writable: boolean | null;
  error: string | null;
  allocatedBytes: number;
  liveFiles: number;
  emptyEntries: number;
  verifiedBlocks: number;
  retiredEntries: number;
  retiredBytes: number;
};
type Summary = {
  available: boolean;
  activeMode: string;
  configuredMode: string;
  restartRequired: boolean;
  initializationPending: boolean;
  initializationError: string | null;
  asOfUtc: string;
  observedSinceUtc: string | null;
  totalAllocatedBytes: number;
  totalQuotaBytes: number;
  liveFiles: number;
  emptyEntries: number;
  verifiedBlocks: number;
  retiredEntries: number;
  retiredBytes: number;
  traffic: Traffic;
  folders: Folder[];
};
type CacheFile = {
  key: string;
  folderId: string;
  itemId: string;
  name: string;
  length: number;
  allocatedBytes: number;
  verifiedBytes: number;
  pinned: boolean;
  lastAccessedAt: string;
  accessCount: number | null;
};
type FilePage = { items: CacheFile[]; totalCount: number; nextCursor: string | null };
type Eviction = {
  id: number;
  folderId: string;
  itemId: string;
  displayName: string;
  length: number;
  allocatedBytes: number;
  verifiedBytes: number;
  accessCount: number | null;
  lastAccessUnix: number;
  reason: string;
  evictedUnix: number;
};
type EvictionPage = { items: Eviction[]; totalCount: number; nextCursor: number | null };
type Transfer = {
  itemId: string;
  displayName: string | null;
  length: number;
  committedBytes: number;
  speedBytesPerSecond: number;
  startedAt: string;
  background: boolean;
};
type Range = { offset: number; count: number };
const pageSize = 50;
const byteFormatter = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
function bytes(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return "—";
  if (value === 0) return "0 B";
  const units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
  const rank = Math.min(units.length - 1, Math.floor(Math.log(Math.abs(value)) / Math.log(1024)));
  return `${byteFormatter.format(value / 1024 ** rank)} ${units[rank]}`;
}
function count(value: number | null | undefined): string {
  return value == null ? "—" : new Intl.NumberFormat().format(value);
}
function relative(date: string | number): string {
  const age = Math.max(0, Date.now() - new Date(date).getTime());
  if (!Number.isFinite(age)) return "—";
  if (age < 60_000) return "Just now";
  if (age < 3_600_000) return `${Math.floor(age / 60_000)}m ago`;
  if (age < 86_400_000) return `${Math.floor(age / 3_600_000)}h ago`;
  return new Date(date).toLocaleString();
}
function percentage(numerator: number, denominator: number): number {
  return denominator > 0 ? Math.min(100, Math.max(0, (numerator / denominator) * 100)) : 0;
}
async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(withUrlBase(path), signal ? { signal } : {});
  if (!response.ok) throw new Error(`Could not load cache data (${response.status}).`);
  return (await response.json()) as T;
}
function Panel({
  title,
  subtitle,
  children,
  action,
}: {
  title: string;
  subtitle?: string;
  children: ReactNode;
  action?: ReactNode;
}) {
  return (
    <section className="overflow-hidden rounded-xl border border-base-content/10 bg-base-200/45">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-base-content/10 px-5 py-4">
        <div>
          <h2 className="text-base font-semibold">{title}</h2>
          {subtitle && <p className="mt-0.5 text-xs text-base-content/55">{subtitle}</p>}
        </div>
        {action}
      </div>
      {children}
    </section>
  );
}
function Progress({ value, label }: { value: number; label: string }) {
  const color = value >= 90 ? "bg-error" : value >= 80 ? "bg-warning" : "bg-primary";
  return (
    <div className="flex min-w-32 items-center gap-2" aria-label={label}>
      <div className="h-2 w-24 overflow-hidden rounded-full bg-base-content/15">
        <div className={`h-full rounded-full ${color}`} style={{ width: `${value}%` }} />
      </div>
      <span className="text-xs font-semibold">{Math.round(value)}%</span>
    </div>
  );
}
function Coverage({ file }: { file: CacheFile }) {
  const value = percentage(file.verifiedBytes, file.length);
  return (
    <div
      className="flex min-w-32 items-center gap-2"
      aria-label={`${Math.round(value)} percent verified`}
    >
      <div className="h-2 w-24 overflow-hidden rounded-full bg-base-content/15">
        <div className="h-full rounded-full bg-primary" style={{ width: `${value}%` }} />
      </div>
      <span className="text-xs font-semibold">{Math.round(value)}%</span>
    </div>
  );
}
function RangeMap({
  file,
  ranges,
  complete,
}: {
  file: CacheFile;
  ranges: Range[];
  complete: boolean;
}) {
  const ordered = [...ranges].sort((a, b) => a.offset - b.offset);
  const gaps: { start: number; end: number }[] = [];
  let position = 0;
  for (const range of ordered) {
    if (range.offset > position) gaps.push({ start: position, end: range.offset });
    position = Math.max(position, range.offset + range.count);
  }
  if (complete && position < file.length) gaps.push({ start: position, end: file.length });
  return (
    <>
      <div
        className="relative mt-4 h-4 overflow-hidden rounded bg-base-content/15"
        role="img"
        aria-label={`${ordered.length} verified ranges; ${complete ? `${gaps.length} known gaps` : "more ranges available"}`}
      >
        {ordered.map((range) => (
          <span
            key={range.offset}
            className="absolute inset-y-0 bg-primary"
            style={{
              left: `${percentage(range.offset, file.length)}%`,
              width: `${percentage(range.count, file.length)}%`,
            }}
          />
        ))}
      </div>
      <div className="mt-2 flex gap-4 text-xs text-base-content/55">
        <span>
          <i className="mr-1 inline-block h-2 w-2 bg-primary" />
          Verified
        </span>
        <span>
          <i className="mr-1 inline-block h-2 w-2 bg-base-content/25" />
          Gap or unloaded
        </span>
      </div>
      {complete && gaps.length > 0 && (
        <div className="mt-4">
          <p className="text-xs font-semibold uppercase tracking-wider text-base-content/55">
            Known gaps
          </p>
          <ul className="mt-1 max-h-40 divide-y divide-base-content/10 overflow-y-auto text-xs">
            {gaps.slice(0, 20).map((gap) => (
              <li className="py-1" key={gap.start}>
                {bytes(gap.start)} – {bytes(gap.end)}
              </li>
            ))}
          </ul>
          {gaps.length > 20 && (
            <p className="text-xs text-base-content/50">Showing first 20 of {gaps.length} gaps.</p>
          )}
        </div>
      )}
    </>
  );
}
function TableWrap({ children }: { children: ReactNode }) {
  return <div className="overflow-x-auto">{children}</div>;
}
const table =
  "table table-sm w-full min-w-[850px] text-sm [&_thead]:text-xs [&_thead]:uppercase [&_thead]:tracking-wide [&_thead]:text-base-content/55";

export default function NativeCachePage() {
  const [view, setView] = useState<"overview" | "files" | "evicted">("overview");
  const [summary, setSummary] = useState<Summary | null>(null);
  const [activity, setActivity] = useState<CacheFile[]>([]);
  const [transfers, setTransfers] = useState<Transfer[]>([]);
  const [overviewError, setOverviewError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");
  const [folderId, setFolderId] = useState("");
  const [coverage, setCoverage] = useState("all");
  const [sort, setSort] = useState("recent");
  const [filePage, setFilePage] = useState<FilePage | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [fileCursor, setFileCursor] = useState<string | null>(null);
  const [fileHistory, setFileHistory] = useState<(string | null)[]>([]);
  const [selected, setSelected] = useState<CacheFile | null>(null);
  const [ranges, setRanges] = useState<Range[]>([]);
  const [rangeCursor, setRangeCursor] = useState<number | null>(null);
  const [rangeError, setRangeError] = useState<string | null>(null);
  const [evictSearch, setEvictSearch] = useState("");
  const [debouncedEvictSearch, setDebouncedEvictSearch] = useState("");
  const [evictReason, setEvictReason] = useState("");
  const [evictPage, setEvictPage] = useState<EvictionPage | null>(null);
  const [evictCursor, setEvictCursor] = useState<number | null>(null);
  const [evictHistory, setEvictHistory] = useState<(number | null)[]>([]);
  const [evictError, setEvictError] = useState<string | null>(null);
  const selectedKey = selected?.key;

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search.trim()), 250);
    return () => clearTimeout(timer);
  }, [search]);
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedEvictSearch(evictSearch.trim()), 250);
    return () => clearTimeout(timer);
  }, [evictSearch]);
  const loadOverview = useCallback(async (signal: AbortSignal) => {
    try {
      const result = await getJson<Summary>("/api/native-cache/summary", signal);
      setSummary(result);
      setOverviewError(null);
      if (result.available) {
        const [recent, active] = await Promise.all([
          getJson<{ items: CacheFile[] }>("/api/native-cache/activity?limit=8", signal),
          getJson<{ writes: Transfer[] }>("/api/native-cache/transfers", signal),
        ]);
        setActivity(recent.items);
        setTransfers(active.writes);
      } else {
        setActivity([]);
        setTransfers([]);
      }
    } catch (error) {
      if (!signal.aborted)
        setOverviewError(error instanceof Error ? error.message : "Cache status unavailable.");
    }
  }, []);
  useEffect(() => {
    const abort = new AbortController();
    void loadOverview(abort.signal);
    const timer = setInterval(() => {
      if (!document.hidden) void loadOverview(abort.signal);
    }, 30_000);
    return () => {
      abort.abort();
      clearInterval(timer);
    };
  }, [loadOverview, refresh]);
  useEffect(() => {
    if (view !== "overview") return;
    const abort = new AbortController();
    const timer = setInterval(() => {
      if (document.hidden || !summary?.available) return;
      void getJson<{ writes: Transfer[] }>("/api/native-cache/transfers", abort.signal)
        .then((data) => setTransfers(data.writes))
        .catch(() => {});
    }, 5_000);
    return () => {
      abort.abort();
      clearInterval(timer);
    };
  }, [view, summary?.available]);
  useEffect(() => {
    setFileCursor(null);
    setFileHistory([]);
  }, [debouncedSearch, folderId, coverage, sort]);
  useEffect(() => {
    if (view !== "files" || !summary?.available) return;
    const abort = new AbortController();
    setFilePage(null);
    setFileError(null);
    const query = new URLSearchParams({
      limit: String(pageSize),
      search: debouncedSearch,
      coverage,
      sort,
    });
    if (folderId) query.set("folderId", folderId);
    if (fileCursor) query.set("cursor", fileCursor);
    void getJson<FilePage>(`/api/native-cache/files?${query}`, abort.signal)
      .then((data) => {
        setFilePage(data);
        setFileError(null);
      })
      .catch((error) => {
        if (!abort.signal.aborted)
          setFileError(error instanceof Error ? error.message : "File list unavailable.");
      });
    return () => abort.abort();
  }, [view, summary?.available, debouncedSearch, folderId, coverage, sort, fileCursor, refresh]);
  useEffect(() => {
    setEvictCursor(null);
    setEvictHistory([]);
  }, [debouncedEvictSearch, evictReason]);
  useEffect(() => {
    if (view !== "evicted" || !summary?.available) return;
    const abort = new AbortController();
    setEvictPage(null);
    setEvictError(null);
    const query = new URLSearchParams({ limit: String(pageSize), search: debouncedEvictSearch });
    if (evictReason) query.set("reason", evictReason);
    if (evictCursor != null) query.set("cursor", String(evictCursor));
    void getJson<EvictionPage>(`/api/native-cache/evictions?${query}`, abort.signal)
      .then((data) => {
        setEvictPage(data);
        setEvictError(null);
      })
      .catch((error) => {
        if (!abort.signal.aborted)
          setEvictError(error instanceof Error ? error.message : "Eviction history unavailable.");
      });
    return () => abort.abort();
  }, [view, summary?.available, debouncedEvictSearch, evictReason, evictCursor, refresh]);
  useEffect(() => {
    if (!selectedKey) {
      setRanges([]);
      setRangeCursor(null);
      setRangeError(null);
      return;
    }
    const abort = new AbortController();
    const query = new URLSearchParams({ key: selectedKey, limit: "100" });
    void getJson<{ ranges: Range[]; nextAfter: number | null }>(
      `/api/native-cache/ranges?${query}`,
      abort.signal,
    )
      .then((data) => {
        setRanges(data.ranges);
        setRangeCursor(data.nextAfter);
        setRangeError(null);
      })
      .catch((error) => {
        if (!abort.signal.aborted)
          setRangeError(error instanceof Error ? error.message : "Ranges unavailable.");
      });
    return () => abort.abort();
  }, [selectedKey]);
  const names = useMemo(
    () => new Map((summary?.folders ?? []).map((folder) => [folder.id, folder.name])),
    [summary],
  );
  const hitRate = summary
    ? percentage(summary.traffic.hitBlocks, summary.traffic.hitBlocks + summary.traffic.missBlocks)
    : 0;
  const card = (title: string, value: string, note: string, color = "") => (
    <div className="rounded-xl border border-base-content/10 bg-base-200/45 p-5">
      <p className="text-xs font-medium text-base-content/60">{title}</p>
      <p className={`mt-2 text-2xl font-bold tracking-tight ${color}`}>{value}</p>
      <p className="mt-1 text-xs text-base-content/55">{note}</p>
    </div>
  );
  return (
    <main className="mx-auto max-w-[1700px] space-y-5 p-4 md:p-8">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="mb-2 text-xs text-base-content/50">Workspace / Native cache</p>
          <h1 className="text-3xl font-bold tracking-tight">Native cache</h1>
          <p className="mt-1 text-sm text-base-content/60">
            Verified media on local and network storage, at a glance.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <span className={`badge ${summary?.available ? "badge-success" : "badge-ghost"}`}>
            {summary?.available
              ? "NATIVE MODE ACTIVE"
              : summary?.initializationPending
                ? "INITIALIZING"
                : "INACTIVE"}
          </span>
          <button
            type="button"
            className="btn btn-sm btn-outline"
            onClick={() => setRefresh((value) => value + 1)}
          >
            <Icon name="refresh" /> Refresh
          </button>
        </div>
      </div>
      <div
        role="tablist"
        aria-label="Native cache views"
        className="tabs tabs-border border-b border-base-content/10"
      >
        {(["overview", "files", "evicted"] as const).map((tab) => (
          <button
            key={tab}
            type="button"
            role="tab"
            aria-selected={view === tab}
            className={`tab ${view === tab ? "tab-active" : ""}`}
            onClick={() => setView(tab)}
          >
            {tab === "overview"
              ? "Overview"
              : tab === "files"
                ? `All files${summary?.liveFiles != null ? ` (${count(summary.liveFiles)})` : ""}`
                : "Recently evicted"}
          </button>
        ))}
      </div>
      {overviewError && (
        <div role="alert" className="alert alert-error">
          {overviewError}
        </div>
      )}
      {summary?.restartRequired && (
        <div className="alert alert-warning">
          Saved cache settings require a restart to take effect. This page shows the running
          configuration.
        </div>
      )}
      {summary && !summary.available && (
        <div className="alert alert-info">
          {summary.initializationError ??
            (summary.initializationPending
              ? "Native cache is starting."
              : "Native cache is not active.")}
        </div>
      )}
      {!summary && !overviewError && (
        <div className="loading loading-spinner loading-md" aria-label="Loading native cache" />
      )}
      {summary?.available && view === "overview" && (
        <>
          <p className="text-xs text-base-content/55">
            Traffic covers up to 24 hours
            {summary.observedSinceUtc
              ? ` (collected since ${new Date(summary.observedSinceUtc).toLocaleString()})`
              : " (no observations yet)"}
            . Capacity includes retired allocation awaiting cleanup. Updated{" "}
            {relative(summary.asOfUtc)}.
          </p>
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            {card(
              "Allocated storage",
              `${bytes(summary.totalAllocatedBytes)} / ${bytes(summary.totalQuotaBytes)}`,
              `${Math.round(percentage(summary.totalAllocatedBytes, summary.totalQuotaBytes))}% used across ${summary.folders.length} folders`,
            )}
            {card(
              "Cached files",
              count(summary.liveFiles),
              `${count(summary.verifiedBlocks)} verified blocks · ${count(summary.retiredEntries)} retired`,
            )}
            {card(
              "Cache hit rate · 24h",
              summary.traffic.hitBlocks + summary.traffic.missBlocks
                ? `${hitRate.toFixed(1)}%`
                : "—",
              `${count(summary.traffic.hitBlocks)} hits · ${count(summary.traffic.missBlocks)} misses`,
              "text-success",
            )}
            {card(
              "Bandwidth saved · 24h",
              bytes(summary.traffic.hitBytes),
              "Verified cache bytes served",
              "text-primary",
            )}
          </div>
          <Panel
            title="Cache folders"
            subtitle="Capacity, placement, and storage health."
            action={
              <Link className="btn btn-sm btn-outline" to={settingsPath("streaming")}>
                Manage folders
              </Link>
            }
          >
            <TableWrap>
              <table className={table}>
                <thead>
                  <tr>
                    <th>Folder</th>
                    <th>Type</th>
                    <th>Usage</th>
                    <th>Files</th>
                    <th>Priority</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {summary.folders.map((folder) => (
                    <tr key={folder.id}>
                      <td>
                        <div className="font-semibold">{folder.name}</div>
                        <div
                          className="max-w-72 truncate text-xs text-base-content/50"
                          title={folder.path}
                        >
                          {folder.path}
                        </div>
                      </td>
                      <td>
                        <span className="badge badge-info badge-sm uppercase">
                          {folder.storageType}
                        </span>
                      </td>
                      <td>
                        <Progress
                          value={percentage(folder.allocatedBytes, folder.maxBytes)}
                          label={`${folder.name}: ${bytes(folder.allocatedBytes)} of ${bytes(folder.maxBytes)}`}
                        />
                        <div className="mt-1 text-xs text-base-content/50">
                          {bytes(folder.allocatedBytes)} / {bytes(folder.maxBytes)}
                        </div>
                      </td>
                      <td>
                        {count(folder.liveFiles)}
                        {folder.retiredEntries > 0 && (
                          <div className="text-xs text-warning">
                            +{folder.retiredEntries} retired
                          </div>
                        )}
                      </td>
                      <td>{folder.priority}</td>
                      <td>
                        <span
                          className={`badge badge-sm ${folder.online === true ? (folder.writable ? "badge-success" : "badge-info") : folder.error?.includes("unknown") ? "badge-warning" : "badge-error"}`}
                        >
                          {folder.online === true
                            ? folder.writable
                              ? "Online"
                              : "Read-only"
                            : folder.error?.includes("unknown")
                              ? "Unknown"
                              : "Offline"}
                        </span>
                        {folder.error && (
                          <div className="max-w-48 text-xs text-base-content/50">
                            {folder.error}
                          </div>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </TableWrap>
          </Panel>
          <div className="grid gap-4 xl:grid-cols-[1fr_1.25fr]">
            <Panel
              title="Active cache writes"
              subtitle="Bytes durably committed to cache."
              action={
                <span className="badge badge-primary badge-sm">{transfers.length} active</span>
              }
            >
              <TableWrap>
                <table className={table}>
                  <thead>
                    <tr>
                      <th>File</th>
                      <th>Committed</th>
                      <th>Speed</th>
                      <th>Type</th>
                    </tr>
                  </thead>
                  <tbody>
                    {transfers.length ? (
                      transfers.map((row, i) => (
                        <tr key={`${row.itemId}-${i}`}>
                          <td className="max-w-72 truncate" title={row.displayName ?? row.itemId}>
                            {row.displayName ?? row.itemId}
                          </td>
                          <td>
                            {bytes(row.committedBytes)} / {bytes(row.length)}
                          </td>
                          <td>{bytes(row.speedBytesPerSecond)}/s</td>
                          <td>{row.background ? "Warming" : "Playback"}</td>
                        </tr>
                      ))
                    ) : (
                      <tr>
                        <td colSpan={4} className="py-8 text-center text-base-content/50">
                          No active cache writes
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </TableWrap>
            </Panel>
            <Panel
              title="Recent cache activity"
              subtitle="Recently accessed catalogue entries."
              action={
                <button className="btn btn-sm btn-ghost" onClick={() => setView("files")}>
                  View all files →
                </button>
              }
            >
              <TableWrap>
                <table className={table}>
                  <thead>
                    <tr>
                      <th>File</th>
                      <th>Folder</th>
                      <th>Coverage</th>
                      <th>Last access</th>
                    </tr>
                  </thead>
                  <tbody>
                    {activity.length ? (
                      activity.map((file) => (
                        <tr key={file.key}>
                          <td className="max-w-72 truncate" title={file.name}>
                            {file.name}
                          </td>
                          <td>{names.get(file.folderId) ?? file.folderId}</td>
                          <td>
                            <Coverage file={file} />
                          </td>
                          <td>{relative(file.lastAccessedAt)}</td>
                        </tr>
                      ))
                    ) : (
                      <tr>
                        <td colSpan={4} className="py-8 text-center text-base-content/50">
                          No recent cache activity
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </TableWrap>
            </Panel>
          </div>
        </>
      )}
      {summary?.available && view === "files" && (
        <Panel
          title="All cached files"
          subtitle="Inspect verified coverage and gaps; browsing does not warm the cache."
          action={
            <span className="text-xs text-base-content/50">
              {count(filePage?.totalCount)} matching
            </span>
          }
        >
          <div className="flex flex-wrap gap-2 border-b border-base-content/10 p-4">
            <input
              className="input input-sm min-w-48 flex-1"
              type="search"
              aria-label="Search cached files"
              placeholder="Search file name…"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
            />
            <select
              className="select select-sm"
              aria-label="Filter folder"
              value={folderId}
              onChange={(event) => setFolderId(event.target.value)}
            >
              <option value="">All folders</option>
              {summary.folders.map((folder) => (
                <option value={folder.id} key={folder.id}>
                  {folder.name}
                </option>
              ))}
            </select>
            <select
              className="select select-sm"
              aria-label="Filter coverage"
              value={coverage}
              onChange={(event) => setCoverage(event.target.value)}
            >
              <option value="all">Any cached</option>
              <option value="complete">Complete</option>
              <option value="partial">Partial</option>
              <option value="empty">Empty catalogue rows</option>
            </select>
            <select
              className="select select-sm"
              aria-label="Sort files"
              value={sort}
              onChange={(event) => setSort(event.target.value)}
            >
              <option value="recent">Recently accessed</option>
              <option value="name">Name A–Z</option>
              <option value="coverage">Coverage high–low</option>
            </select>
          </div>
          {fileError && <div className="alert alert-error m-4">{fileError}</div>}
          <TableWrap>
            <table className={table}>
              <thead>
                <tr>
                  <th>File</th>
                  <th>Folder</th>
                  <th>Verified coverage</th>
                  <th>File size</th>
                  <th>Allocated</th>
                  <th>Last access</th>
                  <th>Retention</th>
                </tr>
              </thead>
              <tbody>
                {filePage?.items.map((file) => (
                  <tr
                    key={file.key}
                    className="cursor-pointer hover:bg-base-content/5"
                    tabIndex={0}
                    onClick={() => {
                      setRangeCursor(null);
                      setSelected(file);
                    }}
                    onKeyDown={(event) => {
                      if (event.key === "Enter") {
                        setRangeCursor(null);
                        setSelected(file);
                      }
                    }}
                  >
                    <td className="max-w-[30rem] truncate font-medium" title={file.name}>
                      {file.name}
                    </td>
                    <td>{names.get(file.folderId) ?? file.folderId}</td>
                    <td>
                      <Coverage file={file} />
                    </td>
                    <td>{bytes(file.length)}</td>
                    <td>{bytes(file.allocatedBytes)}</td>
                    <td>{relative(file.lastAccessedAt)}</td>
                    <td>
                      <span className="badge badge-sm badge-ghost">
                        {file.pinned ? "Pinned" : "Standard"}
                      </span>
                    </td>
                  </tr>
                ))}
                {filePage && filePage.items.length === 0 && (
                  <tr>
                    <td colSpan={7} className="py-10 text-center text-base-content/50">
                      No files match these filters.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </TableWrap>
          <div className="flex items-center justify-between border-t border-base-content/10 p-4 text-xs text-base-content/60">
            <span>
              {filePage ? `${count(filePage.totalCount)} matching entries` : "Loading files…"}
            </span>
            <div className="flex gap-2">
              <button
                className="btn btn-xs btn-outline"
                disabled={!fileHistory.length}
                onClick={() => {
                  setFileCursor(fileHistory.at(-1) ?? null);
                  setFileHistory((old) => old.slice(0, -1));
                }}
              >
                ← Previous
              </button>
              <button
                className="btn btn-xs btn-outline"
                disabled={!filePage?.nextCursor}
                onClick={() => {
                  setFileHistory((old) => [...old, fileCursor]);
                  setFileCursor(filePage!.nextCursor);
                }}
              >
                Next →
              </button>
            </div>
          </div>
        </Panel>
      )}
      {summary?.available && view === "evicted" && (
        <Panel
          title="Eviction history"
          subtitle="Confirmed removals. History starts when this feature is enabled."
          action={
            <span className="text-xs text-base-content/50">
              {count(evictPage?.totalCount)} matching
            </span>
          }
        >
          <div className="flex flex-wrap gap-2 border-b border-base-content/10 p-4">
            <input
              className="input input-sm min-w-48 flex-1"
              type="search"
              aria-label="Search evictions"
              placeholder="Search file name…"
              value={evictSearch}
              onChange={(event) => setEvictSearch(event.target.value)}
            />
            <select
              className="select select-sm"
              aria-label="Filter eviction reason"
              value={evictReason}
              onChange={(event) => setEvictReason(event.target.value)}
            >
              <option value="">All reasons</option>
              <option value="pressure">Storage pressure</option>
              <option value="age">Idle age</option>
              <option value="clear">Folder clear</option>
              <option value="relocation">Folder rollover</option>
            </select>
          </div>
          {evictError && <div className="alert alert-error m-4">{evictError}</div>}
          <TableWrap>
            <table className={table}>
              <thead>
                <tr>
                  <th>File</th>
                  <th>Folder</th>
                  <th>Reason</th>
                  <th>Verified before eviction</th>
                  <th>Last accessed</th>
                  <th>Evicted at</th>
                </tr>
              </thead>
              <tbody>
                {evictPage?.items.map((row) => (
                  <tr key={row.id}>
                    <td className="max-w-[30rem] truncate font-medium" title={row.displayName}>
                      {row.displayName || row.itemId}
                    </td>
                    <td>{names.get(row.folderId) ?? row.folderId}</td>
                    <td>
                      <span
                        className={`badge badge-sm ${row.reason === "pressure" ? "badge-warning" : row.reason === "age" || row.reason === "relocation" ? "badge-info" : "badge-ghost"}`}
                      >
                        {row.reason === "pressure"
                          ? "Storage pressure"
                          : row.reason === "age"
                            ? "Idle age"
                            : row.reason === "relocation"
                              ? "Folder rollover"
                              : "Folder clear"}
                      </span>
                    </td>
                    <td>{bytes(row.verifiedBytes)}</td>
                    <td>{relative(row.lastAccessUnix * 1000)}</td>
                    <td>{relative(row.evictedUnix * 1000)}</td>
                  </tr>
                ))}
                {evictPage && !evictPage.items.length && (
                  <tr>
                    <td colSpan={6} className="py-10 text-center text-base-content/50">
                      No eviction records found.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </TableWrap>
          <div className="flex items-center justify-between border-t border-base-content/10 p-4 text-xs text-base-content/60">
            <span>
              {evictPage ? `${count(evictPage.totalCount)} matching events` : "Loading history…"}
            </span>
            <div className="flex gap-2">
              <button
                className="btn btn-xs btn-outline"
                disabled={!evictHistory.length}
                onClick={() => {
                  setEvictCursor(evictHistory.at(-1) ?? null);
                  setEvictHistory((old) => old.slice(0, -1));
                }}
              >
                ← Previous
              </button>
              <button
                className="btn btn-xs btn-outline"
                disabled={evictPage?.nextCursor == null}
                onClick={() => {
                  setEvictHistory((old) => [...old, evictCursor]);
                  setEvictCursor(evictPage!.nextCursor);
                }}
              >
                Next →
              </button>
            </div>
          </div>
        </Panel>
      )}
      {selected && (
        <div
          className="fixed inset-0 z-50 bg-black/60"
          role="presentation"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget) setSelected(null);
          }}
        >
          <aside
            role="dialog"
            aria-modal="true"
            aria-label="Cached file details"
            className="absolute inset-y-0 right-0 w-full max-w-xl overflow-y-auto border-l border-base-content/20 bg-base-200 p-6 shadow-2xl"
          >
            <button
              className="btn btn-sm btn-ghost absolute right-5 top-5"
              aria-label="Close details"
              onClick={() => setSelected(null)}
            >
              ✕
            </button>
            <span className="badge badge-primary badge-sm">FILE DETAILS</span>
            <h2 className="mt-4 break-all text-xl font-bold">{selected.name}</h2>
            <p className="mt-1 text-sm text-base-content/55">
              {names.get(selected.folderId) ?? selected.folderId} · {bytes(selected.length)} file
            </p>
            <div className="mt-6 rounded-xl border border-base-content/10 bg-base-300/60 p-4">
              <p className="text-xs font-semibold uppercase tracking-wider text-base-content/55">
                Verified coverage
              </p>
              <p className="mt-2 text-xl font-bold">
                {percentage(selected.verifiedBytes, selected.length).toFixed(1)}% ·{" "}
                {bytes(selected.verifiedBytes)}
              </p>
              <div className="mt-3">
                <Coverage file={selected} />
              </div>
              <p className="mt-2 text-xs text-base-content/55">
                {bytes(selected.allocatedBytes)} physically allocated. Verified bytes may be
                scattered across the file.
              </p>
            </div>
            <div className="mt-4 rounded-xl border border-base-content/10 bg-base-300/60 p-4">
              <p className="text-xs font-semibold uppercase tracking-wider text-base-content/55">
                Verified ranges
              </p>
              {rangeError && (
                <p role="alert" className="mt-2 text-sm text-error">
                  {rangeError}
                </p>
              )}
              <ul className="mt-2 max-h-64 divide-y divide-base-content/10 overflow-y-auto text-sm">
                {ranges.map((range) => (
                  <li key={range.offset} className="py-2">
                    {bytes(range.offset)} – {bytes(range.offset + range.count)}{" "}
                    <span className="text-success">verified</span>
                  </li>
                ))}
                {!ranges.length && !rangeError && (
                  <li className="py-2 text-base-content/55">No verified ranges in this entry.</li>
                )}
              </ul>
              {ranges.length > 0 && (
                <RangeMap file={selected} ranges={ranges} complete={rangeCursor == null} />
              )}
              {rangeCursor != null && (
                <button
                  className="btn btn-xs btn-outline mt-3"
                  onClick={() => {
                    void (async () => {
                      if (!selected) return;
                      const query = new URLSearchParams({
                        key: selected.key,
                        afterOffset: String(rangeCursor),
                        limit: "100",
                      });
                      try {
                        const data = await getJson<{ ranges: Range[]; nextAfter: number | null }>(
                          `/api/native-cache/ranges?${query}`,
                        );
                        setRanges((old) => [...old, ...data.ranges]);
                        setRangeCursor(data.nextAfter);
                      } catch {
                        setRangeError("Could not load more ranges.");
                      }
                    })();
                  }}
                >
                  Load more ranges
                </button>
              )}
            </div>
            <p className="mt-4 text-xs leading-relaxed text-base-content/55">
              Coverage can contain gaps. Reads crossing a gap use Usenet while the source is
              available; opening this panel does not fetch missing bytes.
            </p>
          </aside>
        </div>
      )}
    </main>
  );
}
