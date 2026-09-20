import { useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import type { LiveStatsMessage } from "~/clients/backend-client.server";
import { Icon, Tooltip } from "~/components/ui";
import { useWebsocketTopic } from "~/utils/shared-websocket";

export function LiveReadCount() {
  const [count, setCount] = useState<number | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const expiry = useRef<ReturnType<typeof setTimeout> | null>(null);
  const markUnavailable = () => {
    setCount(null);
    setUnavailable(true);
  };

  useEffect(() => {
    expiry.current = setTimeout(markUnavailable, 15_000);
    return () => {
      if (expiry.current) clearTimeout(expiry.current);
    };
  }, []);

  useWebsocketTopic(
    "ls",
    "state",
    (message) => {
      try {
        const data = JSON.parse(message) as LiveStatsMessage;
        if (
          !Number.isInteger(data.activeReads) ||
          data.activeReads < 0 ||
          !Number.isFinite(data.ts) ||
          Date.now() - data.ts > 15_000
        ) {
          markUnavailable();
          return;
        }
        if (expiry.current) clearTimeout(expiry.current);
        setCount(data.activeReads);
        setUnavailable(false);
        expiry.current = setTimeout(markUnavailable, 15_000);
      } catch {
        markUnavailable();
      }
    },
    { onClose: markUnavailable },
  );

  const label =
    count == null
      ? unavailable
        ? "Active file reads unavailable"
        : "Checking active file reads"
      : `${count.toLocaleString()} active file ${count === 1 ? "read" : "reads"}`;

  return (
    <>
      <span className="sr-only" role="status" aria-live="polite">
        {label}
      </span>
      <Tooltip content={label} placement="bottom" className="hidden shrink-0 lg:inline-block">
        <Link
          to="/overview#active-reads"
          aria-label={`${label}. View active-read details`}
          className={`btn btn-ghost h-10 min-h-10 w-24 gap-2 px-2 text-xs font-normal ${unavailable ? "text-warning" : count ? "text-base-content" : "text-base-content/60"}`}
        >
          <Icon name="readiness_score" className="shrink-0 !text-[18px]" />
          <span className="min-w-0 truncate tabular-nums">
            {count == null
              ? "-- reads"
              : `${count.toLocaleString()} ${count === 1 ? "read" : "reads"}`}
          </span>
        </Link>
      </Tooltip>
    </>
  );
}
