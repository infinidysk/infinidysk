import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { formatBytes, formatSessionAge } from "../../utils/format";
import { displayNameForRead } from "../../utils/display-name";
import { clientIdentityTooltip, clientLabelFromUserAgent } from "~/utils/client-label";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { Tooltip } from "~/components/ui";
import { Sparkline } from "../provider-scoreboard/provider-scoreboard";
import { mockLiveReadRows, mockReadsRequested } from "./live-reads-panel.mock";
import type {
  CurrentActivitySnapshot,
  CurrentPlaybackActivity,
  CurrentProviderContribution,
  CurrentTransportActivity,
  PlaybackAuthoritySnapshot,
  PlaybackDeliveryMethod,
  PlaybackState,
} from "./current-activity";

const TOPIC_CURRENT_ACTIVITY = "ca";

// The broadcaster ticks once per second; 60 samples ≈ the last minute.
const HISTORY_LIMIT = 60;

export type LiveReadRow = {
  read: CurrentTransportActivity;
  /** Smoothed InfiniDysk delivery rate in bytes/sec. */
  rate: number;
  /** Recent delivery-rate samples for transport diagnostics. */
  history: number[];
};

/**
 * Trustworthy "Right now" panel. Playback membership/state comes only from
 * CurrentActivity's authoritative playback registry; transport rows remain
 * transport diagnostics and are never promoted to viewers by byte activity.
 */
export function LiveReadsPanel({
  paused = false,
  summary,
}: {
  paused?: boolean;
  summary?: ReactNode;
}) {
  const [playback, setPlayback] = useState<CurrentPlaybackActivity[]>([]);
  const [rows, setRows] = useState<LiveReadRow[]>([]);
  const [authorities, setAuthorities] = useState<PlaybackAuthoritySnapshot[]>([]);
  const [mockCount, setMockCount] = useState<number | null>(null);
  const [snapshotReady, setSnapshotReady] = useState(false);
  const prevRef = useRef<Map<string, { bytes: number; at: number; rate: number }>>(new Map());
  const historyRef = useRef<Map<string, number[]>>(new Map());

  useEffect(() => {
    const count = mockReadsRequested();
    if (count == null) return;
    setMockCount(count);
    setPlayback([]);
    setAuthorities([]);
    setRows(
      mockLiveReadRows(count).map(({ read, rate, history }) => ({
        read: {
          id: read.id,
          davItemId: null,
          fileName: read.fileName,
          path: read.path,
          startedAt: new Date(read.startedAt).toISOString(),
          lastActivityAt: new Date(read.lastActivityAt).toISOString(),
          bytesRead: read.bytesRead,
          bytesFetched: read.bytesFetched ?? 0,
          sourceOffset: read.currentOffset,
          fileSize: read.fileSize ?? null,
          clientIp: read.clientIp ?? null,
          clientUserAgent: read.clientUserAgent ?? null,
          playerSession: null,
          correlationScope: 0,
          matchingPlaybackSessionCount: 0,
          shared: false,
          providers: read.providers.map((provider) => ({
            host: provider.host,
            nickname: provider.nickname ?? null,
            segments: provider.segments,
          })),
        },
        rate,
        history,
      })),
    );
    setSnapshotReady(true);
  }, []);

  useWebsocketTopic(
    TOPIC_CURRENT_ACTIVITY,
    "state",
    (message) => {
      if (mockReadsRequested() != null) return;
      try {
        const payload = JSON.parse(message) as CurrentActivitySnapshot;
        const now = Date.now();
        const prev = prevRef.current;
        const next = new Map<string, { bytes: number; at: number; rate: number }>();
        const nextHistory = new Map<string, number[]>();
        const nextRows: LiveReadRow[] = [];

        for (const read of payload.reads ?? []) {
          const old = prev.get(read.id);
          let rate = old?.rate ?? 0;
          if (old && now > old.at) {
            const dt = (now - old.at) / 1000;
            const db = read.bytesRead - old.bytes;
            if (dt > 0 && db >= 0) {
              const instant = db / dt;
              rate = old.rate * 0.4 + instant * 0.6;
            }
          }

          next.set(read.id, { bytes: read.bytesRead, at: now, rate });
          const history = [...(historyRef.current.get(read.id) ?? []), rate].slice(-HISTORY_LIMIT);
          nextHistory.set(read.id, history);
          nextRows.push({ read, rate, history });
        }

        prevRef.current = next;
        historyRef.current = nextHistory;
        setPlayback(payload.playback ?? []);
        setAuthorities(payload.authorities ?? []);
        setRows(nextRows);
        setSnapshotReady(true);
      } catch {
        // Ignore malformed frames; the next replayable state snapshot can recover.
      }
    },
    { enabled: !paused && mockCount == null },
  );

  return (
    <LiveReadsPanelContent
      playback={playback}
      rows={rows}
      authorities={authorities}
      snapshotReady={snapshotReady}
      summary={summary}
    />
  );
}

export function LiveReadsPanelContent({
  playback,
  rows,
  authorities = [],
  snapshotReady = true,
  summary,
}: {
  playback: CurrentPlaybackActivity[];
  rows: LiveReadRow[];
  authorities?: PlaybackAuthoritySnapshot[];
  snapshotReady?: boolean;
  summary?: ReactNode;
}) {
  const readById = new Map(rows.map((row) => [row.read.id, row]));
  const displayedPlayback = [...playback].sort(comparePlayback);
  const transportOnlyRows = rows
    .filter(({ read }) => read.matchingPlaybackSessionCount === 0)
    .sort((a, b) => parseTimestamp(b.read.startedAt) - parseTimestamp(a.read.startedAt));
  const unavailableAuthorities = authorities.filter(
    (authority) => !authority.available || authority.isStale,
  );

  const playing = playback.filter(({ session }) => session.state === "Playing").length;
  const paused = playback.filter(({ session }) => session.state === "Paused").length;
  const buffering = playback.filter(({ session }) => session.state === "Buffering").length;
  const includeOtherReadCount = summary == null || playback.length > 0;
  const activitySummary = [
    playing > 0 ? `${playing} playing` : null,
    paused > 0 ? `${paused} paused` : null,
    buffering > 0 ? `${buffering} buffering` : null,
    includeOtherReadCount && transportOnlyRows.length > 0
      ? `${transportOnlyRows.length} other reads`
      : null,
  ]
    .filter(Boolean)
    .join(" · ");

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
  const hasActivity = displayedPlayback.length > 0 || transportOnlyRows.length > 0;

  return (
    <section
      ref={cardRef}
      id="active-reads"
      className={`card w-full min-w-0 scroll-mt-20 border border-base-content/10 bg-base-100 shadow-sm${heightLocked ? " overflow-hidden" : ""}`}
      style={heightLocked ? { height: lockedHeight } : undefined}
    >
      <div className="card-body flex h-full min-h-0 flex-col gap-3 p-4">
        <div className="flex shrink-0 items-center gap-2.5">
          <span className="status status-success animate-pulse" aria-hidden="true" />
          <h3 className="card-title m-0 text-base">Right now</h3>
          {activitySummary && (
            <span className="badge badge-ghost badge-sm ml-auto font-mono tabular-nums">
              {activitySummary}
            </span>
          )}
        </div>

        {summary && <div className="shrink-0 border-b border-base-content/10 pb-3">{summary}</div>}

        {unavailableAuthorities.length > 0 && (
          <div className="flex shrink-0 flex-wrap gap-1.5 text-xs text-warning">
            {unavailableAuthorities.map((authority) => (
              <span
                key={authority.sourceInstanceId}
                className="badge badge-warning badge-outline badge-sm"
              >
                {authority.sourceInstanceName}: playback status stale/unavailable
              </span>
            ))}
          </div>
        )}

        {!hasActivity ? (
          <p className="m-0 text-sm text-base-content/50">
            No confirmed playback or active InfiniDysk reads right now.
          </p>
        ) : (
          <ul
            className={
              heightLocked
                ? "yes-scrollbar m-0 min-h-0 w-full min-w-0 flex-1 list-none divide-y divide-base-content/10 overflow-x-hidden overflow-y-auto py-0 pr-4 pl-0"
                : "m-0 w-full min-w-0 list-none divide-y divide-base-content/10 overflow-x-hidden py-0 pr-4 pl-0"
            }
          >
            {displayedPlayback.map((activity) => (
              <PlaybackRow
                key={`${activity.session.key.sourceInstanceId}:${activity.session.nativeSessionId}`}
                activity={activity}
                readById={readById}
              />
            ))}
            {transportOnlyRows.map((row) => (
              <TransportOnlyRow key={row.read.id} row={row} />
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

function PlaybackRow({
  activity,
  readById,
}: {
  activity: CurrentPlaybackActivity;
  readById: Map<string, LiveReadRow>;
}) {
  const session = activity.session;
  const transport = activity.transportReadIds
    .map((id) => readById.get(id))
    .filter((row): row is LiveReadRow => row !== undefined);
  const rate = transport.reduce((sum, row) => sum + row.rate, 0);
  const fetched = transport.reduce((sum, row) => sum + row.read.bytesFetched, 0);
  const providers = aggregateProviders(transport);
  const title =
    session.title?.trim() || session.mediaSourcePath || session.itemId || "Playback session";
  const pct =
    session.positionMs !== null && session.durationMs !== null && session.durationMs > 0
      ? Math.min(100, Math.max(0, (session.positionMs / session.durationMs) * 100))
      : null;
  const delivery = deliveryMethodLabel(session.deliveryMethod);
  const episode = episodeLabel(session.seriesName, session.seasonNumber, session.episodeNumber);
  const device = session.deviceName ?? session.clientName;
  const sourceDetail =
    transport.length === 0
      ? "InfiniDysk source: idle"
      : `InfiniDysk source: ${formatBytes(rate)}/s${fetched > 0 ? ` · fetched ${formatBytes(fetched)}` : ""}`;

  return (
    <li className="flex min-w-0 flex-col gap-1.5 overflow-x-hidden py-2 first:pt-0 last:pb-0">
      <div className="flex min-w-0 items-center gap-2">
        <span className={stateBadgeClass(session.state)}>{session.state.toUpperCase()}</span>
        <Tooltip
          className="min-w-0 flex-1 overflow-hidden"
          content={session.mediaSourcePath ?? title}
        >
          <span className="block truncate text-xs font-bold text-base-content">{title}</span>
        </Tooltip>
        {session.freshness === "Stale" && (
          <span className="badge badge-warning badge-outline badge-xs">STALE</span>
        )}
      </div>

      <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 text-xs text-base-content/60">
        <span className="font-medium text-base-content">{session.sourceInstanceName}</span>
        {session.userName && <span>{session.userName}</span>}
        {device && <span>{device}</span>}
        {delivery && <span>{delivery}</span>}
        {episode && <span>{episode}</span>}
        {activity.hasSharedFileTransport && (
          <span className="badge badge-ghost badge-xs">shared/file-level transport</span>
        )}
      </div>

      <div className="flex min-w-0 items-center gap-3">
        <span className="shrink-0 font-mono text-xs tabular-nums text-base-content">
          {formatPlaybackTime(session.positionMs)} / {formatPlaybackTime(session.durationMs)}
        </span>
        <progress
          className="progress progress-primary h-1 min-w-16 flex-1"
          value={pct ?? 0}
          max={100}
          aria-label={pct === null ? "Playback progress unavailable" : "Playback progress"}
        />
      </div>

      <div className="flex min-w-0 flex-wrap items-center gap-x-2.5 gap-y-0.5 text-xs text-base-content/50">
        <span>{sourceDetail}</span>
        {activity.hasSharedFileTransport && transport.length > 0 && (
          <span>telemetry is shared across viewers/file reads</span>
        )}
        {providers.slice(0, 6).map((provider) => (
          <Tooltip
            key={provider.key}
            content={`${provider.label} (${provider.host}): ${provider.segments} segments`}
          >
            <span className="badge badge-ghost badge-xs max-w-full gap-1 font-mono tabular-nums">
              <span className="max-w-[7rem] truncate">{provider.label}</span>
              <span className="font-medium">{provider.segments}</span>
            </span>
          </Tooltip>
        ))}
      </div>
    </li>
  );
}

function TransportOnlyRow({ row }: { row: LiveReadRow }) {
  const { read, rate, history } = row;
  const display = displayNameForRead(read.fileName, read.path);
  const pct =
    read.fileSize && read.fileSize > 0
      ? Math.min(100, Math.max(0, (read.sourceOffset / read.fileSize) * 100))
      : null;
  const age = formatSessionAge(parseTimestamp(read.startedAt));
  const clientTooltip = clientIdentityTooltip(read.clientUserAgent, read.clientIp) ?? "";

  return (
    <li className="flex min-w-0 flex-col gap-1 overflow-x-hidden py-2 first:pt-0 last:pb-0">
      <div className="flex min-w-0 flex-col gap-1 lg:flex-row lg:items-center lg:gap-x-4">
        <div className="flex min-w-0 items-center gap-2 lg:flex-1">
          <span className="badge badge-ghost badge-xs shrink-0">READ</span>
          <Tooltip
            className="min-w-0 overflow-hidden"
            content={display.isReleaseFallback ? `${read.path}\n(obfuscated file name)` : read.path}
          >
            <span className="block truncate text-xs font-bold text-base-content">
              {display.name}
            </span>
          </Tooltip>
        </div>

        <div className="flex w-full min-w-0 items-center gap-x-2.5 font-mono text-xs tabular-nums lg:w-auto lg:shrink-0 lg:gap-x-3">
          {history.length >= 2 && (
            <span className="hidden shrink-0 sm:block">
              <Sparkline values={history} tone="secondary" />
            </span>
          )}
          <span className="w-[4.5rem] shrink-0 font-medium text-secondary lg:w-[5.5rem]">
            {formatBytes(rate)}/s
          </span>
          <span className="min-w-0 flex-1 truncate font-medium text-base-content lg:w-[10rem] lg:flex-none">
            source {formatBytes(read.sourceOffset)}
            {read.fileSize ? (
              <span className="font-normal text-base-content/50">
                {" "}
                / {formatBytes(read.fileSize)}
              </span>
            ) : null}
          </span>
          <progress
            className="progress progress-success h-1 w-20 shrink-0 lg:w-28"
            value={pct ?? 0}
            max={100}
            aria-label={pct === null ? "Source progress unavailable" : "Source delivery progress"}
          />
        </div>
      </div>

      <div className="flex min-w-0 flex-wrap items-center gap-x-2.5 gap-y-0.5 overflow-hidden text-xs text-base-content/50">
        <span className="font-medium text-base-content/70">No matching playback session</span>
        <Tooltip className="min-w-0 max-w-full overflow-hidden" content={clientTooltip}>
          <span className="block max-w-full truncate">
            {clientLabelFromUserAgent(read.clientUserAgent)}
            {read.clientIp ? (
              <span className="hidden font-mono text-base-content/40 sm:inline">
                {" "}
                · {read.clientIp}
              </span>
            ) : null}
          </span>
        </Tooltip>
        {age && <span className="shrink-0">{age}</span>}
        {read.bytesFetched > 0 && (
          <Tooltip
            className="max-sm:hidden"
            content="Bytes pulled from Usenet for this transport read, including readahead"
          >
            <span className="font-mono tabular-nums">fetched {formatBytes(read.bytesFetched)}</span>
          </Tooltip>
        )}
        {read.providers.slice(0, 6).map((provider, index) => {
          const label = provider.nickname?.trim() || provider.host;
          return (
            <Tooltip
              key={`${provider.host}-${index}`}
              content={`${label} (${provider.host}): ${provider.segments} segments`}
            >
              <span className="badge badge-ghost badge-xs max-w-full gap-1 font-mono tabular-nums">
                <span className="max-w-[7rem] truncate">{label}</span>
                <span className="font-medium">{provider.segments}</span>
              </span>
            </Tooltip>
          );
        })}
      </div>
    </li>
  );
}

function comparePlayback(a: CurrentPlaybackActivity, b: CurrentPlaybackActivity): number {
  const rank = (state: PlaybackState) => {
    switch (state) {
      case "Playing":
        return 0;
      case "Buffering":
        return 1;
      case "Paused":
        return 2;
      default:
        return 3;
    }
  };
  return (
    rank(a.session.state) - rank(b.session.state) ||
    parseTimestamp(b.session.lastConfirmedAt) - parseTimestamp(a.session.lastConfirmedAt)
  );
}

function stateBadgeClass(state: PlaybackState): string {
  switch (state) {
    case "Playing":
      return "badge badge-success badge-xs shrink-0";
    case "Paused":
      return "badge badge-warning badge-xs shrink-0";
    case "Buffering":
      return "badge badge-info badge-xs shrink-0";
    default:
      return "badge badge-ghost badge-xs shrink-0";
  }
}

function deliveryMethodLabel(method: PlaybackDeliveryMethod): string | null {
  switch (method) {
    case "DirectPlay":
      return "Direct Play";
    case "DirectStream":
      return "Direct Stream";
    case "Transcode":
      return "Transcode";
    default:
      return null;
  }
}

function episodeLabel(
  seriesName: string | null,
  seasonNumber: number | null,
  episodeNumber: number | null,
): string | null {
  if (!seriesName) return null;
  if (seasonNumber === null || episodeNumber === null) return seriesName;
  return `${seriesName} · S${String(seasonNumber).padStart(2, "0")}E${String(episodeNumber).padStart(2, "0")}`;
}

function formatPlaybackTime(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms) || ms < 0) return "—";
  const total = Math.floor(ms / 1000);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return `${hours > 0 ? `${hours}:` : ""}${hours > 0 ? String(minutes).padStart(2, "0") : minutes}:${String(seconds).padStart(2, "0")}`;
}

function parseTimestamp(value: string): number {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

type AggregatedProvider = CurrentProviderContribution & { key: string; label: string };

function aggregateProviders(rows: LiveReadRow[]): AggregatedProvider[] {
  const providers = new Map<string, AggregatedProvider>();
  for (const { read } of rows) {
    for (const provider of read.providers) {
      const key = `${provider.host}\0${provider.nickname ?? ""}`;
      const existing = providers.get(key);
      if (existing) {
        existing.segments += provider.segments;
        continue;
      }
      providers.set(key, {
        ...provider,
        key,
        label: provider.nickname?.trim() || provider.host,
      });
    }
  }
  return [...providers.values()].sort((a, b) => b.segments - a.segments);
}
