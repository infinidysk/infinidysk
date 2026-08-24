import { useEffect, useRef, useState } from "react";
import type { ActiveRead, ActiveReadsMessage } from "~/clients/backend-client.server";
import { formatBytes } from "../../utils/format";
import { clientIdentityTooltip, clientLabelFromUserAgent } from "~/utils/client-label";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { Tooltip } from "~/components/ui";

const TOPIC_ACTIVE_READS = "ar";

/**
 * Live "right now" panel — reads cards refreshed via the ActiveReads WS topic.
 * Keeps an empty state when no reads are active so the dashboard rail is stable.
 * When `paused`, the subscription is disabled so layout edit borders stay stable.
 */
export function LiveReadsPanel({ paused = false }: { paused?: boolean }) {
  const [reads, setReads] = useState<ActiveRead[]>([]);
  const [copiedId, setCopiedId] = useState<string | null>(null);
  const [copyNotice, setCopyNotice] = useState<{ seq: number; text: string } | null>(null);
  const copySeqRef = useRef(0);
  const copyTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Track previous bytesRead per session for live MiB/s computation.
  const prevRef = useRef<Map<string, { bytes: number; at: number; rate: number }>>(new Map());

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
          <span className="status status-success animate-pulse" aria-hidden="true" />
          <h3 className="card-title m-0 text-base">Right now</h3>
          {reads.length > 0 && (
            <span className="badge badge-ghost badge-sm ml-auto font-mono tabular-nums">
              {reads.length} active
            </span>
          )}
        </div>

        <div key={copyNotice?.seq ?? 0} className="sr-only" aria-live="polite">
          {copyNotice?.text ?? ""}
        </div>

        {reads.length === 0 ? (
          <p className="m-0 text-sm text-base-content/50">
            No files are being read right now. Open a mounted file to see live progress here.
          </p>
        ) : (
          <ul className="list w-full p-0">
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
                <li key={r.id} className="list-row px-0">
                  <div className="list-col-grow min-w-0 gap-2">
                    <Tooltip content={r.path}>
                      <div className="truncate text-sm font-medium text-base-content">
                        {r.fileName || lastSegment(r.path)}
                      </div>
                    </Tooltip>
                    <Tooltip content={clientIdentityTooltip(r.clientUserAgent, r.clientIp) ?? ""}>
                      <div className="truncate text-xs text-base-content/50">
                        {clientLabelFromUserAgent(r.clientUserAgent)}
                        {r.clientIp ? (
                          <span className="font-mono text-base-content/40"> · {r.clientIp}</span>
                        ) : null}
                      </div>
                    </Tooltip>
                    <Tooltip content={`Copy session id: ${r.id}`}>
                      <button
                        type="button"
                        className="btn btn-link btn-xs h-auto min-h-0 px-0 font-mono"
                        onClick={() => {
                          void copySessionId(r.id);
                        }}
                      >
                        {copiedId === r.id ? "Copied" : shortSessionId(r.id)}
                      </button>
                    </Tooltip>
                    {pct !== null ? (
                      <progress
                        className="progress progress-success h-1 w-full"
                        value={pct}
                        max={100}
                      />
                    ) : (
                      <span className="loading loading-bars loading-sm text-success" />
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
                      </div>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
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
