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
  "usenet.segment-cache.enabled": "true",
  "rclone.mount-dir": "/mnt/nzbdav",
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
  setupRequired: boolean,
): SetupDraft {
  const strategy = normalizeStrategy(config["api.import-strategy"]);
  const next = applyStrategy(config, strategy, managedEnv);
  if (setupRequired && strategy === "symlinks" && !("rclone.rc-enabled" in managedEnv)) {
    next["rclone.rc-enabled"] = "true";
  }

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
  return next;
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
    if (!draft.config["rclone.mount-dir"]?.trim()) {
      errors.push("Enter the rclone mount directory.");
    }
    if (draft.config["rclone.rc-enabled"] === "true") {
      const host = draft.config["rclone.host"]?.trim() ?? "";
      if (!isHttpUrl(host)) errors.push("Enter a valid http(s) rclone RC host.");
    }
    if (!draft.vfsReadAheadConfirmed) {
      errors.push("Confirm that the rclone sidecar has VFS read-ahead enabled.");
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
      (libraryDir === mountDir ||
        libraryDir.startsWith(`${mountDir}/`) ||
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
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.startsWith("/setup")) {
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
