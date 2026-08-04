import { useEffect, useState } from "react";
import { Alert, Icon } from "~/components/ui";

const DISMISSED_KEY = "infinidysk-rename-announcement-v1";
const RENAME_FAQ_URL = "https://nzbdav.com/community/renaming-to-infinidysk/";

export function RenameAnnouncementBanner() {
  const [isVisible, setIsVisible] = useState(false);

  useEffect(() => {
    setIsVisible(globalThis.localStorage?.getItem(DISMISSED_KEY) !== "dismissed");
  }, []);

  if (!isVisible) return null;

  function dismiss() {
    globalThis.localStorage?.setItem(DISMISSED_KEY, "dismissed");
    setIsVisible(false);
  }

  return (
    <Alert
      variant="info"
      className="mb-4 grid-cols-[auto_minmax(0,1fr)_auto] items-start gap-3 border border-info-content/15 text-sm shadow-sm"
    >
      <Icon name="new_releases" className="mt-0.5 shrink-0 !text-[22px]" />
      <div className="min-w-0">
        <p className="font-semibold">NzbDAV is becoming InfiniDysk</p>
        <p className="mt-0.5 text-info-content/80">
          The new name and look are here first. The repository and Docker image will move later;
          no action is needed yet.{" "}
          <a
            href={RENAME_FAQ_URL}
            target="_blank"
            rel="noreferrer"
            className="link font-semibold"
          >
            Read the rename FAQ
          </a>
        </p>
      </div>
      <button
        type="button"
        className="btn btn-ghost btn-square btn-sm -mr-2 -mt-1 text-info-content"
        aria-label="Dismiss rename announcement"
        onClick={dismiss}
      >
        <Icon name="close" className="!text-[20px]" />
      </button>
    </Alert>
  );
}
