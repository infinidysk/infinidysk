import { useEffect, useState } from "react";
import { Alert, Button, Icon } from "~/components/ui";

const WHATS_NEW_URL = "https://www.infinidysk.com/v2/whats-new/";
export const V2_BETA_NOTICE_KEY = "v2-beta-notice-dismissed";

/** Dismissible notice that 2.0 is in beta. Kept apart from the update check: 2.x needs a migration. */
export function V2BetaNotice() {
  // Hidden until mounted: localStorage is browser-only, and dismissed users must not see a flash.
  const [dismissed, setDismissed] = useState(true);

  useEffect(() => {
    try {
      setDismissed(globalThis.localStorage?.getItem(V2_BETA_NOTICE_KEY) === "true");
    } catch {
      setDismissed(false);
    }
  }, []);

  if (dismissed) return null;

  const dismiss = () => {
    setDismissed(true);
    try {
      globalThis.localStorage?.setItem(V2_BETA_NOTICE_KEY, "true");
    } catch {
      /* ignore quota / private mode */
    }
  };

  return (
    <Alert
      role="status"
      variant="info"
      className="alert-soft mb-4 grid-cols-[auto_minmax(0,1fr)_auto] items-start gap-3 border border-info/30 text-sm shadow-sm"
    >
      <Icon name="rocket_launch" className="mt-0.5 shrink-0 text-info !text-[22px]" />
      <div className="min-w-0">
        <p className="font-semibold">infinidysk 2.0 is in beta</p>
        <p className="mt-0.5 text-base-content/80">
          2.0 is a rewrite in Rust with a built-in mount. This install stays on 1.x and keeps
          getting bug fixes; moving to 2.0 takes a few manual steps.{" "}
          <a href={WHATS_NEW_URL} target="_blank" rel="noreferrer" className="link font-semibold">
            Read about 2.0
          </a>
        </p>
      </div>
      <Button
        variant="ghost"
        aria-label="Dismiss"
        className="btn-square -my-1 -mr-1 text-current max-sm:min-w-11"
        onClick={dismiss}
      >
        <Icon name="close" />
      </Button>
    </Alert>
  );
}
