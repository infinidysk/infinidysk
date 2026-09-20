import { Link } from "react-router";
import { Icon } from "~/components/ui";
import type { UpdateAvailable } from "~/utils/update-check";
import { settingsPath } from "~/navigation/settings-tabs";

export function VersionMenu({
  version,
  updateAvailable,
}: {
  version?: string | undefined;
  updateAvailable?: UpdateAvailable | null | undefined;
}) {
  const displayVersion = version || "unknown";

  return (
    <Link
      to={settingsPath("support")}
      aria-label={`InfiniDysk ${displayVersion}. Support and about${updateAvailable ? ". Update available" : ""}`}
      className={`flex min-h-12 min-w-0 items-center gap-3 rounded-lg border px-3 py-2 hover:bg-base-content/5 focus-visible:outline-2 focus-visible:outline-primary ${updateAvailable ? "border-warning" : "border-base-content/20"}`}
    >
      <Icon name="hard_drive" className="shrink-0 !text-[20px] text-base-content/70" />
      <span className="min-w-0 flex-1">
        <span className="block text-xs font-semibold">InfiniDysk</span>
        <span
          className="block truncate font-mono text-[10px] text-base-content/70"
          title={displayVersion}
        >
          {displayVersion}
        </span>
      </span>
      {updateAvailable && (
        <Icon name="arrow_circle_up" className="shrink-0 !text-[18px] text-warning" />
      )}
    </Link>
  );
}
