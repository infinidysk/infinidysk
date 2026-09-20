import { useEffect, useRef, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { Icon, Tooltip } from "~/components/ui";

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
  const nearCapacity = hasUsenetProviders && !!connections && max > 0 && live >= max * 0.9;
  const description = !hasUsenetProviders
    ? "Usenet connections: no providers configured."
    : showConnecting
      ? "Connecting to Usenet connection updates."
      : `${live} open connections out of ${max} allowed. ${active} active; ${idle} warm and ready for playback.${showReconnecting ? " Reconnecting; counts may be stale." : ""}`;

  return (
    <>
      <span className="sr-only" role="status" aria-live="polite">
        {description}
      </span>
      <Tooltip content={description} placement="bottom" className="hidden shrink-0 sm:inline-block">
        <div
          role="group"
          tabIndex={0}
          aria-label="Usenet connections"
          className="flex h-10 w-40 items-center gap-2 rounded-box px-3 text-left focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        >
          <Icon
            name="sync_alt"
            className={`shrink-0 !text-[20px] ${showReconnecting || nearCapacity ? "text-warning" : "text-base-content/60"}`}
          />
          <div className="min-w-0 flex-1 tabular-nums">
            <div className="flex h-4 items-center font-mono text-xs leading-none text-base-content/80">
              {!hasUsenetProviders && "—"}
              {hasUsenetProviders && connections && `${live} / ${max}`}
              {showConnecting && <span className="loading loading-spinner loading-xs" />}
            </div>
            <div
              className={`mt-0.5 whitespace-nowrap text-[10px] leading-3 ${showReconnecting ? "text-warning" : "text-base-content/70"}`}
            >
              {!hasUsenetProviders && "No providers"}
              {hasUsenetProviders &&
                connections &&
                !transportDown &&
                `${active} active · ${idle} warm`}
              {showReconnecting && "Reconnecting"}
              {showConnecting && "Connecting"}
            </div>
          </div>
        </div>
      </Tooltip>
    </>
  );
}
