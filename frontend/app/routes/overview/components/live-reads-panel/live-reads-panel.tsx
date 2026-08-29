import { useEffect, useRef, useState } from "react";
import type { ActiveRead, ActiveReadsMessage } from "~/clients/backend-client.server";
import { formatBytes, formatSessionAge, formatTimeLeft } from "../../utils/format";
import { mediaTypeFromFileName } from "../../utils/media-type";
import { displayNameForRead } from "../../utils/display-name";
import { clientIdentityTooltip, clientLabelFromUserAgent } from "~/utils/client-label";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { Tooltip } from "~/components/ui";
import { Sparkline } from "../provider-scoreboard/provider-scoreboard";

const TOPIC_ACTIVE_READS = "ar";

export type LiveReadRow = {
  read: ActiveRead;
  /** Smoothed download rate in bytes/sec. */
  rate: number;
  /** Recent rate samples (one per broadcast tick) for the sparkline. */
  history: number[];
};

// The broadcaster ticks once per second; 60 samples ≈ the last minute.
const HISTORY_LIMIT = 60;

/**
 * Live "right now" panel — full-width rows refreshed via the ActiveReads WS
 * topic. Keeps an empty state when no reads are active so the dashboard stack
 * stays stable. When `paused`, the subscription is disabled so layout edit
 * borders stay stable.
 */
export function LiveReadsPanel({ paused = false }: { paused?: boolean }) {
  const [rows, setRows] = useState<LiveReadRow[]>([]);
  // Track previous bytesRead per session for live MiB/s computation.
  const prevRef = useRef<Map<string, { bytes: number; at: number; rate: number }>>(new Map());
  // Per-session rate samples for the sparkline, keyed by session id.
  const historyRef = useRef<Map<string, number[]>>(new Map());

  useWebsocketTopic(
    TOPIC_ACTIVE_READS,
    "state",
    (message) => {
      try {
        // ActiveReads websocket topic payload shape (backend contract)
        const payload = JSON.parse(message) as ActiveReadsMessage;
        const now = Date.now();
        const prev = prevRef.current;
        const next = new Map<string, { bytes: number; at: number; rate: number }>();
        const nextHistory = new Map<string, number[]>();
        const nextRows: LiveReadRow[] = [];
        for (const r of payload.reads ?? []) {
          const old = prev.get(r.id);
          let rate = old?.rate ?? 0;
          if (old && now > old.at) {
            const dt = (now - old.at) / 1000;
            const db = r.bytesRead - old.bytes;
            if (dt > 0 && db >= 0) {
              const instant = db / dt;
              rate = old.rate * 0.4 + instant * 0.6;
            }
          }
          next.set(r.id, { bytes: r.bytesRead, at: now, rate });
          const history = [...(historyRef.current.get(r.id) ?? []), rate].slice(-HISTORY_LIMIT);
          nextHistory.set(r.id, history);
          nextRows.push({ read: r, rate, history });
        }
        prevRef.current = next;
        historyRef.current = nextHistory;
        setRows(nextRows);
      } catch {
        /* ignore */
      }
    },
    { enabled: !paused },
  );

  return <LiveReadsPanelContent rows={rows} />;
}

export function LiveReadsPanelContent({ rows }: { rows: LiveReadRow[] }) {
  const [copiedId, setCopiedId] = useState<string | null>(null);
  const [copyNotice, setCopyNotice] = useState<{ seq: number; text: string } | null>(null);
  const copySeqRef = useRef(0);
  const copyTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const displayedRows = [...rows].sort((a, b) => b.read.startedAt - a.read.startedAt);

  useEffect(() => {
    return () => {
      if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    };
  }, []);

  const copySessionId = async (id: string) => {
    try {
      await navigator.clipboard.writeText(id);
    } catch {
      return;
    }
    copySeqRef.current += 1;
    setCopiedId(id);
    setCopyNotice({ seq: copySeqRef.current, text: "Session id copied" });
    if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    copyTimerRef.current = setTimeout(() => {
      setCopiedId((current) => (current === id ? null : current));
      copyTimerRef.current = null;
    }, 1500);
  };

  return (
    <section className="card h-[30rem] w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body flex min-h-0 flex-col gap-3 p-4">
        <div className="flex items-center gap-2.5">
          <span className="status status-success animate-pulse" aria-hidden="true" />
          <h3 className="card-title m-0 text-base">Right now</h3>
          {rows.length > 0 && (
            <span className="badge badge-ghost badge-sm ml-auto font-mono tabular-nums">
              {rows.length} active
            </span>
          )}
        </div>

        <div key={copyNotice?.seq ?? 0} className="sr-only" aria-live="polite">
          {copyNotice?.text ?? ""}
        </div>

        {rows.length === 0 ? (
          <p className="m-0 text-sm text-base-content/50">
            No files are being read right now. Open a mounted file to see live progress here.
          </p>
        ) : (
          <ul className="yes-scrollbar m-0 min-h-0 w-full flex-1 list-none divide-y divide-base-content/10 overflow-y-auto p-0">
            {displayedRows.map(({ read, rate, history }) => (
              <ReadRow
                key={read.id}
                read={read}
                rate={rate}
                history={history}
                copied={copiedId === read.id}
                onCopySessionId={(id) => {
                  void copySessionId(id);
                }}
              />
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

function ReadRow({
  read: r,
  rate,
  history,
  copied,
  onCopySessionId,
}: {
  read: ActiveRead;
  rate: number;
  history: number[];
  copied: boolean;
  onCopySessionId: (id: string) => void;
}) {
  const display = displayNameForRead(r.fileName, r.path);
  const mediaType = mediaTypeFromFileName(display.name);
  // Use the latest read position (what the player is requesting right now) —
  // not cumulative bytes transferred — so the bar reflects actual playback
  // location, immune to seeks/replays.
  const pct =
    r.fileSize && r.fileSize > 0 ? Math.min(100, (r.currentOffset / r.fileSize) * 100) : null;
  const timeLeft = r.fileSize
    ? formatTimeLeft(Math.max(0, r.fileSize - r.currentOffset), rate)
    : null;
  const sessionAge = formatSessionAge(r.startedAt);
  const bytesFetched = r.bytesFetched ?? 0;
  // Total bytes served well past the current position means the player is
  // re-reading ranges (scrubbing / replaying), not streaming linearly.
  const scrubbing = r.bytesRead > r.currentOffset + Math.max(64_000_000, r.currentOffset * 0.2);

  return (
    <li className="flex flex-col gap-1.5 py-3 first:pt-0 last:pb-0">
      <div className="flex flex-col gap-1.5 lg:flex-row lg:items-center lg:gap-x-4">
        <div className="flex min-w-0 items-center gap-2 lg:flex-1">
          {mediaType && (
            <span
              className={`badge badge-sm shrink-0 ${
                mediaType === "movie" ? "badge-primary" : "badge-secondary"
              }`}
            >
              {mediaType === "movie" ? "MOVIE" : "EPISODE"}
            </span>
          )}
          <Tooltip
            className="min-w-0 flex-1"
            content={display.isReleaseFallback ? `${r.path}\n(obfuscated file name)` : r.path}
          >
            <span className="block truncate text-sm font-medium text-base-content">
              {display.name}
            </span>
          </Tooltip>
        </div>
        <div className="flex min-w-0 flex-wrap items-center gap-x-4 gap-y-1 font-mono text-xs tabular-nums">
          {history.length >= 2 && (
            <span className="hidden sm:block">
              <Sparkline values={history} tone="success" />
            </span>
          )}
          <span className="font-medium text-success">{formatBytes(rate)}/s</span>
          <span className="font-medium text-base-content">
            {formatBytes(r.currentOffset)}
            {r.fileSize ? (
              <span className="font-normal text-base-content/50"> / {formatBytes(r.fileSize)}</span>
            ) : null}
          </span>
          <span className="text-base-content/50">{timeLeft ?? "—"}</span>
        </div>
      </div>

      {pct !== null ? (
        <progress className="progress progress-success h-1 w-full" value={pct} max={100} />
      ) : (
        <span
          className="loading loading-bars loading-sm text-success"
          role="status"
          aria-label="Loading progress"
        />
      )}

      <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-xs text-base-content/50">
        <Tooltip content={clientIdentityTooltip(r.clientUserAgent, r.clientIp) ?? ""}>
          <span className="truncate">
            {clientLabelFromUserAgent(r.clientUserAgent)}
            {r.clientIp ? (
              <span className="font-mono text-base-content/40"> · {r.clientIp}</span>
            ) : null}
          </span>
        </Tooltip>
        {sessionAge && <span>{sessionAge}</span>}
        {bytesFetched > 0 && (
          <Tooltip content="Bytes pulled from Usenet for this session, including readahead">
            <span className="font-mono tabular-nums">fetched {formatBytes(bytesFetched)}</span>
          </Tooltip>
        )}
        {scrubbing && (
          <Tooltip content="Total bytes served to the player, including seeks and replays">
            <span className="font-mono tabular-nums">{formatBytes(r.bytesRead)} served</span>
          </Tooltip>
        )}
        {r.providers.length > 0 && (
          <span className="flex min-w-0 flex-wrap gap-1">
            {r.providers.slice(0, 6).map((p, i) => {
              const label = p.nickname?.trim() || p.host;
              return (
                <Tooltip
                  key={`${p.host}-${i}`}
                  content={`${label} (${p.host}): ${p.segments} segments`}
                >
                  <span className="badge badge-ghost badge-sm gap-1.5 font-mono tabular-nums">
                    <span className="max-w-[8rem] truncate">{label}</span>
                    <span className="font-medium">{p.segments}</span>
                  </span>
                </Tooltip>
              );
            })}
          </span>
        )}
        <Tooltip content={`Copy session id: ${r.id}`}>
          <button
            type="button"
            className="btn btn-link btn-xs h-auto min-h-0 px-0 font-mono"
            aria-label={`Copy session id: ${r.id}${copied ? " (copied)" : ""}`}
            onClick={() => onCopySessionId(r.id)}
          >
            {copied ? "Copied" : shortSessionId(r.id)}
          </button>
        </Tooltip>
      </div>
    </li>
  );
}

function shortSessionId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}
