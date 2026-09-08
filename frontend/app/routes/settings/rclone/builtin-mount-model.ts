/**
 * Shape of one entry in the `rclone.builtin.mounts` config value, mirroring the
 * backend's RcloneMountConfig. Property names are PascalCase because the value
 * is serialized by the backend and edited here in place.
 *
 * `AllowOther` is deliberately absent from the settings UI — turning it off
 * breaks the only reason the mount exists — but it round-trips so a value set
 * through `NZBDAV_CONFIG__` is never silently dropped.
 */
export type BuiltinMount = {
  Id: string;
  // `| undefined` is explicit because the project builds with
  // exactOptionalPropertyTypes: an optional field that is assigned undefined has
  // to say so.
  Name?: string | undefined;
  MountPoint: string;
  RemotePath: string;
  Enabled: boolean;
  VfsCacheMode: string;
  AllowOther: boolean;
  Links: boolean;
  DirCacheTime?: string | undefined;
  VfsCacheMaxAge?: string | undefined;
  VfsCacheMaxSizeBytes?: number | null | undefined;
  ReadAheadBytes?: number | null | undefined;
};

export type MountStateInput = {
  configured: boolean;
  enabled: boolean;
  mounted: boolean;
};

export type MountState = {
  label: string;
  variant: "success" | "warning" | "neutral";
};

const MOUNT_DEFAULTS = {
  RemotePath: "/",
  Enabled: true,
  VfsCacheMode: "full",
  AllowOther: true,
  Links: true,
} as const;

/**
 * Reads the stored mount list. A value that cannot be read yields no mounts
 * rather than throwing: a malformed setting should leave the rest of the tab
 * usable so the operator can correct it.
 *
 * Entries without an id and a mount point are dropped rather than rendered.
 * They cannot be applied, they collide with each other in `upsertMount` because
 * their ids are both undefined, and React would render them under a duplicate
 * key.
 */
export function parseMounts(value: string | undefined): BuiltinMount[] {
  if (!value?.trim()) return [];

  try {
    const parsed: unknown = JSON.parse(value);
    if (!Array.isArray(parsed)) return [];

    return parsed
      .filter(isUsableEntry)
      .map((entry) => ({ ...MOUNT_DEFAULTS, ...entry }) as BuiltinMount);
  } catch {
    return [];
  }
}

function isUsableEntry(entry: unknown): entry is Partial<BuiltinMount> {
  if (typeof entry !== "object" || entry === null || Array.isArray(entry)) return false;
  const candidate = entry as Partial<BuiltinMount>;
  return Boolean(candidate.Id?.trim()) && Boolean(candidate.MountPoint?.trim());
}

export function serializeMounts(mounts: BuiltinMount[]): string {
  return JSON.stringify(mounts);
}

/** Adds a mount, or replaces the existing one with the same id, keeping its position. */
export function upsertMount(mounts: BuiltinMount[], mount: BuiltinMount): BuiltinMount[] {
  const index = mounts.findIndex((existing) => existing.Id === mount.Id);
  if (index < 0) return [...mounts, mount];

  const updated = [...mounts];
  updated[index] = mount;
  return updated;
}

export function removeMount(mounts: BuiltinMount[], id: string): BuiltinMount[] {
  return mounts.filter((mount) => mount.Id !== id);
}

/**
 * Names what the operator is looking at. "Unmanaged" is the one that needs
 * saying out loud: it is mounted now, but no configuration describes it, so the
 * next apply will remove it.
 */
export function describeMountState({ configured, enabled, mounted }: MountStateInput): MountState {
  if (!configured) return { label: "Unmanaged", variant: "warning" };
  if (!enabled) return { label: "Off", variant: "neutral" };
  return mounted
    ? { label: "Mounted", variant: "success" }
    : { label: "Not mounted", variant: "warning" };
}

/**
 * A new mount pre-filled with the settings most InfiniDysk users want.
 *
 * The path defaults to `symlinkMountDir` — the directory symlink imports already
 * resolve through — because that is the one value that must not be guessed. An
 * imported file points at `<mount-dir>/.ids/...`, so a mount anywhere else leaves
 * the whole library unplayable.
 */
export function newMount(index: number, symlinkMountDir: string | undefined): BuiltinMount {
  return {
    Id: index === 0 ? "library" : `library-${index + 1}`,
    MountPoint: symlinkMountDir?.trim() || "/mnt/remote/infinidysk",
    ...MOUNT_DEFAULTS,
  };
}

const trimTrailingSlash = (path: string) => (path.length > 1 ? path.replace(/\/+$/, "") : path);

/**
 * Whether an enabled mount covers the directory symlink imports resolve through.
 * False means imported files will not play, however healthy the mounts look, so
 * it is worth saying out loud rather than leaving to be discovered at playback.
 *
 * A mount higher up the tree covers everything beneath it, so `/mnt/remote`
 * covers `/mnt/remote/infinidysk`. Comparing for equality alone warned about
 * setups that work.
 */
export function coversSymlinkRoot(
  mounts: BuiltinMount[],
  symlinkMountDir: string | undefined,
): boolean {
  const root = symlinkMountDir?.trim();
  if (!root) return true;

  const normalizedRoot = trimTrailingSlash(root);
  return mounts.some((mount) => {
    if (!mount.Enabled) return false;
    const mountPoint = trimTrailingSlash(mount.MountPoint ?? "");
    if (!mountPoint) return false;
    return (
      mountPoint === normalizedRoot ||
      normalizedRoot.startsWith(mountPoint === "/" ? "/" : `${mountPoint}/`)
    );
  });
}

/**
 * rclone durations round-trip through .NET's TimeSpan, whose JSON form is
 * `[d.]hh:mm:ss`. Hours above 23 have to move into the day component or the
 * backend rejects the value, which is exactly the trap a plain "24:00:00"
 * would fall into.
 */
export function hoursToTimeSpan(hours: number): string {
  const safeHours = Number.isFinite(hours) && hours > 0 ? Math.floor(hours) : 0;
  const days = Math.floor(safeHours / 24);
  const remainder = safeHours % 24;
  const time = `${String(remainder).padStart(2, "0")}:00:00`;
  return days > 0 ? `${days}.${time}` : time;
}

/** Reads a `[d.]hh:mm:ss` duration back into whole hours. */
export function timeSpanToHours(value: string | undefined, fallback: number): number {
  if (!value) return fallback;

  const match = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.\d+)?$/.exec(value.trim());
  if (!match) return fallback;

  const [, days, hours, minutes] = match;
  const total = Number(days ?? 0) * 24 + Number(hours) + Number(minutes) / 60;
  return Number.isFinite(total) ? Math.round(total) : fallback;
}

/** Human-readable byte size for the read-only cache figures. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0)
    return "unknown";

  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }

  return `${value >= 10 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

/** Parses a size typed as bytes or with an rclone-style suffix ("512M", "20G"). */
export function parseSize(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) return null;

  const match = /^(\d+(?:\.\d+)?)\s*([BKMGT])?B?$/i.exec(trimmed);
  if (!match) return null;

  const multipliers: Record<string, number> = {
    B: 1,
    K: 1024,
    M: 1024 ** 2,
    G: 1024 ** 3,
    T: 1024 ** 4,
  };
  const amount = Number(match[1]);
  const multiplier = multipliers[(match[2] ?? "B").toUpperCase()] ?? 1;
  const bytes = Math.round(amount * multiplier);
  return Number.isFinite(bytes) && bytes >= 0 ? bytes : null;
}

/** Hours of cached data kept when a mount does not say otherwise. */
export const DEFAULT_CACHE_AGE_HOURS = 168;

/** Hours of directory listings cached when a mount does not say otherwise. */
export const DEFAULT_DIR_CACHE_HOURS = 168;

/** The fields that decide what a reconcile pass would actually do. */
type ServerMountRow = {
  id: string;
  mountPoint: string;
  remotePath: string;
  vfsCacheMode: string;
  configured: boolean;
  enabled: boolean;
  links?: boolean;
  readAheadBytes?: number | null;
  vfsCacheMaxAgeSeconds?: number;
};

type MountSignatureInput = {
  id: string;
  mountPoint: string;
  remotePath: string;
  cacheMode: string;
  enabled: boolean;
  links: boolean;
  readAheadBytes: number | null;
  cacheAgeHours: number;
  dirCacheHours: number;
};

// Every field the tab can edit has to appear here, or editing it would leave
// Apply enabled while the change is still unsaved.
const mountSignature = (m: MountSignatureInput) =>
  [
    m.id,
    m.mountPoint,
    m.remotePath,
    m.cacheMode.toLowerCase(),
    m.enabled,
    m.links,
    m.readAheadBytes ?? "default",
    m.cacheAgeHours,
    m.dirCacheHours,
  ].join("|");

/**
 * Whether the edited mounts match what the backend has persisted.
 *
 * Apply reconciles against the *saved* configuration, so pressing it with
 * unsaved edits applies the old settings and reports success — the operator is
 * told their change took effect when it did not. The status endpoint reports
 * what is persisted, so the two can be compared without threading the saved
 * config down from the settings page.
 */
export function mountsMatchServer(
  mounts: BuiltinMount[],
  rows: ServerMountRow[] | undefined,
): boolean {
  // Nothing fetched yet: there is no basis to claim a difference.
  if (!rows) return true;

  const local = mounts
    .map((mount) =>
      mountSignature({
        id: mount.Id,
        mountPoint: mount.MountPoint,
        remotePath: mount.RemotePath,
        cacheMode: mount.VfsCacheMode,
        enabled: mount.Enabled,
        links: mount.Links,
        readAheadBytes: mount.ReadAheadBytes ?? null,
        cacheAgeHours: timeSpanToHours(mount.VfsCacheMaxAge, DEFAULT_CACHE_AGE_HOURS),
        dirCacheHours: timeSpanToHours(mount.DirCacheTime, DEFAULT_DIR_CACHE_HOURS),
      }),
    )
    .sort();

  const server = rows
    .filter((row) => row.configured)
    .map((row) =>
      mountSignature({
        id: row.id,
        mountPoint: row.mountPoint,
        remotePath: row.remotePath,
        cacheMode: row.vfsCacheMode,
        enabled: row.enabled,
        links: row.links ?? true,
        readAheadBytes: row.readAheadBytes ?? null,
        // Seconds on the wire, hours in the form: normalize before comparing.
        cacheAgeHours:
          row.vfsCacheMaxAgeSeconds === undefined
            ? DEFAULT_CACHE_AGE_HOURS
            : Math.round(row.vfsCacheMaxAgeSeconds / 3600),
        dirCacheHours:
          row.dirCacheTimeSeconds === undefined
            ? DEFAULT_DIR_CACHE_HOURS
            : Math.round(row.dirCacheTimeSeconds / 3600),
      }),
    )
    .sort();

  return local.length === server.length && local.every((entry, index) => entry === server[index]);
}
