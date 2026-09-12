import type {
  SetupWizardIngestionMethod,
  SetupWizardStrategy,
} from "~/clients/backend-client.server";
import type { ManagedEnvMap } from "~/components/ui";

export const SETUP_CONFIG_KEYS = [
  "api.import-strategy",
  "api.completed-downloads-dir",
  "api.categories",
  "api.key",
  "usenet.segment-cache.enabled",
  "rclone.mount-dir",
  "rclone.builtin.enabled",
  "rclone.builtin.mounts",
  "rclone.rc-enabled",
  "rclone.host",
  "rclone.user",
  "rclone.pass",
  "general.base-url",
  "arr.instances",
  "backup.schedule-enabled",
  "backup.schedule-time",
  "backup.retention-count",
  "media.library-dir",
] as const;

export const SETUP_DEFAULT_CONFIG: Record<string, string> = {
  "api.import-strategy": "symlinks",
  "api.completed-downloads-dir": "/data/completed-downloads",
  "api.categories": "movies,tv",
  "api.key": "",
  "usenet.segment-cache.enabled": "false",
  "rclone.mount-dir": "/mnt/nzbdav",
  // Running rclone ourselves needs no second container, so it is what a new
  // symlink install gets unless the operator chooses their own.
  "rclone.builtin.enabled": "true",
  "rclone.builtin.mounts": "",
  "rclone.rc-enabled": "false",
  "rclone.host": "",
  "rclone.user": "",
  "rclone.pass": "",
  "general.base-url": "http://localhost:3000",
  "arr.instances": '{"RadarrInstances":[],"SonarrInstances":[],"QueueRules":[]}',
  "backup.schedule-enabled": "false",
  "backup.schedule-time": "0",
  "backup.retention-count": "5",
  "media.library-dir": "",
};

export type ArrInstance = {
  Name?: string;
  Host: string;
  ApiKey: string;
  Enabled?: boolean;
};

export type ArrConfig = {
  RadarrInstances: ArrInstance[];
  SonarrInstances: ArrInstance[];
  QueueRules: { Message: string; Action: number }[];
  QueueReplacementSearchLimit?: number;
  QueueReplacementSearchWindowMinutes?: number;
};

export type SetupDraft = {
  config: Record<string, string>;
  ingestionMethods: SetupWizardIngestionMethod[];
  vfsReadAheadConfirmed: boolean;
};

export function normalizeStrategy(value: string | undefined): SetupWizardStrategy {
  return value?.trim().toLowerCase() === "strm" ? "strm" : "symlinks";
}

export function createInitialDraft(
  config: Record<string, string>,
  managedEnv: ManagedEnvMap,
  ingestionMethods: string[],
): SetupDraft {
  const strategy = normalizeStrategy(config["api.import-strategy"]);
  const next = applyStrategy(config, strategy, managedEnv);

  return {
    config: next,
    ingestionMethods: ingestionMethods.filter(isIngestionMethod),
    vfsReadAheadConfirmed: false,
  };
}

export function applyStrategy(
  config: Record<string, string>,
  strategy: SetupWizardStrategy,
  managedEnv: ManagedEnvMap,
): Record<string, string> {
  const next = { ...config };
  if (!("api.import-strategy" in managedEnv)) {
    next["api.import-strategy"] = strategy;
  }
  if (!("usenet.segment-cache.enabled" in managedEnv)) {
    next["usenet.segment-cache.enabled"] = strategy === "strm" ? "true" : "false";
  }
  // RC notifications address a separate rclone container. Proposing them while
  // InfiniDysk runs rclone itself would ask for a host that does not exist, so
  // they are only offered on the sidecar path — where the user must still opt
  // out explicitly.
  const usesBuiltin = next["rclone.builtin.enabled"] === "true";
  if (strategy === "symlinks" && !usesBuiltin && !("rclone.rc-enabled" in managedEnv)) {
    next["rclone.rc-enabled"] = "true";
  }
  return next;
}

/**
 * The mount list for a built-in setup: one mount covering the directory that
 * symlink imports resolve through. Anything more belongs in Settings, where the
 * whole list is editable.
 */
export function builtinMountsFor(mountDir: string): string {
  return JSON.stringify([
    { Id: "library", MountPoint: mountDir.trim(), RemotePath: "/", Enabled: true },
  ]);
}

type StoredMount = {
  Id?: string;
  MountPoint?: string;
  RemotePath?: string;
  Enabled?: boolean;
};

const trimTrailingSlash = (path: string) => (path.length > 1 ? path.replace(/\/+$/, "") : path);

function parseStoredMounts(value: string | undefined): StoredMount[] {
  if (!value?.trim()) return [];

  try {
    const parsed: unknown = JSON.parse(value);
    if (!Array.isArray(parsed)) return [];

    return parsed.filter(
      (entry): entry is StoredMount =>
        typeof entry === "object" && entry !== null && !Array.isArray(entry),
    );
  } catch {
    return [];
  }
}

/**
 * The mount list a completed built-in symlink setup should save.
 *
 * Setup is re-runnable, and an operator who opens it again has usually already
 * built a mount list in Settings: extra shares, tuned cache settings. Replacing
 * that with the wizard's single derived mount deletes work the wizard never
 * asked about, so an existing list is kept as it is.
 *
 * The one thing setup still owns is that something enabled serves the directory
 * symlink imports resolve through -- imports point at `<mount-dir>/.ids/...`,
 * which only the remote root mounted at exactly that directory exposes. When
 * nothing does, the first mount is repointed there rather than a second one
 * added: two mounts for one library is not what the wizard offered.
 */
export function builtinMountsForCompletion(existing: string | undefined, mountDir: string): string {
  const dir = trimTrailingSlash(mountDir.trim());
  const mounts = parseStoredMounts(existing);
  if (mounts.length === 0) return builtinMountsFor(mountDir);
  if (!dir) return JSON.stringify(mounts);

  const covered = mounts.some(
    (mount) =>
      mount.Enabled !== false &&
      trimTrailingSlash((mount.RemotePath ?? "/").trim() || "/") === "/" &&
      trimTrailingSlash((mount.MountPoint ?? "").trim()) === dir,
  );
  if (covered) return JSON.stringify(mounts);

  const [first, ...rest] = mounts;
  return JSON.stringify([{ ...first, MountPoint: dir, RemotePath: "/", Enabled: true }, ...rest]);
}

export function parseArrConfig(value: string | undefined): ArrConfig {
  try {
    const parsed = JSON.parse(value ?? "") as Partial<ArrConfig> | null;
    if (
      parsed &&
      Array.isArray(parsed.RadarrInstances) &&
      Array.isArray(parsed.SonarrInstances) &&
      Array.isArray(parsed.QueueRules)
    ) {
      return parsed as ArrConfig;
    }
  } catch {
    // Invalid persisted JSON falls back to an empty integration list.
  }

  return { RadarrInstances: [], SonarrInstances: [], QueueRules: [] };
}

export function serializeArrConfig(config: ArrConfig): string {
  return JSON.stringify(config);
}

export function changedSetupConfig(
  baseline: Record<string, string>,
  draft: Record<string, string>,
  managedEnv: ManagedEnvMap,
): Record<string, string> {
  const changed: Record<string, string> = {};
  for (const key of SETUP_CONFIG_KEYS) {
    if (key in managedEnv || baseline[key] === draft[key]) continue;
    changed[key] = draft[key] ?? "";
  }
  return changed;
}

export function completionSetupConfig(
  baseline: Record<string, string>,
  draft: SetupDraft,
  managedEnv: ManagedEnvMap,
): Record<string, string> {
  const config = changedSetupConfig(baseline, draft.config, managedEnv);
  const strategy = normalizeStrategy(draft.config["api.import-strategy"]);
  const usesBuiltin = draft.config["rclone.builtin.enabled"] === "true";
  const requiredKeys = [
    ...(strategy === "symlinks"
      ? usesBuiltin
        ? ["rclone.mount-dir", "rclone.builtin.enabled", "rclone.builtin.mounts"]
        : [
            "rclone.mount-dir",
            "rclone.builtin.enabled",
            "rclone.rc-enabled",
            "rclone.host",
            "rclone.user",
            "rclone.pass",
          ]
      : ["api.completed-downloads-dir", "general.base-url"]),
    "backup.schedule-enabled",
    "backup.schedule-time",
    "backup.retention-count",
    "media.library-dir",
    ...(draft.ingestionMethods.includes("arrs") ? ["arr.instances"] : []),
  ];

  for (const key of requiredKeys) {
    if (key in managedEnv) continue;
    config[key] = draft.config[key] ?? "";
  }

  // The wizard offers one mount, at the directory the rest of setup already
  // asked for -- but only writes it where there is nothing to lose. A rerun over
  // a configured install keeps the mounts it finds.
  if (strategy === "symlinks" && usesBuiltin && !("rclone.builtin.mounts" in managedEnv)) {
    config["rclone.builtin.mounts"] = builtinMountsForCompletion(
      baseline["rclone.builtin.mounts"],
      draft.config["rclone.mount-dir"] ?? "",
    );
  }

  // A STRM library has nothing to mount, so the wizard says nothing about the
  // built-in mount either way. Writing it would clobber a deliberate choice the
  // operator made elsewhere, and a detour through STRM would silently move them
  // onto the sidecar path.
  if (strategy === "strm") {
    delete config["rclone.builtin.enabled"];
    delete config["rclone.builtin.mounts"];
  }
  return config;
}

export function validateSetupStep(
  step: number,
  draft: SetupDraft,
  managedEnv: ManagedEnvMap,
  strategyChangeConfirmed: boolean,
  baselineStrategy: SetupWizardStrategy,
): string[] {
  const strategy = normalizeStrategy(draft.config["api.import-strategy"]);
  const errors: string[] = [];

  if (step === 1 && strategy === "symlinks") {
    const usesBuiltin = draft.config["rclone.builtin.enabled"] === "true";

    if (!draft.config["rclone.mount-dir"]?.trim()) {
      errors.push("Enter the rclone mount directory.");
    }

    // The RC host and the read-ahead confirmation both describe a separate
    // rclone container. With the built-in daemon there is nothing to point at
    // and no flags for the operator to check.
    if (!usesBuiltin) {
      if (draft.config["rclone.rc-enabled"] === "true") {
        const host = draft.config["rclone.host"]?.trim() ?? "";
        if (!isHttpUrl(host)) errors.push("Enter a valid http(s) rclone RC host.");
      }
      if (!draft.vfsReadAheadConfirmed) {
        errors.push("Confirm that the rclone sidecar has VFS read-ahead enabled.");
      }
    }
    if (
      "usenet.segment-cache.enabled" in managedEnv &&
      draft.config["usenet.segment-cache.enabled"] !== "false"
    ) {
      errors.push(
        "Disable the environment-managed segment cache before completing Symlinks setup.",
      );
    }
  }

  if (step === 1 && strategy === "strm") {
    if (!draft.config["api.completed-downloads-dir"]?.trim()) {
      errors.push("Enter the completed downloads directory.");
    }
    if (!isHttpUrl(draft.config["general.base-url"] ?? "")) {
      errors.push("Enter an absolute http(s) Base URL without credentials, query, or fragment.");
    }
  }

  if (step === 2 && draft.ingestionMethods.length === 0) {
    errors.push("Select at least one way to ingest content.");
  }

  if (step === 3 && draft.config["backup.schedule-enabled"] === "true") {
    const retention = Number.parseInt(draft.config["backup.retention-count"] ?? "", 10);
    if (!Number.isInteger(retention) || retention < 1) {
      errors.push("Backup retention must be at least 1.");
    }
  }

  if (step === 4) {
    const libraryDir = normalizePath(draft.config["media.library-dir"] ?? "");
    const mountDir = normalizePath(draft.config["rclone.mount-dir"] ?? "");
    if (
      libraryDir &&
      ((mountDir !== "" && (libraryDir === mountDir || libraryDir.startsWith(`${mountDir}/`))) ||
        libraryDir === "/completed-symlinks" ||
        libraryDir.startsWith("/completed-symlinks/"))
    ) {
      errors.push("Library Directory must be outside the rclone mount.");
    }
  }

  if (step === 5 && strategy !== baselineStrategy && !strategyChangeConfirmed) {
    errors.push("Confirm that changing strategy affects future imports only.");
  }

  return errors;
}

export function timeFromMinutes(value: string | undefined): string {
  const total = Math.max(0, Math.min(1439, Number.parseInt(value ?? "0", 10) || 0));
  return `${String(Math.floor(total / 60)).padStart(2, "0")}:${String(total % 60).padStart(2, "0")}`;
}

export function minutesFromTime(value: string): string {
  const [hour, minute] = value.split(":").map(Number);
  return String((hour ?? 0) * 60 + (minute ?? 0));
}

export function safeReturnTo(value: string | null): string {
  if (
    !value ||
    !value.startsWith("/") ||
    value.startsWith("//") ||
    value.includes("\\") ||
    [...value].some((character) => {
      const code = character.charCodeAt(0);
      return code <= 0x1f || code === 0x7f;
    }) ||
    value.startsWith("/setup")
  ) {
    return "/overview";
  }
  return value;
}

function isIngestionMethod(value: string): value is SetupWizardIngestionMethod {
  return value === "arrs" || value === "search" || value === "manual";
}

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return (
      (url.protocol === "http:" || url.protocol === "https:") &&
      !url.username &&
      !url.password &&
      !url.search &&
      !url.hash
    );
  } catch {
    return false;
  }
}

function normalizePath(value: string): string {
  const normalized = value.trim().replaceAll("\\", "/").replace(/\/+$/, "");
  return normalized;
}
