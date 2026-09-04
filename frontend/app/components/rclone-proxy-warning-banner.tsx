import { Alert, Icon } from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";
import { useEffect, useState } from "react";

export const RCLONE_PROXY_STATUS_POLL_MS = 60_000;

export function RcloneProxyWarningBanner({ active: initiallyActive }: { active: boolean }) {
  const [active, setActive] = useState(initiallyActive);

  useEffect(() => {
    setActive(initiallyActive);
  }, [initiallyActive]);

  useEffect(() => {
    let stopped = false;
    const refresh = async () => {
      if (document.visibilityState === "hidden") return;
      try {
        const response = await fetch(withUrlBase("/rclone-proxy-warning"), {
          headers: { Accept: "application/json" },
        });
        if (!response.ok) return;
        const status = (await response.json()) as { active?: unknown };
        if (!stopped && typeof status.active === "boolean") setActive(status.active);
      } catch {
        // Keep the last known state while the frontend route is unavailable.
      }
    };

    const interval = window.setInterval(() => void refresh(), RCLONE_PROXY_STATUS_POLL_MS);
    return () => {
      stopped = true;
      window.clearInterval(interval);
    };
  }, []);

  if (!active) return null;

  return (
    <Alert
      variant="warning"
      className="mb-4 grid-cols-[auto_minmax(0,1fr)] items-start gap-3 border border-warning-content/15 text-sm shadow-sm"
    >
      <Icon name="lan" className="mt-0.5 shrink-0 !text-[22px]" />
      <div className="min-w-0">
        <p className="font-semibold">rclone is using the frontend proxy</p>
        <p className="mt-0.5 text-warning-content/80">
          Point the rclone WebDAV remote directly to backend port <code>8080</code> on the trusted
          Docker network. Port <code>3000</code> proxies every streamed byte through Node and can
          limit throughput. Do not publish backend port <code>8080</code> to untrusted networks.
        </p>
      </div>
    </Alert>
  );
}
