import { useEffect, useState, type Dispatch, type SetStateAction } from "react";
import {
  Alert,
  Button,
  Input,
  ManagedSetting,
  Select,
  SettingsCard,
  Toggle,
} from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";
import {
  cacheMode,
  parseNativeFolders,
  validateNativeFolders,
  type NativeFolder,
} from "./native-cache-model";

type CacheStatus = {
  activeMode: string;
  configuredMode: string;
  restartRequired: boolean;
  initializationError?: string;
  initializationPending?: boolean;
  reservedBufferBytes: number;
  counters?: {
    hitBlocks: number;
    missBlocks: number;
    committedBytes: number;
    fallbacks: number;
    ioTimeouts: number;
  };
  folders: {
    id: string;
    online: boolean;
    writable: boolean;
    committedBytes: number;
    entries: number;
    error?: string;
  }[];
  jobs: {
    id: string;
    folderId: string;
    operation: string;
    state: string;
    result?: number;
    error?: string;
    probe?: {
      fileSystem: string;
      capability: string;
      readable: boolean;
      writable: boolean;
      durableWriteVerified: boolean;
      availableBytes: number;
      error?: string;
    };
  }[];
};

type CacheEntry = {
  key: string;
  itemId: string;
  name?: string;
  generation?: string;
  length: number;
  allocatedBytes: number;
  verifiedBytes: number;
  pinned: boolean;
};
type RangePage = { ranges: { offset: number; count: number }[]; nextAfter: number | null };

export function NativeCacheSettings({
  config,
  setNewConfig,
}: {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
}) {
  const [status, setStatus] = useState<CacheStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [cachePage, setCachePage] = useState<{
    folderId: string;
    entries: CacheEntry[];
    nextAfter: string | null;
  } | null>(null);
  const [rangePage, setRangePage] = useState<(RangePage & { key: string }) | null>(null);
  const inspectRanges = async (key: string, afterOffset = -1) => {
    setBusy(true);
    setError(null);
    try {
      const query = new URLSearchParams({ key, afterOffset: String(afterOffset), limit: "50" });
      const response = await fetch(withUrlBase(`/api/native-cache/ranges?${query}`));
      if (!response.ok) throw new Error("Could not load verified cache ranges.");
      setRangePage({ ...((await response.json()) as RangePage), key });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Range listing failed.");
    } finally {
      setBusy(false);
    }
  };
  const browse = async (folderId: string, after?: string) => {
    setBusy(true);
    try {
      const query = new URLSearchParams({ folderId, limit: "50", ...(after ? { after } : {}) });
      const response = await fetch(withUrlBase(`/api/native-cache/entries?${query}`));
      if (!response.ok) throw new Error("Could not load cached files.");
      const page = (await response.json()) as { entries: CacheEntry[]; nextAfter: string | null };
      setCachePage({ ...page, folderId });
      setRangePage(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Cache listing failed.");
    } finally {
      setBusy(false);
    }
  };
  const pin = async (entry: CacheEntry) => {
    setBusy(true);
    try {
      const response = await fetch(withUrlBase("/api/native-cache/operations"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ operation: "pin", cacheKey: entry.key, pinned: !entry.pinned }),
      });
      if (!response.ok) throw new Error("Could not update cache retention.");
      setCachePage(
        (current) =>
          current && {
            ...current,
            entries: current.entries.map((item) =>
              item.key === entry.key ? { ...item, pinned: !item.pinned } : item,
            ),
          },
      );
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Pin operation failed.");
    } finally {
      setBusy(false);
    }
  };
  useEffect(() => {
    const abort = new AbortController();
    const refresh = async () => {
      try {
        const response = await fetch(withUrlBase("/api/native-cache"), { signal: abort.signal });
        if (!response.ok) throw new Error("Could not load native cache status.");
        setStatus((await response.json()) as CacheStatus);
      } catch (cause) {
        if (!abort.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Status unavailable.");
      }
    };
    void refresh();
    const timer = setInterval(() => void refresh(), 10_000);
    return () => {
      abort.abort();
      clearInterval(timer);
    };
  }, []);
  let folders: NativeFolder[] = [];
  let parseError: string | null = null;
  try {
    folders = parseNativeFolders(config["cache.native.folders"]);
  } catch {
    parseError =
      "Saved folder configuration is invalid. Correct it through the API or environment before editing.";
  }
  const validation = parseError ?? validateNativeFolders(folders);
  const update = (next: NativeFolder[]) =>
    setNewConfig({ ...config, "cache.native.folders": JSON.stringify(next) });
  const edit = (id: string, patch: Partial<NativeFolder>) =>
    update(folders.map((folder) => (folder.id === id ? { ...folder, ...patch } : folder)));
  const operate = async (folderId: string, operation: string, jobId?: string) => {
    if (
      operation === "clear" &&
      !globalThis.confirm(
        "Clear application-owned cached files in this folder? Active and pinned files will be retained. Source media is not deleted.",
      )
    )
      return;
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(withUrlBase("/api/native-cache/operations"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          folderId,
          operation,
          jobId,
          confirmFolderId: operation === "clear" ? folderId : undefined,
        }),
      });
      if (!response.ok)
        throw new Error(
          "Cache operation was rejected. Check that the saved folder is active and writable.",
        );
      const next = await fetch(withUrlBase("/api/native-cache"));
      if (next.ok) setStatus((await next.json()) as CacheStatus);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Cache operation failed.");
    } finally {
      setBusy(false);
    }
  };
  return (
    <SettingsCard
      icon="storage"
      title="Disk cache"
      description="Select one cache engine. Native cache keeps verified final movie/episode bytes in large files; Segment cache keeps decoded Usenet articles."
    >
      <ManagedSetting configKeys={["cache.mode", "usenet.segment-cache.enabled"]}>
        <label className="flex flex-col gap-2 text-sm">
          Cache mode (restart required)
          <Select
            value={cacheMode(config)}
            onChange={(event) =>
              setNewConfig({
                ...config,
                "cache.mode": event.target.value,
                "usenet.segment-cache.enabled": String(event.target.value === "segment"),
              })
            }
          >
            <option value="off">Off</option>
            <option value="segment">Segment — fast local storage</option>
            <option value="native">Native — whole media on HDD/NAS</option>
          </Select>
        </label>
      </ManagedSetting>
      <p className="text-xs">
        Only one engine runs. Changing mode or native storage settings requires restart; existing
        cache data is retained, not converted or deleted. For a 50 TB HDD/NAS cache, use Native and
        keep its metadata on local storage.
      </p>
      {status && (
        <p className="text-sm">
          Running: {status.activeMode}; saved: {status.configuredMode}
          {status.restartRequired ? " — restart required" : ""}. Buffer reservations:{" "}
          {(status.reservedBufferBytes / 1048576).toFixed(0)} MiB.
          {status.initializationPending
            ? " Storage initialization pending; source playback remains available."
            : ""}
        </p>
      )}
      {status?.counters && (
        <p className="text-xs">
          Verified block hits: {status.counters.hitBlocks}; misses: {status.counters.missBlocks};
          committed this process: {(status.counters.committedBytes / 1e9).toFixed(2)} GB; source
          fallbacks: {status.counters.fallbacks} ({status.counters.ioTimeouts} storage timeouts).
        </p>
      )}
      {(error || validation || status?.initializationError) && (
        <Alert variant="warning">{error ?? validation ?? status?.initializationError}</Alert>
      )}
      {cacheMode(config) === "native" && (
        <>
          <ManagedSetting configKey="cache.native.metadata-path">
            <label className="flex flex-col gap-2 text-sm">
              Local metadata directory (never NAS)
              <Input
                value={config["cache.native.metadata-path"] ?? ""}
                placeholder="/config/native-cache-metadata"
                onChange={(event) =>
                  setNewConfig({ ...config, "cache.native.metadata-path": event.target.value })
                }
              />
            </label>
          </ManagedSetting>
          <ManagedSetting configKey="cache.native.writer-mb">
            <label className="flex flex-col gap-2 text-sm">
              Native stream buffer budget (4–256 MiB)
              <Input
                type="number"
                min={4}
                max={256}
                value={config["cache.native.writer-mb"] ?? "32"}
                onChange={(event) =>
                  setNewConfig({ ...config, "cache.native.writer-mb": event.target.value })
                }
              />
            </label>
          </ManagedSetting>
          <ManagedSetting configKey="cache.native.folders">
            <div className="space-y-4">
              {folders.map((folder) => {
                const live = status?.folders.find((item) => item.id === folder.id);
                return (
                  <fieldset
                    key={folder.id}
                    className="space-y-3 rounded border border-base-content/15 p-4"
                  >
                    <legend className="px-2">{folder.name || "New cache folder"}</legend>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <label>
                        Name
                        <Input
                          value={folder.name}
                          onChange={(event) => edit(folder.id, { name: event.target.value })}
                        />
                      </label>
                      <label>
                        Absolute folder path
                        <Input
                          value={folder.path}
                          onChange={(event) => edit(folder.id, { path: event.target.value })}
                        />
                      </label>
                      <label>
                        Quota (decimal TB)
                        <Input
                          type="number"
                          min={0.001}
                          step="any"
                          value={folder.maxBytes / 1e12}
                          onChange={(event) =>
                            edit(folder.id, {
                              maxBytes: Math.round(Number(event.target.value) * 1e12),
                            })
                          }
                        />
                      </label>
                      <label>
                        Free-space reserve (GB)
                        <Input
                          type="number"
                          min={0}
                          step="any"
                          value={folder.minFreeBytes / 1e9}
                          onChange={(event) =>
                            edit(folder.id, {
                              minFreeBytes: Math.round(Number(event.target.value) * 1e9),
                            })
                          }
                        />
                      </label>
                      <label>
                        Maximum idle age (days; 0 = unlimited)
                        <Input
                          type="number"
                          min={0}
                          value={folder.maxAgeDays}
                          onChange={(event) =>
                            edit(folder.id, { maxAgeDays: Number(event.target.value) })
                          }
                        />
                      </label>
                      <label>
                        Placement priority (highest first)
                        <Input
                          type="number"
                          value={folder.priority}
                          onChange={(event) =>
                            edit(folder.id, { priority: Number(event.target.value) })
                          }
                        />
                      </label>
                      <label>
                        Start eviction at quota (%)
                        <Input
                          type="number"
                          min={2}
                          max={100}
                          value={folder.highWaterPercent ?? 90}
                          onChange={(event) =>
                            edit(folder.id, { highWaterPercent: Number(event.target.value) })
                          }
                        />
                      </label>
                      <label>
                        Evict down to quota (%)
                        <Input
                          type="number"
                          min={1}
                          max={99}
                          value={folder.lowWaterPercent ?? 80}
                          onChange={(event) =>
                            edit(folder.id, { lowWaterPercent: Number(event.target.value) })
                          }
                        />
                      </label>
                      <label>
                        Storage type
                        <Select
                          value={folder.storageType}
                          onChange={(event) =>
                            edit(folder.id, {
                              storageType: event.target.value as NativeFolder["storageType"],
                            })
                          }
                        >
                          <option value="hdd">HDD</option>
                          <option value="nas">NAS</option>
                          <option value="ssd">SSD</option>
                        </Select>
                      </label>
                    </div>
                    <Toggle
                      label="Enabled"
                      checked={folder.enabled}
                      onChange={(event) => edit(folder.id, { enabled: event.target.checked })}
                    />
                    <Toggle
                      label="Read-only (hits/import only; no writes or eviction)"
                      checked={folder.readOnly}
                      onChange={(event) => edit(folder.id, { readOnly: event.target.checked })}
                    />
                    {live && (
                      <p className="text-xs">
                        {live.online
                          ? live.writable
                            ? "Online, writable"
                            : "Online, read-only/unavailable for writes"
                          : "Offline or storage identity changed"}{" "}
                        · {(live.committedBytes / 1e12).toFixed(3)} TB allocated · {live.entries}{" "}
                        files. {live.error}
                      </p>
                    )}
                    <div className="flex flex-wrap gap-2">
                      <Button disabled={busy || !live} onClick={() => void browse(folder.id)}>
                        View cached files
                      </Button>
                      {["probe", "scan", "clear"].map((operation) => (
                        <Button
                          key={operation}
                          disabled={busy || !live || (operation === "clear" && !live.writable)}
                          onClick={() => void operate(folder.id, operation)}
                        >
                          {operation}
                        </Button>
                      ))}
                      <Button
                        onClick={() => update(folders.filter((item) => item.id !== folder.id))}
                      >
                        Remove configuration
                      </Button>
                    </div>
                    <p className="text-xs">
                      Operations target the currently running folder configuration. Removing a
                      folder does not delete its files.
                    </p>
                  </fieldset>
                );
              })}
              <Button
                disabled={!!parseError || folders.length >= 32}
                onClick={() =>
                  update([
                    ...folders,
                    {
                      id: crypto.randomUUID(),
                      name: "Native cache",
                      path: "",
                      maxBytes: 10e12,
                      minFreeBytes: 100e9,
                      maxAgeDays: 0,
                      priority: 0,
                      enabled: true,
                      readOnly: false,
                      storageType: "nas",
                    },
                  ])
                }
              >
                Add cache folder
              </Button>
            </div>
          </ManagedSetting>
          {cachePage && (
            <div className="space-y-2">
              <h4>
                Cached files —{" "}
                {folders.find((folder) => folder.id === cachePage.folderId)?.name ??
                  cachePage.folderId}
              </h4>
              <p className="text-xs">
                Catalogue coverage is a snapshot; playback rechecks volume identity and block
                integrity. Pinned files are retained during automatic eviction and folder clear;
                unpin them before clearing.
              </p>
              {cachePage.entries.map((entry) => (
                <div key={entry.key} className="flex flex-wrap items-center gap-2 text-xs">
                  <span>{entry.name ?? entry.itemId}</span>
                  <span>
                    {entry.length ? ((entry.verifiedBytes / entry.length) * 100).toFixed(1) : "0"}%
                    verified · {(entry.allocatedBytes / 1e9).toFixed(3)} GB allocated
                  </span>
                  <span className="break-all">
                    Generation: {entry.generation ?? "unknown until written or scanned"}
                  </span>
                  <Button
                    disabled={busy}
                    aria-label={`Verified ranges for ${entry.name ?? entry.itemId}`}
                    onClick={() => void inspectRanges(entry.key)}
                  >
                    Verified ranges
                  </Button>
                  <Button
                    disabled={busy}
                    aria-label={`${entry.pinned ? "Unpin" : "Pin"} ${entry.name ?? entry.itemId}`}
                    onClick={() => void pin(entry)}
                  >
                    {entry.pinned ? "Unpin" : "Pin"}
                  </Button>
                  {rangePage?.key === entry.key && (
                    <div className="w-full">
                      {rangePage.ranges.map((range) => (
                        <p key={range.offset}>
                          Bytes {range.offset.toLocaleString()}–
                          {(range.offset + range.count - 1).toLocaleString()}
                        </p>
                      ))}
                      {rangePage.ranges.length === 0 && <p>No verified ranges in this page.</p>}
                      <Button disabled={busy} onClick={() => void inspectRanges(entry.key)}>
                        First range page
                      </Button>
                      <Button
                        disabled={busy || rangePage.nextAfter === null}
                        onClick={() => void inspectRanges(entry.key, rangePage.nextAfter ?? -1)}
                      >
                        Next range page
                      </Button>
                    </div>
                  )}
                </div>
              ))}
              {cachePage.entries.length === 0 && <p>No cached files in this page.</p>}
              <Button disabled={busy} onClick={() => void browse(cachePage.folderId)}>
                First page / refresh
              </Button>
              <Button
                disabled={busy || !cachePage.nextAfter}
                onClick={() => void browse(cachePage.folderId, cachePage.nextAfter ?? undefined)}
              >
                Next page
              </Button>
            </div>
          )}
          <div className="space-y-2">
            {status?.jobs.map((job) => (
              <p key={job.id} className="text-xs">
                {job.operation} / {job.folderId}: {job.state}
                {job.result !== undefined ? ` (${job.result})` : ""} {job.error}
                {job.probe && (
                  <span>
                    {" "}
                    — {job.probe.fileSystem} / {job.probe.capability}; readable:{" "}
                    {job.probe.readable ? "yes" : "no"}; writable:{" "}
                    {job.probe.writable ? "yes" : "no"}; durable write verified:{" "}
                    {job.probe.durableWriteVerified ? "yes" : "no"}; available:{" "}
                    {(job.probe.availableBytes / 1e9).toFixed(1)} GB. {job.probe.error}
                  </span>
                )}
                {["queued", "running"].includes(job.state) && (
                  <Button
                    disabled={busy}
                    onClick={() => void operate(job.folderId, "cancel", job.id)}
                  >
                    Cancel
                  </Button>
                )}
              </p>
            ))}
          </div>
        </>
      )}
    </SettingsCard>
  );
}
