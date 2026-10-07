import type { Route } from "./+types/route";
import {
  type ChangeEvent,
  type KeyboardEvent,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  backendClient,
  type GetLogsResponse,
  type LogEntry,
  type LogLevel,
} from "~/clients/backend-client.server";
import { useLogsWebsocket, type ConnectionStatus } from "./controllers/websocket-controller";
import { Alert, Badge, Icon, PageHeader, Tooltip } from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";

const ALL_LEVELS: LogLevel[] = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
const DEFAULT_LEVELS: LogLevel[] = ["Information", "Warning", "Error", "Fatal"];
const INITIAL_LIMIT = 1000;
const CLIENT_MAX_ENTRIES = 5000;

export async function loader() {
  const data = await backendClient.getLogs({ limit: INITIAL_LIMIT });
  return data;
}

// Opt out of react-router revalidation. The page already fetches its own
// updates via WebSocket + on-filter-change; loader revalidation would just
// double-fetch and fight with the live stream.
export function shouldRevalidate() {
  return false;
}

export default function Logs({ loaderData }: Route.ComponentProps) {
  const initialQuery =
    typeof window !== "undefined"
      ? new URLSearchParams(window.location.search)
      : new URLSearchParams();

  const [entries, setEntries] = useState<LogEntry[]>(loaderData.entries);
  const [counts, setCounts] = useState<Record<string, number>>(loaderData.countsByLevel);
  const [capacity] = useState<number>(loaderData.capacity);
  const [enabledLevels, setEnabledLevels] = useState<Set<LogLevel>>(
    () => new Set(parseLevels(initialQuery.get("levels"))),
  );
  const [searchInput, setSearchInput] = useState<string>(initialQuery.get("q") ?? "");
  const [sourceInput, setSourceInput] = useState<string>(initialQuery.get("src") ?? "");
  const [search, setSearch] = useState<string>(initialQuery.get("q") ?? "");
  const [source, setSource] = useState<string>(initialQuery.get("src") ?? "");
  const [paused, setPaused] = useState<boolean>(false);
  const [pendingCount, setPendingCount] = useState<number>(0);
  const [followTail, setFollowTail] = useState<boolean>(true);
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const [connection, setConnection] = useState<ConnectionStatus>("connecting");
  const [errorText, setErrorText] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);

  const listRef = useRef<HTMLDivElement | null>(null);
  const searchRef = useRef<HTMLInputElement | null>(null);
  const pausedQueueRef = useRef<LogEntry[]>([]);
  const pausedRef = useRef<boolean>(paused);
  pausedRef.current = paused;
  const followTailRef = useRef<boolean>(followTail);
  followTailRef.current = followTail;
  const enabledLevelsRef = useRef<Set<LogLevel>>(enabledLevels);
  enabledLevelsRef.current = enabledLevels;
  const searchRefValue = useRef<string>(search);
  searchRefValue.current = search;
  const sourceRefValue = useRef<string>(source);
  sourceRefValue.current = source;

  // debounce search & source inputs
  useEffect(() => {
    const t = setTimeout(() => setSearch(searchInput.trim()), 220);
    return () => clearTimeout(t);
  }, [searchInput]);
  useEffect(() => {
    const t = setTimeout(() => setSource(sourceInput.trim()), 220);
    return () => clearTimeout(t);
  }, [sourceInput]);

  // sync URL state — using history.replaceState directly so we don't trigger
  // react-router navigation/revalidation (which was causing render loops).
  const didMountRef = useRef(false);
  useEffect(() => {
    if (typeof window === "undefined") return;
    const next = new URLSearchParams();
    if (enabledLevels.size > 0 && !sameLevels(enabledLevels, DEFAULT_LEVELS)) {
      next.set("levels", [...enabledLevels].join(","));
    }
    if (search) next.set("q", search);
    if (source) next.set("src", source);
    const qs = next.toString();
    const target = `${window.location.pathname}${qs ? `?${qs}` : ""}`;
    if (target !== `${window.location.pathname}${window.location.search}`) {
      window.history.replaceState(null, "", target);
    }
  }, [enabledLevels, search, source]);

  // refetch when filters change — but skip the very first render, since the
  // loader already provided the initial unfiltered set.
  useEffect(() => {
    if (!didMountRef.current) {
      didMountRef.current = true;
      return;
    }
    let cancelled = false;
    const params = new URLSearchParams();
    params.set("limit", String(INITIAL_LIMIT));
    if (enabledLevels.size > 0 && enabledLevels.size < ALL_LEVELS.length) {
      params.set("levels", [...enabledLevels].join(","));
    }
    if (search) params.set("search", search);
    if (source) params.set("source", source);
    fetch(withUrlBase(`/api/get-logs?${params.toString()}`))
      .then((r) => {
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        // /api/get-logs returns GetLogsResponse
        return r.json() as Promise<GetLogsResponse>;
      })
      .then((data) => {
        if (cancelled) return;
        setEntries(data.entries ?? []);
        setCounts(data.countsByLevel ?? {});
        setErrorText(null);
        if (followTailRef.current) requestAnimationFrame(scrollToBottom);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setErrorText(e instanceof Error ? e.message : String(e));
      });
    return () => {
      cancelled = true;
    };
  }, [enabledLevels, search, source, reloadToken]);

  // WebSocket: live append (or queue while paused)
  const onBatch = useCallback((batch: LogEntry[]) => {
    if (pausedRef.current) {
      pausedQueueRef.current.push(...batch);
      setPendingCount((c) => c + batch.length);
      return;
    }
    applyBatch(batch);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function applyBatch(batch: LogEntry[]) {
    if (batch.length === 0) return;
    const matches = batch.filter(matchesCurrentFilters);
    setCounts((prev) => {
      const next = { ...prev };
      for (const e of batch) next[e.level] = (next[e.level] ?? 0) + 1;
      return next;
    });
    if (matches.length > 0) {
      setEntries((prev) => mergeAndCap(prev, matches));
      if (followTailRef.current) requestAnimationFrame(scrollToBottom);
    }
  }

  function matchesCurrentFilters(e: LogEntry): boolean {
    const levels = enabledLevelsRef.current;
    if (levels.size > 0 && !levels.has(e.level)) return false;
    const src = sourceRefValue.current;
    if (src && !(e.source ?? "").toLowerCase().includes(src.toLowerCase())) return false;
    const q = searchRefValue.current.toLowerCase();
    if (q) {
      if (
        !e.msg.toLowerCase().includes(q) &&
        !(e.source ?? "").toLowerCase().includes(q) &&
        !(e.exception ?? "").toLowerCase().includes(q)
      ) {
        return false;
      }
    }
    return true;
  }

  useLogsWebsocket(onBatch, setConnection);

  // smart auto-scroll: detect when the user scrolls up to disengage follow.
  // Programmatic scrolls (scrollToBottom) set a suppression flag so the
  // resulting scroll event doesn't bounce followTail.
  const suppressScrollRef = useRef(false);
  const handleScroll = useCallback(() => {
    if (suppressScrollRef.current) {
      suppressScrollRef.current = false;
      return;
    }
    const el = listRef.current;
    if (!el) return;
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    const near = distanceFromBottom < 48;
    setFollowTail((prev) => (prev !== near ? near : prev));
  }, []);

  function scrollToBottom() {
    const el = listRef.current;
    if (!el) return;
    suppressScrollRef.current = true;
    el.scrollTop = el.scrollHeight;
  }

  // when the entries list mounts/first-loads, scroll to bottom
  useEffect(() => {
    requestAnimationFrame(scrollToBottom);
  }, []);

  // keyboard shortcuts
  useEffect(() => {
    const onKey = (ev: globalThis.KeyboardEvent) => {
      const target = ev.target as HTMLElement | null;
      const inField = target?.tagName === "INPUT" || target?.tagName === "TEXTAREA";
      if (ev.key === "/" && !inField) {
        ev.preventDefault();
        searchRef.current?.focus();
        searchRef.current?.select();
        return;
      }
      if (ev.key === "Escape" && inField && target === searchRef.current) {
        setSearchInput("");
        searchRef.current?.blur();
        return;
      }
      if ((ev.key === "f" || ev.key === "F") && !inField && !ev.metaKey && !ev.ctrlKey) {
        ev.preventDefault();
        setFollowTail((v) => {
          if (!v) requestAnimationFrame(scrollToBottom);
          return !v;
        });
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const toggleLevel = useCallback((level: LogLevel) => {
    setEnabledLevels((prev) => {
      const next = new Set(prev);
      if (next.has(level)) next.delete(level);
      else next.add(level);
      return next;
    });
  }, []);

  const toggleExpanded = useCallback((seq: number) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(seq)) next.delete(seq);
      else next.add(seq);
      return next;
    });
  }, []);

  const togglePause = useCallback(() => {
    if (pausedRef.current) {
      const queued = pausedQueueRef.current;
      pausedQueueRef.current = [];
      setPendingCount(0);
      applyBatch(queued);
      setPaused(false);
    } else {
      setPaused(true);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const clearView = useCallback(() => {
    setEntries([]);
    setExpanded(new Set());
  }, []);

  const downloadHref = useMemo(() => {
    const params = new URLSearchParams();
    if (enabledLevels.size > 0 && enabledLevels.size < ALL_LEVELS.length) {
      params.set("levels", [...enabledLevels].join(","));
    }
    if (search) params.set("search", search);
    if (source) params.set("source", source);
    return withUrlBase(`/api/download-logs${params.toString() ? `?${params.toString()}` : ""}`);
  }, [enabledLevels, search, source]);

  const totalInBuffer = useMemo(() => Object.values(counts).reduce((a, b) => a + b, 0), [counts]);
  const filtersActive =
    !sameLevels(enabledLevels, DEFAULT_LEVELS) || search !== "" || source !== "";
  const clearFilters = useCallback(() => {
    setEnabledLevels(new Set(DEFAULT_LEVELS));
    setSearchInput("");
    setSourceInput("");
  }, []);
  const toggleFollow = useCallback(() => {
    setFollowTail((v) => {
      if (!v) requestAnimationFrame(scrollToBottom);
      return !v;
    });
  }, []);

  return (
    <section className="flex h-full min-h-0 min-w-0 flex-col gap-4 overflow-hidden px-4 py-4 text-sm md:px-8">
      <PageHeader
        title="Logs"
        subtitle={
          <>
            Live application logs from the in-memory ring buffer. The last{" "}
            {capacity.toLocaleString()} entries are kept in RAM and cleared on restart. Press{" "}
            <kbd className="kbd kbd-xs">/</kbd> to search, <kbd className="kbd kbd-xs">f</kbd> to
            follow, <kbd className="kbd kbd-xs">Esc</kbd> to leave search.
          </>
        }
        actions={
          <>
            <span
              role="status"
              className={`badge badge-soft gap-1.5 ${connectionBadgeClass(connection)}`}
            >
              <span className={`status status-sm ${connectionStatusClass(connection)}`} />
              {connectionLabel(connection)}
            </span>
            <Tooltip content="Keep scrolled to the newest entry (f)" placement="bottom">
              <button
                type="button"
                className={`btn btn-sm ${followTail ? "border-primary/60 bg-primary/15 text-primary" : ""}`}
                aria-pressed={followTail}
                onClick={toggleFollow}
              >
                <Icon name="vertical_align_bottom" filled={followTail} className="!text-[16px]" />
                {followTail ? "Following" : "Follow"}
              </button>
            </Tooltip>
            <button
              type="button"
              className={`btn btn-sm ${paused ? "btn-warning" : ""}`}
              aria-pressed={paused}
              onClick={togglePause}
            >
              <Icon name={paused ? "play_arrow" : "pause"} filled className="!text-[16px]" />
              {paused ? "Resume" : "Pause"}
              {paused && pendingCount > 0 && (
                <span className="badge badge-xs tabular-nums">{pendingCount.toLocaleString()}</span>
              )}
            </button>
            <Tooltip content="Clears this view only; the server buffer is kept" placement="bottom">
              <button
                type="button"
                className="btn btn-sm"
                onClick={clearView}
                disabled={entries.length === 0}
              >
                <Icon name="clear_all" className="!text-[16px]" />
                Clear view
              </button>
            </Tooltip>
            <Tooltip content="Download the filtered entries as a .log file" placement="bottom">
              <a href={downloadHref} className="btn btn-sm" download>
                <Icon name="download" className="!text-[16px]" />
                Download
              </a>
            </Tooltip>
          </>
        }
      />

      <div className="flex shrink-0 flex-col gap-3 rounded-box border border-base-content/10 bg-base-200 p-3">
        <div className="flex flex-wrap items-center gap-2">
          <div className="join flex-wrap" role="group" aria-label="Log levels">
            {ALL_LEVELS.map((level) => (
              <LevelChip
                key={level}
                level={level}
                active={enabledLevels.has(level)}
                count={counts[level] ?? 0}
                onClick={() => toggleLevel(level)}
              />
            ))}
          </div>
          <span className="ml-auto text-xs whitespace-nowrap tabular-nums text-base-content/60 max-sm:ml-0">
            {entries.length.toLocaleString()} shown · {totalInBuffer.toLocaleString()} of{" "}
            {capacity.toLocaleString()} in buffer
          </span>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <label className="input input-sm min-w-0 flex-1 sm:min-w-60">
            <Icon name="search" className="!text-[16px] opacity-60" />
            <input
              ref={searchRef}
              type="search"
              aria-label="Search messages, sources, and stack traces"
              value={searchInput}
              onChange={(e: ChangeEvent<HTMLInputElement>) => setSearchInput(e.target.value)}
              onKeyDown={(e: KeyboardEvent<HTMLInputElement>) => {
                if (e.key === "Escape") {
                  setSearchInput("");
                  e.currentTarget.blur();
                }
              }}
              placeholder="Search messages, sources, stack traces"
              spellCheck={false}
              autoComplete="off"
            />
            <kbd className="kbd kbd-xs">/</kbd>
          </label>
          <label className="input input-sm w-full sm:w-72">
            <span className="label">Source</span>
            <input
              type="text"
              aria-label="Source filter"
              value={sourceInput}
              onChange={(e: ChangeEvent<HTMLInputElement>) => setSourceInput(e.target.value)}
              placeholder="e.g. NzbWebDAV.Queue"
              spellCheck={false}
              autoComplete="off"
            />
          </label>
          {filtersActive && (
            <button type="button" className="btn btn-sm btn-ghost" onClick={clearFilters}>
              <Icon name="filter_alt_off" className="!text-[16px]" />
              Clear filters
            </button>
          )}
        </div>

        {errorText && (
          <Alert variant="danger" className="alert-soft py-2 text-xs">
            <Icon name="error" className="!text-[18px]" />
            <span>Couldn&apos;t load logs: {errorText}</span>
            <button
              type="button"
              className="btn btn-xs"
              onClick={() => setReloadToken((t) => t + 1)}
            >
              Retry
            </button>
          </Alert>
        )}
      </div>

      <div className="card relative flex min-h-0 flex-1 flex-col overflow-hidden border border-base-content/10 bg-base-100 shadow-sm">
        {entries.length === 0 ? (
          <div className="card-body items-center justify-center gap-2 py-16 text-center">
            <Icon name="receipt_long" className="!text-[40px] text-base-content/30" />
            <h2 className="text-base font-semibold text-base-content">
              {totalInBuffer === 0
                ? "Nothing logged yet"
                : filtersActive
                  ? "No entries match your filters"
                  : "Waiting for new entries"}
            </h2>
            <p className="text-xs text-base-content/60">
              {totalInBuffer === 0
                ? "New entries stream in here as InfiniDysk logs them."
                : filtersActive
                  ? "Widen the level, search, or source filters to see more."
                  : "The view is clear. New entries will appear here live."}
            </p>
            {filtersActive && totalInBuffer > 0 && (
              <button type="button" className="btn btn-sm mt-2" onClick={clearFilters}>
                Clear filters
              </button>
            )}
          </div>
        ) : (
          <div
            ref={listRef}
            className="yes-scrollbar min-h-0 flex-1 overflow-x-hidden overflow-y-auto py-1 font-mono text-xs leading-normal"
            onScroll={handleScroll}
          >
            {entries.map((entry) => (
              <LogRow
                key={entry.seq}
                entry={entry}
                expanded={expanded.has(entry.seq)}
                onToggle={() => toggleExpanded(entry.seq)}
              />
            ))}
          </div>
        )}
        {!followTail && entries.length > 0 && (
          <button
            type="button"
            className="btn btn-sm absolute right-4 bottom-4 z-2 gap-2 shadow-lg"
            onClick={() => {
              setFollowTail(true);
              requestAnimationFrame(scrollToBottom);
            }}
          >
            <Icon name="keyboard_arrow_down" className="!text-[16px]" />
            Jump to live
          </button>
        )}
      </div>
    </section>
  );
}

function LogRow({
  entry,
  expanded,
  onToggle,
}: {
  entry: LogEntry;
  expanded: boolean;
  onToggle: () => void;
}) {
  const expandable = Boolean(entry.exception);
  const rowClass = `grid w-full ${expandable ? "cursor-pointer" : ""} grid-cols-[max-content_64px_1fr] items-baseline gap-3 border-l-2 border-transparent px-3.5 py-0.5 text-left transition-colors duration-75 [contain:content] ${expandable ? "hover:bg-base-content/5" : ""} max-[899px]:grid-cols-[max-content_56px_1fr] max-[899px]:gap-2 max-[899px]:px-2.5 max-[899px]:py-1 ${levelRowClass(entry.level)}`;
  return (
    <div
      className={rowClass}
      {...(expandable
        ? {
            role: "button" as const,
            tabIndex: 0,
            onClick: onToggle,
            onKeyDown: (event: KeyboardEvent<HTMLDivElement>) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                onToggle();
              }
            },
            "aria-expanded": expanded,
          }
        : {})}
    >
      <span className="whitespace-nowrap tabular-nums text-base-content/40">
        {formatTime(entry.ts)}
      </span>
      <Badge className={`badge-xs uppercase tracking-wide ${levelBadgeClass(entry.level)}`}>
        {shortLevel(entry.level)}
      </Badge>
      <span className="flex min-w-0 flex-col gap-0.5 break-words">
        <span className="log-msg whitespace-pre-wrap break-words text-base-content">
          {entry.msg}
        </span>
        {entry.source && (
          <span className="text-[11px] text-base-content/40 max-[899px]:text-[10.5px]">
            {entry.source}
          </span>
        )}
        {entry.exception && (
          <span className="mt-0.5 inline-flex items-center gap-0.5 font-sans text-[11px] text-base-content/60">
            <Icon name={expanded ? "expand_more" : "chevron_right"} className="!text-[14px]" />
            {expanded ? "Hide stack trace" : "Show stack trace"}
          </span>
        )}
        {entry.exception && expanded && (
          <pre className="mt-1 mb-0.5 max-h-80 overflow-auto whitespace-pre-wrap rounded-md border border-base-content/10 bg-base-300 p-2 text-[11px] text-base-content/70">
            {entry.exception}
          </pre>
        )}
      </span>
    </div>
  );
}

function LevelChip({
  level,
  active,
  count,
  onClick,
}: {
  level: LogLevel;
  active: boolean;
  count: number;
  onClick: () => void;
}) {
  const activeClass = !active ? "btn-ghost text-base-content/50" : levelActiveBtnClass(level);
  return (
    <label
      className={`btn btn-sm join-item max-sm:min-h-11 uppercase tracking-wide focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary ${activeClass}`}
    >
      <input
        type="checkbox"
        className="sr-only"
        aria-label={`${shortLevel(level)} ${count}`}
        checked={active}
        onChange={onClick}
      />
      <span>{shortLevel(level)}</span>
      <span className={`badge badge-xs ${active ? levelBadgeClass(level) : "badge-ghost"}`}>
        {count}
      </span>
    </label>
  );
}

function levelActiveBtnClass(level: LogLevel): string {
  switch (level) {
    case "Information":
      return "btn-info";
    case "Warning":
      return "btn-warning";
    case "Error":
      return "btn-error";
    case "Fatal":
      return "btn-error";
    case "Debug":
    case "Verbose":
      // btn-neutral is nearly invisible on night.
      return "border-transparent bg-base-content/80 text-base-100";
  }
}

function levelBadgeClass(level: LogLevel): string {
  switch (level) {
    case "Information":
      return "badge-info";
    case "Warning":
      return "badge-warning";
    case "Error":
      return "badge-error";
    case "Fatal":
      return "badge-error";
    case "Debug":
      return "badge-ghost";
    case "Verbose":
      return "badge-ghost";
  }
}

function levelRowClass(level: LogLevel): string {
  switch (level) {
    case "Verbose":
    case "Debug":
      return "text-base-content/50 border-l-base-content/20 [&_.log-msg]:text-base-content/70";
    case "Information":
      return "text-base-content/70 border-l-info/40";
    case "Warning":
      return "text-warning border-l-warning bg-warning/5";
    case "Error":
      return "text-error border-l-error bg-error/5 [&_.log-msg]:text-error/80";
    case "Fatal":
      return "font-semibold text-error border-l-error bg-error/10 [&_.log-msg]:text-error/80";
  }
}

function connectionStatusClass(s: ConnectionStatus): string {
  switch (s) {
    case "live":
      return "status-success";
    case "reconnecting":
    case "connecting":
      return "status-warning animate-pulse";
    case "disconnected":
      return "status-error";
  }
}

function connectionBadgeClass(s: ConnectionStatus): string {
  switch (s) {
    case "live":
      return "badge-success";
    case "reconnecting":
    case "connecting":
      return "badge-warning";
    case "disconnected":
      return "badge-error";
  }
}

function connectionLabel(s: ConnectionStatus): string {
  switch (s) {
    case "live":
      return "Live";
    case "connecting":
      return "Connecting";
    case "reconnecting":
      return "Reconnecting";
    case "disconnected":
      return "Disconnected";
  }
}

function shortLevel(level: LogLevel): string {
  switch (level) {
    case "Verbose":
      return "trace";
    case "Debug":
      return "debug";
    case "Information":
      return "info";
    case "Warning":
      return "warn";
    case "Error":
      return "error";
    case "Fatal":
      return "fatal";
  }
}

function formatTime(unixMs: number): string {
  const d = new Date(unixMs);
  const hh = String(d.getHours()).padStart(2, "0");
  const mm = String(d.getMinutes()).padStart(2, "0");
  const ss = String(d.getSeconds()).padStart(2, "0");
  const ms = String(d.getMilliseconds()).padStart(3, "0");
  return `${hh}:${mm}:${ss}.${ms}`;
}

function parseLevels(raw: string | null): LogLevel[] {
  if (!raw) return [...DEFAULT_LEVELS];
  const parts = raw
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
  const known = parts.filter((s): s is LogLevel => ALL_LEVELS.includes(s as LogLevel));
  return known.length > 0 ? known : [...DEFAULT_LEVELS];
}

function sameLevels(set: Set<LogLevel>, list: LogLevel[]): boolean {
  if (set.size !== list.length) return false;
  for (const l of list) if (!set.has(l)) return false;
  return true;
}

function mergeAndCap(prev: LogEntry[], incoming: LogEntry[]): LogEntry[] {
  if (incoming.length === 0) return prev;
  // Newest live entries always have higher sequence numbers, so append + dedupe.
  const lastSeq = prev.at(-1)?.seq ?? 0;
  const fresh = incoming.filter((e) => e.seq > lastSeq);
  if (fresh.length === 0) return prev;
  const next = prev.concat(fresh);
  if (next.length <= CLIENT_MAX_ENTRIES) return next;
  return next.slice(next.length - CLIENT_MAX_ENTRIES);
}
