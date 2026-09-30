import { useEffect, useState, type Dispatch, type SetStateAction } from "react";
import {
  Alert,
  Button,
  Input,
  ManagedSetting,
  Select,
  SettingsCard,
  SettingsIntro,
  SettingsPage,
  Toggle,
} from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";
import { plexRequest, type PlexServer } from "../plex/plex-api";

type Props = {
  savedConfig: Record<string, string>;
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};
type PlexStatus = {
  ready: boolean;
  syncedAt: string | null;
  entryCount: number;
  warning: string | null;
  syncing: boolean;
};
const KEYS = [
  "media.library-enabled",
  "media.library-video-only",
  "media.library-dir",
  "media.library-scan-dirs",
  "media.library-scan-interval-minutes",
  "media.library-plex-server-ids",
];
const INTERVALS = [5, 15, 30, 60, 360];

export function parseScanDirectories(value: string): string[] {
  try {
    const paths: unknown = JSON.parse(value);
    return Array.isArray(paths)
      ? paths.filter((path): path is string => typeof path === "string")
      : [];
  } catch {
    return [];
  }
}

export function selectedPlexServerIds(value: string, servers: PlexServer[]): Set<string> {
  if (!value.trim())
    return new Set(servers.filter((server) => server.enabled).map((server) => server.id));
  try {
    const ids: unknown = JSON.parse(value);
    return new Set(
      Array.isArray(ids) ? ids.filter((id): id is string => typeof id === "string") : [],
    );
  } catch {
    return new Set();
  }
}

export function LibrarySettings({ config, savedConfig, setNewConfig }: Props) {
  const enabled = config["media.library-enabled"] !== "false";
  const savedEnabled = savedConfig["media.library-enabled"];
  const savedSourceIds = savedConfig["media.library-plex-server-ids"];
  const [servers, setServers] = useState<PlexServer[]>([]);
  const [status, setStatus] = useState<PlexStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [directoryDraft, setDirectoryDraft] = useState("");
  const [directoryError, setDirectoryError] = useState<string | null>(null);
  useEffect(() => {
    let active = true;
    void Promise.all([
      plexRequest<{ servers: PlexServer[] }>("servers"),
      plexRequest<PlexStatus>("library/status"),
    ])
      .then(([list, result]) => {
        if (active) {
          setServers(list.servers);
          setStatus(result);
        }
      })
      .catch((cause: unknown) => {
        if (active)
          setError(cause instanceof Error ? cause.message : "Could not load Plex status.");
      });
    return () => {
      active = false;
    };
  }, [savedEnabled, savedSourceIds]);
  useEffect(() => {
    if (!status?.syncing) return;
    let active = true;
    const timer = setInterval(() => {
      void plexRequest<PlexStatus>("library/status")
        .then((result) => {
          if (active) setStatus(result);
        })
        .catch((cause: unknown) => {
          if (active)
            setError(cause instanceof Error ? cause.message : "Could not refresh Plex status.");
        });
    }, 1500);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, [status?.syncing]);
  const enabledServers = servers.filter((server) => server.enabled);
  const selectedIds = selectedPlexServerIds(
    config["media.library-plex-server-ids"] ?? "",
    enabledServers,
  );
  const scanDirectories = parseScanDirectories(config["media.library-scan-dirs"] ?? "[]");
  const addScanDirectory = () => {
    const path = directoryDraft.trim().replace(/\/+$/, "");
    if (
      !path.startsWith("/") ||
      path === "" ||
      path === config["media.library-dir"]?.replace(/\/+$/, "")
    ) {
      setDirectoryError("Enter an absolute path different from the primary library directory.");
      return;
    }
    if (scanDirectories.includes(path)) {
      setDirectoryError("This directory is already in the scan list.");
      return;
    }
    setNewConfig((current) => ({
      ...current,
      "media.library-scan-dirs": JSON.stringify([...scanDirectories, path]),
    }));
    setDirectoryDraft("");
    setDirectoryError(null);
  };
  const removeScanDirectory = (path: string) =>
    setNewConfig((current) => ({
      ...current,
      "media.library-scan-dirs": JSON.stringify(scanDirectories.filter((entry) => entry !== path)),
    }));
  const sourcesHaveUnsavedChanges =
    config["media.library-plex-server-ids"] !== savedConfig["media.library-plex-server-ids"];
  const changeSource = (id: string, checked: boolean) => {
    const next = new Set(selectedIds);
    if (checked) next.add(id);
    else next.delete(id);
    setNewConfig((current) => ({
      ...current,
      "media.library-plex-server-ids": JSON.stringify([...next]),
    }));
  };
  const syncNow = async () => {
    setError(null);
    try {
      setStatus(await plexRequest<PlexStatus>("library/sync"));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Could not start Plex sync.");
    }
  };
  return (
    <SettingsPage>
      <SettingsIntro>
        Choose the directories to scan and the Plex sources used to organize your catalog.
      </SettingsIntro>
      <SettingsCard
        icon="video_library"
        title="Media Library"
        description="Control the catalog and its background indexing."
      >
        <ManagedSetting configKey="media.library-enabled">
          <Toggle
            id="media-library-enabled"
            checked={enabled}
            className="cursor-pointer gap-2 p-0"
            onChange={(event) =>
              setNewConfig((current) => ({
                ...current,
                "media.library-enabled": String(event.target.checked),
              }))
            }
            label={<span className="text-sm font-medium">Enable Media Library</span>}
          />
        </ManagedSetting>
        <p className="text-xs text-base-content/60">
          When off, the main menu item disappears and catalog scans and Plex matching refresh pause.
          Existing catalog data, media files, and Plex connections remain in place.
        </p>
      </SettingsCard>
      <fieldset
        disabled={!enabled}
        className={!enabled ? "flex flex-col gap-8 opacity-50" : "flex flex-col gap-8"}
      >
        <SettingsCard
          icon="folder"
          title="Library location"
          description="Where InfiniDysk finds imported media links."
        >
          <ManagedSetting configKey="media.library-dir">
            <div className="space-y-2">
              <label className="block text-sm font-medium" htmlFor="library-dir-input">
                Library Directory
              </label>
              <Input
                id="library-dir-input"
                className="w-full"
                value={config["media.library-dir"] ?? ""}
                onChange={(event) =>
                  setNewConfig((current) => ({
                    ...current,
                    "media.library-dir": event.target.value,
                  }))
                }
              />
              <p className="text-xs text-base-content/60">
                Primary path inside the InfiniDysk container. It also remains the destination for
                links created by InfiniDysk. Changing it does not move files.
              </p>
            </div>
          </ManagedSetting>
          <ManagedSetting configKey="media.library-scan-dirs">
            <div className="space-y-3">
              <label className="block text-sm font-medium" htmlFor="library-scan-dir-input">
                Additional scan directories
              </label>
              {scanDirectories.map((path) => (
                <div key={path} className="flex items-center gap-2">
                  <span className="min-w-0 flex-1 break-all rounded-lg bg-base-200/40 p-2 text-sm">
                    {path}
                  </span>
                  <Button
                    type="button"
                    size="small"
                    variant="outline"
                    onClick={() => removeScanDirectory(path)}
                  >
                    Remove
                  </Button>
                </div>
              ))}
              <div className="flex flex-wrap gap-2">
                <Input
                  id="library-scan-dir-input"
                  className="min-w-56 flex-1"
                  placeholder="/mnt/special2"
                  value={directoryDraft}
                  onChange={(event) => {
                    setDirectoryDraft(event.target.value);
                    setDirectoryError(null);
                  }}
                />
                <Button type="button" size="small" variant="outline" onClick={addScanDirectory}>
                  Add directory
                </Button>
              </div>
              {directoryError && <p className="text-xs text-error">{directoryError}</p>}
              <p className="text-xs text-base-content/60">
                Add mounted paths inside the container. These roots are scanned for symlinks and
                STRM files only; InfiniDysk does not create links there. Save before scanning.
              </p>
            </div>
          </ManagedSetting>
        </SettingsCard>
        <SettingsCard
          icon="movie"
          title="Video files only"
          description="Limit the Media Library catalog to supported video formats."
        >
          <ManagedSetting configKey="media.library-video-only">
            <Toggle
              id="media-library-video-only"
              checked={config["media.library-video-only"] === "true"}
              className="cursor-pointer gap-2 p-0"
              onChange={(event) =>
                setNewConfig((current) => ({
                  ...current,
                  "media.library-video-only": String(event.target.checked),
                }))
              }
              label={<span className="text-sm font-medium">Index video files only</span>}
            />
          </ManagedSetting>
          <p className="text-xs text-base-content/60">
            Applies to new library links and catalog results after the next scan. Subtitles, images,
            and other files remain on disk. Turn this off and scan again to show them.
          </p>
        </SettingsCard>
        <SettingsCard
          icon="schedule"
          title="Library scans"
          description="Discover added, changed, or removed symlinks and STRM files."
        >
          <ManagedSetting configKey="media.library-scan-interval-minutes">
            <div className="space-y-2">
              <label className="block text-sm font-medium" htmlFor="library-scan-interval">
                Scan every
              </label>
              <Select
                id="library-scan-interval"
                className="w-full max-w-xs"
                value={config["media.library-scan-interval-minutes"] || "15"}
                onChange={(event) =>
                  setNewConfig((current) => ({
                    ...current,
                    "media.library-scan-interval-minutes": event.target.value,
                  }))
                }
              >
                {INTERVALS.map((minutes) => (
                  <option key={minutes} value={minutes}>
                    {minutes === 15
                      ? "15 minutes · recommended"
                      : minutes === 60
                        ? "1 hour"
                        : minutes === 360
                          ? "6 hours"
                          : `${minutes} minutes`}
                  </option>
                ))}
              </Select>
              <p className="text-xs text-base-content/60">
                A new scan waits for the previous one to finish. Plex metadata refresh is separate
                and runs every six hours.
              </p>
            </div>
          </ManagedSetting>
        </SettingsCard>
        <SettingsCard
          icon="dns"
          title="Plex matching"
          description="Choose the enabled Plex servers used to classify TV, Movies, and Unmatched."
        >
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-sm text-base-content/70">Connected sources</p>
            <a
              className="btn btn-outline btn-sm"
              href={withUrlBase("/settings?tab=streaming#plex-connections")}
            >
              Manage Plex connections
            </a>
          </div>
          <ManagedSetting configKey="media.library-plex-server-ids">
            {enabledServers.length ? (
              <div className="space-y-2">
                {enabledServers.map((server) => (
                  <label
                    key={server.id}
                    className="flex items-center justify-between rounded-lg border border-base-content/10 bg-base-200/30 p-3 text-sm"
                  >
                    <span>{server.name || server.id}</span>
                    <input
                      type="checkbox"
                      className="checkbox checkbox-sm"
                      checked={selectedIds.has(server.id)}
                      onChange={(event) => changeSource(server.id, event.target.checked)}
                    />
                  </label>
                ))}
              </div>
            ) : (
              <p className="text-xs text-base-content/60">
                No enabled Plex servers. Configure one in Streaming settings.
              </p>
            )}
          </ManagedSetting>
          <p className="text-xs text-base-content/60">
            The catalog stays unclassified until its first successful Plex sync. A failed sync keeps
            the previous complete index.
          </p>
        </SettingsCard>
        <SettingsCard
          icon="sync"
          title="Catalog sync"
          description="Refresh Plex metadata after its library changes."
        >
          <div className="grid gap-3 sm:grid-cols-3">
            <div className="rounded-lg bg-base-200/40 p-3">
              <p className="text-lg font-semibold">{status?.entryCount.toLocaleString() ?? "—"}</p>
              <p className="text-xs text-base-content/55">Plex items indexed</p>
            </div>
            <div className="rounded-lg bg-base-200/40 p-3">
              <p className="text-sm font-semibold">
                {status?.syncedAt ? new Date(status.syncedAt).toLocaleString() : "Never"}
              </p>
              <p className="text-xs text-base-content/55">Last successful sync</p>
            </div>
            <div className="rounded-lg bg-base-200/40 p-3">
              <p className="text-sm font-semibold">
                {status?.syncing ? "Syncing" : status?.ready ? "Ready" : "Pending"}
              </p>
              <p className="text-xs text-base-content/55">Matching status</p>
            </div>
          </div>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-xs text-base-content/60">Automatic refresh: every 6 hours</p>
            <Button
              size="small"
              variant="outline"
              disabled={status?.syncing || !selectedIds.size || sourcesHaveUnsavedChanges}
              onClick={() => void syncNow()}
            >
              Sync Plex now
            </Button>
          </div>
          {sourcesHaveUnsavedChanges && (
            <p className="text-xs text-base-content/60">
              Save your source selection before syncing Plex.
            </p>
          )}
          {status?.warning && (
            <Alert variant="warning" className="text-xs">
              {status.warning}
            </Alert>
          )}
          {error && (
            <Alert variant="danger" className="text-xs">
              {error}
            </Alert>
          )}
          <p className="text-xs text-base-content/60">
            This refreshes metadata only. It does not alter media files or symlinks.
          </p>
        </SettingsCard>
      </fieldset>
    </SettingsPage>
  );
}

export function isLibrarySettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return KEYS.some((key) => config[key] !== newConfig[key]);
}
