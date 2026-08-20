import { useEffect, useRef, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";

type LiveUsenetConnectionsProps = {
  hasUsenetProviders: boolean;
};

/** Keep the last known count visible briefly across websocket reconnect flaps. */
const RECONNECT_GRACE_MS = 8_000;

export function LiveUsenetConnections({ hasUsenetProviders }: LiveUsenetConnectionsProps) {
  const [connections, setConnections] = useState<string | null>(null);
  const [transportDown, setTransportDown] = useState(false);
  const graceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const parts = (connections || "0|0|0|0|1|0").split("|");
  const live = Number(parts[3]);
  const max = Number(parts[4]);
  const idle = Number(parts[5]);
  const active = live - idle;

  useWebsocketTopic(
    "cxs",
    "state",
    (message) => {
      if (graceTimerRef.current) {
        clearTimeout(graceTimerRef.current);
        graceTimerRef.current = null;
      }
      setTransportDown(false);
      setConnections(message);
    },
    {
      enabled: hasUsenetProviders,
      onOpen: () => {
        if (graceTimerRef.current) {
          clearTimeout(graceTimerRef.current);
          graceTimerRef.current = null;
        }
        setTransportDown(false);
      },
      onClose: () => {
        setTransportDown(true);
        if (graceTimerRef.current) clearTimeout(graceTimerRef.current);
        // Keep last value during brief reconnects; only clear after grace.
        graceTimerRef.current = setTimeout(() => {
          setConnections(null);
          graceTimerRef.current = null;
        }, RECONNECT_GRACE_MS);
      },
    },
  );

  useEffect(() => {
    if (!hasUsenetProviders) {
      if (graceTimerRef.current) {
        clearTimeout(graceTimerRef.current);
        graceTimerRef.current = null;
      }
      setConnections(null);
      setTransportDown(false);
    }
  }, [hasUsenetProviders]);

  useEffect(
    () => () => {
      if (graceTimerRef.current) clearTimeout(graceTimerRef.current);
    },
    [],
  );

  const showConnecting = hasUsenetProviders && !connections;
  const showReconnecting = hasUsenetProviders && !!connections && transportDown;

  return (
    <div
      className="stats hidden h-10 overflow-hidden border border-base-content/10 bg-base-200 sm:inline-grid"
      aria-label="Usenet connections"
    >
      <div className="stat flex items-center gap-3 px-3 py-1">
        <div className="stat-title text-[10px] font-semibold leading-none uppercase tracking-wide text-base-content/50">
          Connections
        </div>
        <span className="h-4 w-px bg-base-content/15" aria-hidden="true" />
        <div className="stat-value font-mono text-sm leading-tight text-base-content/80">
          {!hasUsenetProviders && "—"}
          {hasUsenetProviders && connections && `${live}/${max}`}
          {showConnecting && <span className="loading loading-spinner loading-xs" />}
        </div>
        <div className="stat-desc text-[10px] leading-none whitespace-nowrap text-base-content/50">
          {!hasUsenetProviders && "No providers"}
          {hasUsenetProviders && connections && !transportDown && `${active} active`}
          {showReconnecting && "Reconnecting"}
          {showConnecting && "Connecting"}
        </div>
      </div>
    </div>
  );
}
