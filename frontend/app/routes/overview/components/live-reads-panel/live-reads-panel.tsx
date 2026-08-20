import { useRef, useState } from "react";
import styles from "./live-reads-panel.module.css";
import type { ActiveRead, ActiveReadsMessage } from "~/clients/backend-client.server";
import { formatBytes } from "../../utils/format";
import { clientIdentityTooltip, clientLabelFromUserAgent } from "~/utils/client-label";
import { useWebsocketTopic } from "~/utils/shared-websocket";

const TOPIC_ACTIVE_READS = "ar";

/**
 * Live "right now" panel — reads cards refreshed via the ActiveReads WS topic.
 * Keeps an empty state when no reads are active so the dashboard rail is stable.
 * When `paused`, the subscription is disabled so layout edit borders stay stable.
 */
export function LiveReadsPanel({ paused = false }: { paused?: boolean }) {
  const [reads, setReads] = useState<ActiveRead[]>([]);
  // Track previous bytesRead per session for live MiB/s computation.
  const prevRef = useRef<Map<string, { bytes: number; at: number; rate: number }>>(new Map());

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
        }
        prevRef.current = next;
        setReads(payload.reads ?? []);
      } catch {
        /* ignore */
      }
    },
    { enabled: !paused },
  );

  return (
    <section className="card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm xl:h-full">
      <div className="card-body gap-3 p-4">
        <div className="flex items-center gap-2.5">
          <span className={styles.livePulse} aria-hidden="true" />
          <h3 className="card-title m-0 text-base">Right now</h3>
          {reads.length > 0 && (
            <span className="badge badge-ghost badge-sm ml-auto font-mono tabular-nums">
              {reads.length} active
            </span>
          )}
        </div>

        {reads.length === 0 ? (
          <p className="m-0 text-sm text-base-content/50">No active reads.</p>
        ) : (
          <div className="flex flex-col gap-2.5">
            {reads.map((r) => {
              const meta = prevRef.current.get(r.id);
              const rate = meta?.rate ?? 0;
              // Use the latest read position (what the player is requesting
              // right now) — not cumulative bytes transferred — so the bar
              // reflects actual playback location, immune to seeks/replays.
              const pct =
                r.fileSize && r.fileSize > 0
                  ? Math.min(100, (r.currentOffset / r.fileSize) * 100)
                  : null;
              return (
                <div
                  key={r.id}
                  className="flex min-w-0 w-full flex-col gap-2 rounded-box border border-base-content/10 bg-base-200 p-3"
                >
                  <div className="truncate text-sm font-medium text-base-content" title={r.path}>
                    {r.fileName || lastSegment(r.path)}
                  </div>
                  <div
                    className="truncate text-xs text-base-content/50"
                    title={clientIdentityTooltip(r.clientUserAgent, r.clientIp)}
                  >
                    {clientLabelFromUserAgent(r.clientUserAgent)}
                    {r.clientIp ? (
                      <span className="font-mono text-base-content/40"> · {r.clientIp}</span>
                    ) : null}
                  </div>
                  <button
                    type="button"
                    className="btn btn-ghost btn-xs self-start px-0 font-mono text-base-content/50 hover:underline"
                    title={`Copy session id: ${r.id}`}
                    onClick={() => void navigator.clipboard.writeText(r.id)}
                  >
                    {shortSessionId(r.id)}
                  </button>
                  {pct !== null ? (
                    <progress
                      className="progress progress-success h-1 w-full"
                      value={pct}
                      max={100}
                    />
                  ) : (
                    <div className={styles.progressIndeterminateWrap}>
                      <div className={styles.progressIndeterminate} />
                    </div>
                  )}
                  <div className="flex items-baseline justify-between font-mono text-xs tabular-nums">
                    <span className="font-medium text-base-content">
                      {r.fileSize ? (
                        <>
                          at {formatBytes(r.currentOffset)}{" "}
                          <span className="font-normal text-base-content/50">
                            / {formatBytes(r.fileSize)}
                          </span>
                        </>
                      ) : (
                        <>at {formatBytes(r.currentOffset)}</>
                      )}
                    </span>
                    <span className="font-medium text-base-content">{formatBytes(rate)}/s</span>
                  </div>
                  {r.providers.length > 0 && (
                    <div className="flex min-w-0 flex-wrap gap-1">
                      {r.providers.slice(0, 6).map((p, i) => {
                        const label = p.nickname?.trim() || p.host;
                        return (
                          <span
                            key={`${p.host}-${i}`}
                            className={`badge badge-sm gap-1.5 font-mono tabular-nums ${i === 0 ? "badge-primary" : "badge-ghost"}`}
                            title={`${label} (${p.host}): ${p.segments} segments`}
                          >
                            <span className="max-w-[8rem] truncate">{label}</span>
                            <span className="font-medium">{p.segments}</span>
                          </span>
                        );
                      })}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </section>
  );
}

function lastSegment(path: string): string {
  const idx = path.lastIndexOf("/");
  return idx >= 0 ? path.slice(idx + 1) : path;
}

function shortSessionId(id: string): string {
  return id.length > 8 ? id.slice(0, 8) : id;
}
