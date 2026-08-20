import { Alert, Icon } from "~/components/ui";

const RENAME_FAQ_URL = "https://www.infinidysk.com/community/renaming-to-infinidysk/";

/**
 * Persistent (non-dismissible) notice for installs still pulling the
 * deprecated ghcr.io/nzbdav/nzbdav image path. Those images are one-layer
 * derivatives that bake in NZBDAV_LEGACY_IMAGE=true; the root loader
 * forwards that env flag here. Installs on the new path never see this.
 */
export function LegacyImageBanner({ isLegacyImage }: { isLegacyImage: boolean }) {
  if (!isLegacyImage) return null;

  return (
    <Alert
      variant="warning"
      className="mb-4 grid-cols-[auto_minmax(0,1fr)] items-start gap-3 border border-warning-content/15 text-sm shadow-sm"
    >
      <Icon name="warning" className="mt-0.5 shrink-0 !text-[22px]" />
      <div className="min-w-0">
        <p className="font-semibold">This image path is deprecated</p>
        <p className="mt-0.5 text-warning-content/80">
          You are running InfiniDysk from the old <code>ghcr.io/nzbdav/nzbdav</code> image. Switch
          to <code>ghcr.io/infinidysk/infinidysk</code> — same <code>/config</code>, same tags, no
          other changes. Updates on the old path will stop after the transition period.{" "}
          <a href={RENAME_FAQ_URL} target="_blank" rel="noreferrer" className="link font-semibold">
            Read the rename FAQ
          </a>
        </p>
      </div>
    </Alert>
  );
}
