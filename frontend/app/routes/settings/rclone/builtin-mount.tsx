import { type Dispatch, type SetStateAction, useCallback, useEffect, useState } from "react";
import { Button } from "~/components/ui/button";
import { Alert, Badge, Spinner } from "~/components/ui/feedback";
import { ManagedSetting, SettingsCard } from "~/components/ui";
import { Input, Select, Textarea, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { withUrlBase } from "~/utils/url-base";
import {
  coversSymlinkRoot,
  DEFAULT_CACHE_AGE_HOURS,
  DEFAULT_DIR_CACHE_HOURS,
  describeMountState,
  formatBytes,
  hoursToTimeSpan,
  mountsMatchServer,
  newMount,
  parseMounts,
  parseSize,
  removeMount,
  serializeMounts,
  timeSpanToHours,
  upsertMount,
  type BuiltinMount,
} from "./builtin-mount-model";

type MountRow = {
  id: string;
  name?: string;
  mountPoint: string;
  remotePath: string;
  vfsCacheMode: string;
  configured: boolean;
  enabled: boolean;
  mounted: boolean;
  allowOther?: boolean;
  links?: boolean;
  dirCacheTimeSeconds?: number;
  vfsCacheMaxAgeSeconds?: number;
  vfsCacheMaxSizeBytes?: number | null;
  readAheadBytes?: number | null;
};

type StatusResponse = {
  status?: boolean;
  enabled?: boolean;
  running?: boolean;
  rcloneVersion?: string | null;
  remoteConfigured?: boolean;
  symlinkMountDir?: string | null;
  cacheDir?: string | null;
  cacheDirFreeBytes?: number | null;
  cacheSizeLimitBytes?: number | null;
  blockingReasons?: string[];
  mounts?: MountRow[];
};

type ApplyResponse = {
  mounted?: string[];
  unmounted?: string[];
  errors?: string[];
};

type ImportResponse = {
  imported?: boolean;
  mounts?: MountRow[];
  warnings?: string[];
};

type BuiltinMountSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

const CACHE_MODES = [
  { value: "full", label: "Full — disk cache, best for seeking" },
  { value: "writes", label: "Writes — cache written files only" },
  { value: "minimal", label: "Minimal" },
  { value: "off", label: "Off — stream everything" },
];

// The flags a container needs before FUSE will work. Shown verbatim so it can be
// copied into a compose file without translation.
const REQUIRED_DOCKER_FLAGS = `devices:
  - /dev/fuse:/dev/fuse:rwm
cap_add:
  - SYS_ADMIN
security_opt:
  - apparmor:unconfined
volumes:
  - /mnt:/mnt:rshared`;

/** Turns a failed request into something an operator can act on. */
/**
 * Reads a JSON body, treating a non-2xx response as a failure.
 *
 * Without this a 500 was cast straight to the success shape, so an error body
 * arrived on screen as an apply that worked.
 */
async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = (await response.json().catch(() => ({}))) as { error?: string };
    throw new Error(body.error || `Request failed (${response.status})`);
  }
  return (await response.json()) as T;
}

function describeFailure(action: string, error: unknown): string {
  const detail = error instanceof Error ? error.message : String(error);
  return `Could not ${action}: ${detail}. Check that InfiniDysk is still running and try again.`;
}

export function BuiltinMountSettings({ config, setNewConfig }: BuiltinMountSettingsProps) {
  const enabled = config["rclone.builtin.enabled"] === "true";

  // Stored in bytes so the backend can validate it; shown in the units the
  // operator typed them in.
  const savedCacheSizeLimit = Number(config["rclone.builtin.cache-size-limit"] ?? "");
  const cacheSizeLimitText =
    Number.isFinite(savedCacheSizeLimit) && savedCacheSizeLimit > 0
      ? formatBytes(savedCacheSizeLimit)
      : "";
  const mounts = parseMounts(config["rclone.builtin.mounts"]);

  const [status, setStatus] = useState<StatusResponse | null>(null);
  const [busy, setBusy] = useState<"idle" | "applying" | "importing" | "saving-password">("idle");
  const [applyResult, setApplyResult] = useState<ApplyResponse | null>(null);
  const [importResult, setImportResult] = useState<ImportResponse | null>(null);
  const [pastedCommand, setPastedCommand] = useState("");
  const [webdavPassword, setWebdavPassword] = useState("");
  const [credentialResult, setCredentialResult] = useState<ApplyResponse | null>(null);
  const [showLogs, setShowLogs] = useState(false);
  const [logLines, setLogLines] = useState<string[]>([]);

  const writeMounts = useCallback(
    (updated: BuiltinMount[]) => {
      setNewConfig((current) => ({
        ...current,
        "rclone.builtin.mounts": serializeMounts(updated),
      }));
    },
    [setNewConfig],
  );

  const refreshStatus = useCallback(async () => {
    try {
      const response = await fetch(withUrlBase("/api/rclone-mounts/status"));
      setStatus(await readJson<StatusResponse>(response));
    } catch {
      setStatus(null);
    }
  }, []);

  useEffect(() => {
    if (!enabled) return;
    void refreshStatus();
    const timer = setInterval(() => void refreshStatus(), 10000);
    return () => clearInterval(timer);
  }, [enabled, refreshStatus]);

  const apply = useCallback(async () => {
    setBusy("applying");
    setApplyResult(null);
    try {
      const response = await fetch(withUrlBase("/api/rclone-mounts/apply"), { method: "POST" });
      setApplyResult(await readJson<ApplyResponse>(response));
      await refreshStatus();
    } catch (error) {
      setApplyResult({ errors: [describeFailure("apply the mounts", error)] });
    } finally {
      setBusy("idle");
    }
  }, [refreshStatus]);

  const remount = useCallback(
    async (id: string) => {
      setApplyResult(null);
      try {
        const response = await fetch(
          withUrlBase(`/api/rclone-mounts/remount?id=${encodeURIComponent(id)}`),
          { method: "POST" },
        );
        setApplyResult(await readJson<ApplyResponse>(response));
        await refreshStatus();
      } catch (error) {
        setApplyResult({ errors: [describeFailure(`remount '${id}'`, error)] });
      }
    },
    [refreshStatus],
  );

  const importExternal = useCallback(async (command: string) => {
    setBusy("importing");
    setImportResult(null);
    try {
      const body = new FormData();
      if (command.trim()) body.append("command", command);
      const response = await fetch(withUrlBase("/api/rclone-mounts/import-external"), {
        method: "POST",
        body,
      });
      setImportResult(await readJson<ImportResponse>(response));
    } catch (error) {
      setImportResult({ warnings: [describeFailure("read the rclone settings", error)] });
    } finally {
      setBusy("idle");
    }
  }, []);

  const saveWebdavPassword = useCallback(async () => {
    setBusy("saving-password");
    setCredentialResult(null);
    try {
      const body = new FormData();
      body.append("password", webdavPassword);
      const response = await fetch(withUrlBase("/api/rclone-mounts/webdav-credentials"), {
        method: "POST",
        body,
      });
      const result = await readJson<ApplyResponse>(response);
      setCredentialResult(result);
      if ((result.errors ?? []).length === 0) setWebdavPassword("");
      await refreshStatus();
    } catch (error) {
      setCredentialResult({ errors: [describeFailure("save the WebDAV password", error)] });
    } finally {
      setBusy("idle");
    }
  }, [refreshStatus, webdavPassword]);

  const loadLogs = useCallback(async () => {
    try {
      const response = await fetch(withUrlBase("/api/rclone-mounts/logs"));
      const body = await readJson<{ lines?: string[] }>(response);
      setLogLines(body.lines ?? []);
    } catch (error) {
      setLogLines([describeFailure("read the rclone log", error)]);
    }
  }, []);

  const symlinkMountDir = status?.symlinkMountDir ?? undefined;
  const symlinkRootCovered = coversSymlinkRoot(mounts, symlinkMountDir);
  const externalRcloneConfigured = Boolean(config["rclone.host"]?.trim());
  const blockingReasons = status?.blockingReasons ?? [];
  const liveById = new Map((status?.mounts ?? []).map((row) => [row.id, row]));
  const unmanaged = (status?.mounts ?? []).filter((row) => !row.configured);
  // Apply reconciles what the backend has saved, not what is on screen.
  const hasUnsavedMounts = !mountsMatchServer(mounts, status?.mounts);

  return (
    <div className="flex flex-col gap-4">
      <SettingsCard
        icon="hard_drive"
        title="Built-in mount"
        description="Let InfiniDysk run rclone itself, instead of a separate rclone container."
      >
        <ManagedSetting configKey="rclone.builtin.enabled">
          <Toggle
            id="rclone-builtin-enabled"
            className="cursor-pointer gap-2 p-0"
            checked={enabled}
            onChange={(e) =>
              setNewConfig({ ...config, "rclone.builtin.enabled": "" + e.target.checked })
            }
            label={<span className="text-sm text-base-content">Run rclone inside InfiniDysk</span>}
          />
        </ManagedSetting>
        <p className="mt-2 text-[11px] leading-relaxed text-base-content/45">
          Leave this off to keep using your own rclone container. Turning it on does not change your
          external settings, so you can switch back at any time.
        </p>

        {enabled && (
          <div className="mt-4 border-t border-base-300 pt-3">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <span className="text-sm font-medium">Cache</span>
              <span className="text-[11px] text-base-content/45">
                {status?.cacheDir ?? "—"}
                {status?.cacheDirFreeBytes != null && (
                  <> · {formatBytes(status.cacheDirFreeBytes)} free</>
                )}
                {status?.cacheSizeLimitBytes != null && (
                  <> · limit {formatBytes(status.cacheSizeLimitBytes)}</>
                )}
              </span>
            </div>
            <p className="mt-1 text-[11px] leading-relaxed text-base-content/45">
              The cache size is chosen from the free space on this volume, and never grows into the
              last few GB where the databases live. Move it to a bigger or faster disk below.
            </p>
            <div className="mt-3 grid grid-cols-1 gap-3 lg:grid-cols-2">
              <ManagedSetting configKey="rclone.builtin.cache-dir">
                <label className="block space-y-1">
                  <span className="block text-sm font-medium">Cache directory</span>
                  <Input
                    className="w-full"
                    placeholder={status?.cacheDir ?? "/config/rclone/cache"}
                    value={config["rclone.builtin.cache-dir"] ?? ""}
                    onChange={(e) =>
                      setNewConfig({ ...config, "rclone.builtin.cache-dir": e.target.value })
                    }
                  />
                  <span className="block text-[11px] leading-relaxed text-base-content/45">
                    Must be an absolute path outside every mount point. Changing it restarts the
                    rclone process when you save.
                  </span>
                </label>
              </ManagedSetting>
              <ManagedSetting configKey="rclone.builtin.cache-size-limit">
                <label className="block space-y-1">
                  <span className="block text-sm font-medium">Cache size limit</span>
                  <Input
                    // Keyed on the saved value so clearing the box, or a save
                    // that rounds it, replaces what is shown.
                    key={config["rclone.builtin.cache-size-limit"] ?? "auto"}
                    className="w-full"
                    placeholder={
                      status?.cacheSizeLimitBytes != null
                        ? `${formatBytes(status.cacheSizeLimitBytes)} (automatic)`
                        : "Automatic"
                    }
                    defaultValue={cacheSizeLimitText}
                    onBlur={(e) => {
                      // Empty hands the size back to the automatic budget. A
                      // value that cannot be read is left for the operator to
                      // correct rather than silently discarded.
                      const typed = e.target.value.trim();
                      const bytes = typed === "" ? null : parseSize(typed);
                      if (typed !== "" && bytes === null) return;
                      setNewConfig({
                        ...config,
                        "rclone.builtin.cache-size-limit": bytes === null ? "" : String(bytes),
                      });
                    }}
                  />
                  <span className="block text-[11px] leading-relaxed text-base-content/45">
                    Shared by every share on this cache. Leave empty to size it from the free space
                    here. Accepts values like <code>20G</code>.
                  </span>
                </label>
              </ManagedSetting>
            </div>
          </div>
        )}
      </SettingsCard>

      {enabled && blockingReasons.length > 0 && (
        <SettingsCard
          icon="report"
          title="This container cannot mount yet"
          description="rclone is installed, but Docker has to give the container access to FUSE."
        >
          <ul className="mb-3 space-y-2">
            {blockingReasons.map((reason) => (
              <li key={reason} className="text-sm leading-relaxed text-base-content/80">
                {reason}
              </li>
            ))}
          </ul>
          <p className="mb-2 text-sm text-base-content/70">
            Add this to the InfiniDysk service in your compose file, then recreate the container:
          </p>
          <pre className="overflow-x-auto rounded-lg bg-base-300/60 p-3 text-xs leading-relaxed">
            <code>{REQUIRED_DOCKER_FLAGS}</code>
          </pre>
          <p className="mt-2 text-[11px] leading-relaxed text-base-content/45">
            The <code>rshared</code> bind is what makes the mount visible to Plex, Sonarr, and other
            containers. Without it the mount exists only inside InfiniDysk.
          </p>
        </SettingsCard>
      )}

      {enabled && status?.running && status.remoteConfigured === false && (
        <SettingsCard
          icon="key"
          title="Connect to your library"
          description="InfiniDysk needs its own WebDAV password once, to let rclone read the library."
        >
          <p className="mb-3 text-sm leading-relaxed text-base-content/70">
            This is the password under Settings, WebDAV. It is stored only in rclone&apos;s own
            configuration, and InfiniDysk never shows it again.
          </p>
          <div className="flex flex-wrap items-end gap-2">
            <label className="min-w-0 flex-1 space-y-1">
              <span className="block text-sm font-medium">WebDAV password</span>
              <Input
                type="password"
                className="w-full"
                autoComplete="off"
                value={webdavPassword}
                onChange={(e) => setWebdavPassword(e.target.value)}
              />
            </label>
            <Button
              onClick={() => void saveWebdavPassword()}
              disabled={!webdavPassword.trim() || busy === "saving-password"}
            >
              {busy === "saving-password" ? <Spinner /> : "Connect and mount"}
            </Button>
          </div>
          {credentialResult &&
            (credentialResult.errors ?? []).map((error) => (
              <Alert key={error} variant="danger" className="mt-2 py-2 text-xs">
                {error}
              </Alert>
            ))}
        </SettingsCard>
      )}

      {enabled && (
        <SettingsCard
          icon="folder_open"
          title="Mounts"
          description="Each mount exposes the WebDAV tree as a folder on this host."
          action={
            <div className="flex items-center gap-2">
              {status?.running ? (
                <Badge className="badge-success badge-sm">Daemon running</Badge>
              ) : (
                <Badge className="badge-ghost badge-sm">Daemon stopped</Badge>
              )}
              {status?.rcloneVersion && (
                <span className="text-[11px] text-base-content/45">{status.rcloneVersion}</span>
              )}
            </div>
          }
        >
          <div className="flex flex-col gap-3">
            {mounts.length === 0 && (
              <div className="space-y-2">
                <p className="text-sm text-base-content/60">
                  No mounts yet.
                  {symlinkMountDir
                    ? " Your imported files resolve through the folder below, so that is where a mount belongs."
                    : " Add one to expose the WebDAV tree as a folder."}
                </p>
                {symlinkMountDir && (
                  <Button onClick={() => writeMounts([newMount(0, symlinkMountDir)])}>
                    Add a mount at {symlinkMountDir}
                  </Button>
                )}
              </div>
            )}

            {mounts.map((mount) => {
              const live = liveById.get(mount.Id);
              const state = describeMountState({
                configured: true,
                enabled: mount.Enabled,
                mounted: live?.mounted ?? false,
              });

              return (
                <div
                  key={mount.Id}
                  className="rounded-lg border border-base-300 p-3"
                  data-testid={`mount-${mount.Id}`}
                >
                  <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                    <span className="font-medium text-base-content">{mount.Name ?? mount.Id}</span>
                    <div className="flex items-center gap-2">
                      <Badge
                        className={
                          state.variant === "success"
                            ? "badge-success badge-sm"
                            : state.variant === "warning"
                              ? "badge-warning badge-sm"
                              : "badge-ghost badge-sm"
                        }
                      >
                        {state.label}
                      </Badge>
                      <Button
                        variant="ghost"
                        className="btn-xs"
                        onClick={() => writeMounts(removeMount(mounts, mount.Id))}
                        aria-label={`Remove ${mount.Name ?? mount.Id}`}
                      >
                        <Icon name="delete" className="!text-[16px]" />
                      </Button>
                    </div>
                  </div>

                  <div className="grid grid-cols-1 gap-3 lg:grid-cols-3">
                    <label className="space-y-1">
                      <span className="block text-sm font-medium">Folder on this host</span>
                      <Input
                        className="w-full"
                        value={mount.MountPoint}
                        onChange={(e) =>
                          writeMounts(upsertMount(mounts, { ...mount, MountPoint: e.target.value }))
                        }
                      />
                    </label>
                    <label className="space-y-1">
                      <span className="block text-sm font-medium">Folder inside WebDAV</span>
                      <Input
                        className="w-full"
                        value={mount.RemotePath}
                        onChange={(e) =>
                          writeMounts(upsertMount(mounts, { ...mount, RemotePath: e.target.value }))
                        }
                      />
                    </label>
                    <label className="space-y-1">
                      <span className="block text-sm font-medium">Cache mode</span>
                      <Select
                        className="w-full"
                        value={mount.VfsCacheMode}
                        onChange={(e) =>
                          writeMounts(
                            upsertMount(mounts, { ...mount, VfsCacheMode: e.target.value }),
                          )
                        }
                      >
                        {CACHE_MODES.map((mode) => (
                          <option key={mode.value} value={mode.value}>
                            {mode.label}
                          </option>
                        ))}
                      </Select>
                    </label>
                  </div>

                  <details className="mt-3">
                    <summary className="cursor-pointer text-sm text-base-content/70">
                      Cache and tuning
                    </summary>
                    <div className="mt-3 grid grid-cols-1 gap-3 lg:grid-cols-3">
                      <label className="space-y-1">
                        <span className="block text-sm font-medium">Read ahead</span>
                        <Input
                          // Keyed on the stored value so an import or another
                          // edit replaces what is displayed; an uncontrolled
                          // defaultValue alone would keep showing stale text.
                          key={mount.ReadAheadBytes ?? "unset"}
                          className="w-full"
                          placeholder="rclone default"
                          defaultValue={
                            mount.ReadAheadBytes ? formatBytes(mount.ReadAheadBytes) : ""
                          }
                          onBlur={(e) => {
                            // The field shows a rounded size, so re-parsing it
                            // unchanged can differ from the imported byte count.
                            // Only write when the operator actually changed it.
                            const parsed = parseSize(e.target.value);
                            const current = mount.ReadAheadBytes ?? null;
                            if (parsed === current) return;
                            writeMounts(upsertMount(mounts, { ...mount, ReadAheadBytes: parsed }));
                          }}
                        />
                        <span className="block text-[11px] leading-relaxed text-base-content/45">
                          Buffered on disk in Full cache mode, so it spends the same budget as the
                          cache. Accepts values like <code>512M</code>.
                        </span>
                      </label>
                      <label className="space-y-1">
                        <span className="block text-sm font-medium">Keep cached data for</span>
                        <Input
                          type="number"
                          min={1}
                          className="w-full"
                          value={timeSpanToHours(mount.VfsCacheMaxAge, DEFAULT_CACHE_AGE_HOURS)}
                          onChange={(e) => {
                            // An empty box reads as 0, which would tell rclone to
                            // expire cached data immediately. Keep the previous
                            // value until a usable number is typed.
                            const hours = Number(e.target.value);
                            if (!Number.isFinite(hours) || hours <= 0) return;
                            writeMounts(
                              upsertMount(mounts, {
                                ...mount,
                                VfsCacheMaxAge: hoursToTimeSpan(hours),
                              }),
                            );
                          }}
                        />
                        <span className="block text-[11px] leading-relaxed text-base-content/45">
                          Hours. Cached data is also evicted when the cache reaches its size limit.
                        </span>
                      </label>
                      <label className="space-y-1">
                        <span className="block text-sm font-medium">
                          Remember folder listings for
                        </span>
                        <Input
                          type="number"
                          min={1}
                          className="w-full"
                          value={timeSpanToHours(mount.DirCacheTime, DEFAULT_DIR_CACHE_HOURS)}
                          onChange={(e) => {
                            // Same guard as the cache age: an empty box reads as
                            // 0, which would re-list every folder on every access.
                            const hours = Number(e.target.value);
                            if (!Number.isFinite(hours) || hours <= 0) return;
                            writeMounts(
                              upsertMount(mounts, {
                                ...mount,
                                DirCacheTime: hoursToTimeSpan(hours),
                              }),
                            );
                          }}
                        />
                        <span className="block text-[11px] leading-relaxed text-base-content/45">
                          Hours. InfiniDysk drops a folder from this cache as soon as it changes it,
                          so a long value stays accurate. Lower it if listings ever look stale.
                        </span>
                      </label>
                    </div>
                  </details>

                  <div className="mt-3 flex flex-wrap items-center gap-4">
                    <Toggle
                      className="cursor-pointer gap-2 p-0"
                      checked={mount.Enabled}
                      onChange={(e) =>
                        writeMounts(upsertMount(mounts, { ...mount, Enabled: e.target.checked }))
                      }
                      label={<span className="text-sm">Mount this folder</span>}
                    />
                    <Toggle
                      className="cursor-pointer gap-2 p-0"
                      checked={mount.Links}
                      onChange={(e) =>
                        writeMounts(upsertMount(mounts, { ...mount, Links: e.target.checked }))
                      }
                      label={<span className="text-sm">Follow symlinks</span>}
                    />
                    {live?.mounted && (
                      <Button
                        variant="ghost"
                        className="btn-xs"
                        onClick={() => void remount(mount.Id)}
                      >
                        Remount
                      </Button>
                    )}
                  </div>
                </div>
              );
            })}

            {mounts.length > 0 && !symlinkRootCovered && symlinkMountDir && (
              <Alert variant="warning" className="py-2 text-xs">
                <span>
                  Nothing is mounted at <code>{symlinkMountDir}</code>, which is where imported
                  files resolve through. Sonarr and Radarr imports will not play until a mount
                  covers it.
                </span>
              </Alert>
            )}

            {unmanaged.map((row) => (
              <Alert key={row.mountPoint} variant="warning" className="text-xs py-2">
                <span>
                  <code>{row.mountPoint}</code> is mounted but is no longer in this list. Applying
                  will unmount it.
                </span>
              </Alert>
            ))}

            <div className="flex flex-wrap gap-2">
              <Button
                variant="ghost"
                onClick={() => writeMounts([...mounts, newMount(mounts.length, symlinkMountDir)])}
              >
                Add a mount
              </Button>
              <Button
                onClick={() => void apply()}
                disabled={busy === "applying" || hasUnsavedMounts}
              >
                {busy === "applying" ? <Spinner /> : "Apply now"}
              </Button>
            </div>

            {hasUnsavedMounts && (
              <Alert variant="warning" className="py-2 text-xs">
                <span>
                  Save your changes first. Applying now would mount the settings that are already
                  saved, not the ones on screen.
                </span>
              </Alert>
            )}

            {applyResult && (
              <div className="space-y-2">
                {(applyResult.errors ?? []).map((error) => (
                  <Alert key={error} variant="danger" className="text-xs py-2">
                    {error}
                  </Alert>
                ))}
                {(applyResult.errors ?? []).length === 0 && (
                  <Alert variant="success" className="text-xs py-2">
                    Mounted {applyResult.mounted?.length ?? 0}, unmounted{" "}
                    {applyResult.unmounted?.length ?? 0}.
                  </Alert>
                )}
              </div>
            )}
          </div>
        </SettingsCard>
      )}

      {enabled && (
        <SettingsCard
          icon="move_down"
          title="Move from your rclone container"
          description={
            externalRcloneConfigured
              ? "Copy the mount settings from the rclone you already run."
              : "Only needed if you already run rclone somewhere else."
          }
        >
          <div className="flex flex-col gap-3">
            {externalRcloneConfigured ? (
              <>
                <p className="text-sm leading-relaxed text-base-content/70">
                  InfiniDysk reads the settings your rclone is actually running with — the mount
                  path, cache mode, and tuning. The folder path is kept exactly as it is, because
                  your library&apos;s symlinks point through it. The WebDAV password is not copied;
                  enter it once after applying.
                </p>

                <div className="flex flex-wrap gap-2">
                  <Button onClick={() => void importExternal("")} disabled={busy === "importing"}>
                    {busy === "importing" ? <Spinner /> : "Read my rclone server"}
                  </Button>
                </div>
              </>
            ) : (
              // No external rclone is configured, so reading one can only fail.
              // Kept available for a pasted command, but out of the way of the
              // path that works on a fresh install.
              <p className="text-sm leading-relaxed text-base-content/70">
                No rclone server is configured under Server connection below, so there is nothing to
                read. If you run rclone elsewhere, paste its mount command instead.
              </p>
            )}

            <details className="text-sm">
              <summary className="cursor-pointer text-base-content/70">
                My rclone has no remote control enabled
              </summary>
              <div className="mt-2 space-y-2">
                <Textarea
                  className="w-full font-mono text-xs"
                  rows={4}
                  placeholder="rclone mount nzbdav: /mnt/remote/nzbdav --vfs-cache-mode=full ..."
                  value={pastedCommand}
                  onChange={(e) => setPastedCommand(e.target.value)}
                />
                <Button
                  variant="ghost"
                  onClick={() => void importExternal(pastedCommand)}
                  disabled={!pastedCommand.trim() || busy === "importing"}
                >
                  Read this command
                </Button>
              </div>
            </details>

            {importResult && (
              <div className="space-y-2">
                {(importResult.mounts ?? []).map((row) => (
                  <div
                    key={row.mountPoint}
                    className="rounded-lg border border-base-300 p-3 text-sm"
                  >
                    <div className="font-medium">{row.mountPoint}</div>
                    <div className="text-base-content/60">
                      WebDAV folder {row.remotePath} · cache {row.vfsCacheMode}
                      {row.readAheadBytes ? (
                        <> · read ahead {formatBytes(row.readAheadBytes)}</>
                      ) : null}
                      {row.links === false ? <> · symlinks off</> : null}
                    </div>
                  </div>
                ))}

                {(importResult.warnings ?? []).map((warning) => (
                  <Alert key={warning} variant="warning" className="text-xs py-2">
                    {warning}
                  </Alert>
                ))}

                {importResult.imported && (
                  <Button
                    onClick={() => {
                      // Every field the preview reports is carried across. Dropping
                      // any of them would quietly retune a mount the operator
                      // migrated precisely to avoid retuning.
                      const imported = (importResult.mounts ?? []).map<BuiltinMount>((row) => ({
                        Id: row.id,
                        Name: row.name,
                        MountPoint: row.mountPoint,
                        RemotePath: row.remotePath,
                        Enabled: true,
                        VfsCacheMode: row.vfsCacheMode,
                        AllowOther: row.allowOther ?? true,
                        Links: row.links ?? true,
                        DirCacheTime: row.dirCacheTimeSeconds
                          ? secondsToTimeSpan(row.dirCacheTimeSeconds)
                          : undefined,
                        VfsCacheMaxAge: row.vfsCacheMaxAgeSeconds
                          ? secondsToTimeSpan(row.vfsCacheMaxAgeSeconds)
                          : undefined,
                        VfsCacheMaxSizeBytes: row.vfsCacheMaxSizeBytes ?? null,
                        ReadAheadBytes: row.readAheadBytes ?? null,
                      }));
                      writeMounts(imported.reduce(upsertMount, mounts));
                    }}
                  >
                    Use these settings
                  </Button>
                )}
              </div>
            )}
          </div>
        </SettingsCard>
      )}

      {enabled && (
        <SettingsCard
          icon="terminal"
          title="rclone log"
          description="The most recent output from the built-in rclone process."
        >
          <Button
            variant="ghost"
            className="btn-sm"
            onClick={() => {
              setShowLogs((visible) => !visible);
              if (!showLogs) void loadLogs();
            }}
          >
            {showLogs ? "Hide log" : "Show log"}
          </Button>
          {showLogs && (
            <pre className="mt-3 max-h-64 overflow-auto rounded-lg bg-base-300/60 p-3 text-xs leading-relaxed">
              <code>{logLines.length > 0 ? logLines.join("\n") : "Nothing logged yet."}</code>
            </pre>
          )}
        </SettingsCard>
      )}
    </div>
  );
}

/** Seconds from the status API back into the `[d.]hh:mm:ss` form config stores. */
function secondsToTimeSpan(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const remainder = seconds % 60;
  const time = [hours, minutes, remainder].map((part) => String(part).padStart(2, "0")).join(":");
  return days > 0 ? `${days}.${time}` : time;
}

export function isBuiltinMountSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return (
    config["rclone.builtin.enabled"] !== newConfig["rclone.builtin.enabled"] ||
    config["rclone.builtin.mounts"] !== newConfig["rclone.builtin.mounts"] ||
    config["rclone.builtin.rc-port"] !== newConfig["rclone.builtin.rc-port"] ||
    config["rclone.builtin.cache-dir"] !== newConfig["rclone.builtin.cache-dir"] ||
    config["rclone.builtin.cache-size-limit"] !== newConfig["rclone.builtin.cache-size-limit"]
  );
}
