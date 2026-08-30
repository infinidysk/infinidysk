import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { ActiveRead, ActiveReadsMessage } from "~/clients/backend-client.server";
import { formatBytes, formatSessionAge, formatTimeLeft } from "../../utils/format";
import { displayNameForRead } from "../../utils/display-name";
import { clientIdentityTooltip, clientLabelFromUserAgent } from "~/utils/client-label";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { Tooltip } from "~/components/ui";
import { Sparkline } from "../provider-scoreboard/provider-scoreboard";
import { mockLiveReadRows, mockReadsRequested } from "./live-reads-panel.mock";

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
 * topic. Sizes to the first snapshot of reads, then freezes that height until
 * the next page load so later sessions scroll instead of stretching the card.
 * When `paused`, the subscription is disabled so layout edit borders stay stable.
 */
export function LiveReadsPanel({ paused = false }: { paused?: boolean }) {
  const [rows, setRows] = useState<LiveReadRow[]>([]);
  const [mockCount, setMockCount] = useState<number | null>(null);
  const [snapshotReady, setSnapshotReady] = useState(false);
  // Track previous bytesRead per session for live MiB/s computation.
  const prevRef = useRef<Map<string, { bytes: number; at: number; rate: number }>>(new Map());
  // Per-session rate samples for the sparkline, keyed by session id.
  const historyRef = useRef<Map<string, number[]>>(new Map());

  useEffect(() => {
    const count = mockReadsRequested();
    if (count == null) return;
    setMockCount(count);
    setRows(mockLiveReadRows(count));
    setSnapshotReady(true);
  }, []);

  useWebsocketTopic(
    TOPIC_ACTIVE_READS,
    "state",
    (message) => {
      if (mockReadsRequested() != null) return;
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
        setSnapshotReady(true);
      } catch {
        /* ignore */
      }
    },
    { enabled: !paused && mockCount == null },
  );

  return <LiveReadsPanelContent rows={rows} snapshotReady={snapshotReady} />;
}

export function LiveReadsPanelContent({
  rows,
  snapshotReady = true,
}: {
  rows: LiveReadRow[];
  snapshotReady?: boolean;
}) {
  const displayedRows = [...rows].sort((a, b) => b.read.startedAt - a.read.startedAt);
  const cardRef = useRef<HTMLElement>(null);
  const [lockedHeight, setLockedHeight] = useState<number | null>(null);

  useLayoutEffect(() => {
    if (!snapshotReady || lockedHeight != null) return;
    const card = cardRef.current;
    if (!card) return;

    const lockFromCard = (): boolean => {
      const height = card.getBoundingClientRect().height;
      if (height < 1) return false;
      setLockedHeight(height);
      return true;
    };

    if (lockFromCard()) return;

    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => {
      if (lockFromCard()) observer.disconnect();
    });
    observer.observe(card);
    return () => observer.disconnect();
  }, [snapshotReady, lockedHeight]);

  const heightLocked = lockedHeight != null;

  return (
    <section
      ref={cardRef}
      className={`card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm${heightLocked ? " overflow-hidden" : ""}`}
      style={heightLocked ? { height: lockedHeight } : undefined}
    >
      <div className="card-body flex h-full min-h-0 flex-col gap-3 p-4">
        <div className="flex shrink-0 items-center gap-2.5">
          <span className="status status-success animate-pulse" aria-hidden="true" />
          <h3 className="card-title m-0 text-base">Right now</h3>
          {rows.length > 0 && (
            <span className="badge badge-ghost badge-sm ml-auto font-mono tabular-nums">
              {rows.length} active
            </span>
          )}
        </div>

        {rows.length === 0 ? (
          <p className="m-0 text-sm text-base-content/50">
            No files are being read right now. Open a mounted file to see live progress here.
          </p>
        ) : (
          <ul
            className={
              heightLocked
                ? "yes-scrollbar m-0 min-h-0 w-full min-w-0 flex-1 list-none divide-y divide-base-content/10 overflow-x-hidden overflow-y-auto py-0 pr-4 pl-0"
                : "m-0 w-full min-w-0 list-none divide-y divide-base-content/10 overflow-x-hidden py-0 pr-4 pl-0"
            }
          >
            {displayedRows.map(({ read, rate, history }) => (
              <ReadRow key={read.id} read={read} rate={rate} history={history} />
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
}: {
  read: ActiveRead;
  rate: number;
  history: number[];
}) {
  const display = displayNameForRead(r.fileName, r.path);
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
    <li className="flex min-w-0 flex-col gap-1 overflow-x-hidden py-2 first:pt-0 last:pb-0">
      <div className="flex min-w-0 flex-col gap-1 lg:flex-row lg:items-center lg:gap-x-4">
        <Tooltip
          className="min-w-0 overflow-hidden lg:flex-1"
          content={display.isReleaseFallback ? `${r.path}\n(obfuscated file name)` : r.path}
        >
          <span className="block truncate text-xs font-bold text-base-content">{display.name}</span>
        </Tooltip>

        <div className="flex w-full min-w-0 items-center gap-x-2.5 font-mono text-xs tabular-nums lg:w-auto lg:shrink-0 lg:gap-x-3">
          {history.length >= 2 && (
            <span className="hidden shrink-0 sm:block">
              <Sparkline values={history} tone="secondary" />
            </span>
          )}
          <span className="w-[4.5rem] shrink-0 font-medium text-secondary lg:w-[5.5rem]">
            {formatBytes(rate)}/s
          </span>
          <span className="min-w-0 flex-1 truncate font-medium text-base-content lg:w-[8.5rem] lg:flex-none">
            {formatBytes(r.currentOffset)}
            {r.fileSize ? (
              <span className="font-normal text-base-content/50"> / {formatBytes(r.fileSize)}</span>
            ) : null}
          </span>
          <div className="flex w-20 shrink-0 flex-col gap-1 lg:w-28">
            <progress
              className="progress progress-success h-1 w-full"
              value={pct ?? 0}
              max={100}
              aria-label={pct === null ? "Loading progress" : undefined}
            />
            <span className="text-end leading-none text-base-content/50">{timeLeft ?? "—"}</span>
          </div>
        </div>
      </div>

      <div className="flex min-w-0 flex-wrap items-center gap-x-2.5 gap-y-0.5 overflow-hidden text-xs text-base-content/50">
        <Tooltip
          className="min-w-0 max-w-full overflow-hidden"
          content={clientIdentityTooltip(r.clientUserAgent, r.clientIp) ?? ""}
        >
          <span className="block max-w-full truncate">
            {clientLabelFromUserAgent(r.clientUserAgent)}
            {r.clientIp ? (
              <span className="hidden font-mono text-base-content/40 sm:inline">
                {" "}
                · {r.clientIp}
              </span>
            ) : null}
          </span>
        </Tooltip>
        {sessionAge && <span className="shrink-0">{sessionAge}</span>}
        {bytesFetched > 0 && (
          <Tooltip
            className="max-sm:hidden"
            content="Bytes pulled from Usenet for this session, including readahead"
          >
            <span className="font-mono tabular-nums">fetched {formatBytes(bytesFetched)}</span>
          </Tooltip>
        )}
        {scrubbing && (
          <Tooltip content="Total bytes served to the player, including seeks and replays">
            <span className="font-mono tabular-nums">{formatBytes(r.bytesRead)} served</span>
          </Tooltip>
        )}
        {r.providers.length > 0 &&
          r.providers.slice(0, 6).map((p, i) => {
            const label = p.nickname?.trim() || p.host;
            return (
              <Tooltip
                key={`${p.host}-${i}`}
                content={`${label} (${p.host}): ${p.segments} segments`}
              >
                <span className="badge badge-ghost badge-xs max-w-full gap-1 font-mono tabular-nums">
                  <span className="max-w-[7rem] truncate">{label}</span>
                  <span className="font-medium">{p.segments}</span>
                </span>
              </Tooltip>
            );
          })}
      </div>
    </li>
  );
}
