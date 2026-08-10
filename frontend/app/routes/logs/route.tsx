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
import { backendClient, type GetLogsResponse, type LogEntry, type LogLevel } from "~/clients/backend-client.server";
import { useLogsWebsocket, type ConnectionStatus } from "./controllers/websocket-controller";
import { Alert, Badge, Icon } from "~/components/ui";
import { Input } from "~/components/ui/form";
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
    const initialQuery = typeof window !== "undefined"
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
            .then(r => {
                if (!r.ok) throw new Error(`HTTP ${r.status}`);
                // /api/get-logs returns GetLogsResponse
                return r.json() as Promise<GetLogsResponse>;
            })
            .then(data => {
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
    }, [enabledLevels, search, source]);

    // WebSocket: live append (or queue while paused)
    const onBatch = useCallback((batch: LogEntry[]) => {
        if (pausedRef.current) {
            pausedQueueRef.current.push(...batch);
            setPendingCount(c => c + batch.length);
            return;
        }
        applyBatch(batch);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    function applyBatch(batch: LogEntry[]) {
        if (batch.length === 0) return;
        const matches = batch.filter(matchesCurrentFilters);
        setCounts(prev => {
            const next = { ...prev };
            for (const e of batch) next[e.level] = (next[e.level] ?? 0) + 1;
            return next;
        });
        if (matches.length > 0) {
            setEntries(prev => mergeAndCap(prev, matches));
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
            if (!e.msg.toLowerCase().includes(q)
                && !(e.source ?? "").toLowerCase().includes(q)
                && !(e.exception ?? "").toLowerCase().includes(q)) {
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
        setFollowTail(prev => (prev !== near ? near : prev));
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
                setFollowTail(v => {
                    if (!v) requestAnimationFrame(scrollToBottom);
                    return !v;
                });
            }
        };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, []);

    const toggleLevel = useCallback((level: LogLevel) => {
        setEnabledLevels(prev => {
            const next = new Set(prev);
            if (next.has(level)) next.delete(level);
            else next.add(level);
            return next;
        });
    }, []);

    const toggleExpanded = useCallback((seq: number) => {
        setExpanded(prev => {
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
        return `/api/download-logs${params.toString() ? `?${params.toString()}` : ""}`;
    }, [enabledLevels, search, source]);

    const totalInBuffer = useMemo(
        () => Object.values(counts).reduce((a, b) => a + b, 0),
        [counts],
    );

    return (
        <div className="flex h-full min-h-0 min-w-full flex-col gap-4 overflow-hidden px-4 py-4 text-sm text-base-content/70 md:px-8">
            <div className="card shrink-0 border border-base-content/10 bg-base-100 shadow-sm">
                <div className="card-body gap-4 p-4 md:p-6">
                    <div className="flex flex-wrap items-start justify-between gap-4">
                        <div>
                            <div className="flex items-center gap-2.5">
                                <span
                                    className={`status status-sm ${connectionStatusClass(connection)}`}
                                    title={`WebSocket ${connection}`}
                                />
                                <h2 className="text-base font-semibold tracking-tight text-base-content">Logs</h2>
                            </div>
                            <p className="mt-1 text-xs text-base-content/50">
                                Live application logs from the in-memory ring buffer.
                                Last {capacity.toLocaleString()} entries are kept in RAM only, not persisted across restarts.
                            </p>
                        </div>
                        <div className="join flex w-full flex-wrap sm:w-auto">
                            <button
                                type="button"
                                className={`btn btn-sm join-item ${followTail ? "btn-primary" : "btn-ghost"}`}
                                onClick={() => {
                                    setFollowTail(v => {
                                        if (!v) requestAnimationFrame(scrollToBottom);
                                        return !v;
                                    });
                                }}
                                title="Auto-follow the latest entry. Shortcut: f">
                                {followTail ? "Following" : "Follow tail"}
                            </button>
                            <button
                                type="button"
                                className={`btn btn-sm join-item ${paused ? "btn-warning" : "btn-ghost"}`}
                                onClick={togglePause}
                                title={paused ? "Stream paused. Click to resume." : "Pause live stream."}>
                                {paused ? `Paused (${pendingCount})` : "Pause"}
                            </button>
                            <button
                                type="button"
                                className="btn btn-sm btn-ghost join-item"
                                onClick={clearView}
                                disabled={entries.length === 0}
                                title="Clear the on-screen view. Server buffer is untouched.">
                                Clear view
                            </button>
                            <a
                                href={downloadHref}
                                className="btn btn-sm btn-ghost join-item"
                                title="Download current view as a .log file."
                                download>
                                <Icon name="download" className="!text-[16px]" />
                                Download
                            </a>
                        </div>
                    </div>

                    <div className="join flex-wrap">
                        {ALL_LEVELS.map(level => (
                            <LevelChip
                                key={level}
                                level={level}
                                active={enabledLevels.has(level)}
                                count={counts[level] ?? 0}
                                onClick={() => toggleLevel(level)}
                            />
                        ))}
                    </div>

                    <div className="flex flex-wrap items-center gap-2">
                        <label className="input input-sm input-bordered flex min-w-0 flex-1 items-center gap-2 sm:min-w-[240px]">
                            <Icon name="search" className="!text-[16px] shrink-0 text-base-content/40" />
                            <input
                                ref={searchRef}
                                className="grow bg-transparent outline-none"
                                type="search"
                                value={searchInput}
                                onChange={(e: ChangeEvent<HTMLInputElement>) => setSearchInput(e.target.value)}
                                onKeyDown={(e: KeyboardEvent<HTMLInputElement>) => {
                                    if (e.key === "Escape") { setSearchInput(""); e.currentTarget.blur(); }
                                }}
                                placeholder="Search messages, sources, stack traces…  ( / to focus )"
                                spellCheck={false}
                                autoComplete="off"
                            />
                        </label>
                        <Input
                            className="input-sm w-full sm:w-auto sm:max-w-[220px]"
                            type="text"
                            value={sourceInput}
                            onChange={(e: ChangeEvent<HTMLInputElement>) => setSourceInput(e.target.value)}
                            placeholder="Source filter (e.g. NzbWebDAV.Queue)"
                            spellCheck={false}
                            autoComplete="off"
                        />
                        <span className="ml-auto font-mono text-[11px] whitespace-nowrap tabular-nums text-base-content/50 max-sm:ml-0">
                            {entries.length.toLocaleString()} shown · {totalInBuffer.toLocaleString()}/{capacity.toLocaleString()} in buffer
                        </span>
                    </div>

                    {errorText && (
                        <Alert variant="danger" className="text-xs">
                            Couldn&apos;t load logs: {errorText}
                        </Alert>
                    )}
                </div>
            </div>

            <div className="card relative flex min-h-0 flex-1 flex-col overflow-hidden border border-base-content/10 bg-base-100 shadow-sm">
                {entries.length === 0 ? (
                    <div className="card-body items-center justify-center gap-1 py-16 text-center text-base-content/50">
                        <div className="text-sm text-base-content/70">No log entries to show.</div>
                        <div className="text-xs">
                            {totalInBuffer === 0
                                ? "Nothing has been logged yet."
                                : "Try widening your filters."}
                        </div>
                    </div>
                ) : (
                    <div
                        ref={listRef}
                        className="yes-scrollbar min-h-0 flex-1 overflow-x-hidden overflow-y-auto py-1 font-mono text-xs leading-normal"
                        onScroll={handleScroll}
                    >
                        {entries.map(entry => (
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
                        className="btn btn-sm btn-primary absolute right-4 bottom-4 z-2 gap-2 shadow-lg"
                        onClick={() => { setFollowTail(true); requestAnimationFrame(scrollToBottom); }}>
                        <Icon name="keyboard_arrow_down" className="!text-[16px]" />
                        Jump to live
                    </button>
                )}
            </div>
        </div>
    );
}

function LogRow({ entry, expanded, onToggle }: {
    entry: LogEntry,
    expanded: boolean,
    onToggle: () => void,
}) {
    const rowClass = `grid cursor-pointer grid-cols-[86px_64px_1fr] items-baseline gap-3 border-l-2 border-transparent px-3.5 py-0.5 transition-colors duration-75 [contain:content] hover:bg-base-content/5 max-[899px]:grid-cols-[70px_56px_1fr] max-[899px]:gap-2 max-[899px]:px-2.5 max-[899px]:py-1 ${levelRowClass(entry.level)}`;
    return (
        <div className={rowClass} onClick={onToggle} title={entry.exception ? "Click to toggle stack trace" : undefined}>
            <span className="whitespace-nowrap tabular-nums text-base-content/40">{formatTime(entry.ts)}</span>
            <Badge className={`badge-xs uppercase tracking-wide ${levelBadgeClass(entry.level)}`}>
                {shortLevel(entry.level)}
            </Badge>
            <span className="flex min-w-0 flex-col gap-0.5 break-words">
                <span className="log-msg whitespace-pre-wrap break-words text-base-content">{entry.msg}</span>
                {entry.source && <span className="text-[11px] text-base-content/40 max-[899px]:text-[10.5px]">{entry.source}</span>}
                {entry.exception && !expanded && (
                    <span className="mt-0.5 text-[10.5px] text-base-content/50">▸ click to view stack trace</span>
                )}
                {entry.exception && expanded && (
                    <pre className="mt-1 mb-0.5 max-h-80 overflow-auto whitespace-pre-wrap rounded-md border border-base-content/10 bg-base-300 p-2 text-[11px] text-base-content/70">{entry.exception}</pre>
                )}
            </span>
        </div>
    );
}

function LevelChip({ level, active, count, onClick }: {
    level: LogLevel,
    active: boolean,
    count: number,
    onClick: () => void,
}) {
    const activeClass = !active ? "btn-ghost opacity-55" : levelActiveBtnClass(level);
    return (
        <button
            type="button"
            className={`btn btn-xs join-item gap-2 uppercase tracking-wide ${activeClass}`}
            onClick={onClick}>
            <span>{shortLevel(level)}</span>
            <span className="badge badge-xs font-mono tabular-nums opacity-70">{count}</span>
        </button>
    );
}

function levelActiveBtnClass(level: LogLevel): string {
    switch (level) {
        case "Information": return "btn-info";
        case "Warning": return "btn-warning";
        case "Error": return "btn-error";
        case "Fatal": return "btn-error";
        case "Debug": return "btn-neutral";
        case "Verbose": return "btn-ghost";
    }
}

function levelBadgeClass(level: LogLevel): string {
    switch (level) {
        case "Information": return "badge-info";
        case "Warning": return "badge-warning";
        case "Error": return "badge-error";
        case "Fatal": return "badge-error";
        case "Debug": return "badge-ghost";
        case "Verbose": return "badge-ghost";
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
        case "live": return "status-success";
        case "reconnecting":
        case "connecting": return "status-warning animate-pulse";
        case "disconnected": return "status-error";
    }
}

function shortLevel(level: LogLevel): string {
    switch (level) {
        case "Verbose": return "trace";
        case "Debug": return "debug";
        case "Information": return "info";
        case "Warning": return "warn";
        case "Error": return "error";
        case "Fatal": return "fatal";
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
    const parts = raw.split(",").map(s => s.trim()).filter(Boolean);
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
    const fresh = incoming.filter(e => e.seq > lastSeq);
    if (fresh.length === 0) return prev;
    const next = prev.concat(fresh);
    if (next.length <= CLIENT_MAX_ENTRIES) return next;
    return next.slice(next.length - CLIENT_MAX_ENTRIES);
}
