export type NativeFolder = {
  id: string;
  name: string;
  path: string;
  maxBytes: number;
  minFreeBytes: number;
  maxAgeDays: number;
  priority: number;
  highWaterPercent?: number;
  lowWaterPercent?: number;
  enabled: boolean;
  readOnly: boolean;
  storageType: "hdd" | "nas" | "ssd";
};

export function cacheMode(config: Record<string, string>): "off" | "segment" | "native" {
  const explicit = config["cache.mode"]?.trim().toLowerCase();
  if (explicit === "native" || explicit === "segment" || explicit === "off") return explicit;
  return config["usenet.segment-cache.enabled"] === "true" ? "segment" : "off";
}

export function parseNativeFolders(value: string | undefined): NativeFolder[] {
  const parsed: unknown = JSON.parse(value?.trim() || "[]");
  if (!Array.isArray(parsed)) throw new Error("Native cache folders must be an array.");
  // Accept backend serialization and frontend edits without changing the persisted shape.
  return parsed.map((item: unknown) => {
    if (item === null || typeof item !== "object" || Array.isArray(item))
      throw new Error("Native cache folders must contain folder objects.");
    return Object.fromEntries(
      Object.entries(item).map(([key, val]) => [key[0]!.toLowerCase() + key.slice(1), val]),
    ) as NativeFolder;
  });
}

export function validateNativeFolders(folders: NativeFolder[]): string | null {
  if (folders.length > 32) return "At most 32 folders are supported.";
  const ids = new Set<string>();
  const paths: string[] = [];
  for (const folder of folders) {
    if (!folder.id || !folder.name?.trim() || ids.has(folder.id))
      return "Folder names and unique IDs are required.";
    ids.add(folder.id);
    const path = folder.path?.replace(/\/+$/, "");
    if (!path?.startsWith("/") || path.split("/").some((part) => part === "." || part === ".."))
      return "Use an absolute, non-root folder path without dot segments.";
    if (
      paths.some(
        (other) => path === other || path.startsWith(other + "/") || other.startsWith(path + "/"),
      )
    )
      return "Cache folder paths must not overlap.";
    paths.push(path);
    if (
      !Number.isSafeInteger(folder.maxBytes) ||
      folder.maxBytes <= 0 ||
      !Number.isSafeInteger(folder.minFreeBytes) ||
      folder.minFreeBytes < 0
    )
      return "Folder quota must be positive and free-space reserve nonnegative.";
    if (
      !Number.isInteger(folder.maxAgeDays) ||
      folder.maxAgeDays < 0 ||
      !Number.isInteger(folder.priority)
    )
      return "Age and priority must be integers; age cannot be negative.";
    if (!["hdd", "nas", "ssd"].includes(folder.storageType)) return "Choose a storage type.";
    const high = folder.highWaterPercent ?? 90;
    const low = folder.lowWaterPercent ?? 80;
    if (!Number.isInteger(high) || !Number.isInteger(low) || low < 1 || low >= high || high > 100)
      return "Watermarks must be integers with 1 ≤ low < high ≤ 100.";
  }
  return null;
}

export const NATIVE_CACHE_KEYS = [
  "cache.mode",
  "usenet.segment-cache.enabled",
  "cache.native.folders",
  "cache.native.metadata-path",
  "cache.native.writer-mb",
  "cache.native.min-file-mb",
  "cache.native.chunk-mb",
];

export function isNativeCacheSettingsUpdated(
  saved: Record<string, string>,
  draft: Record<string, string>,
): boolean {
  return NATIVE_CACHE_KEYS.some((key) => saved[key] !== draft[key]);
}

export function nativeSettingsValid(config: Record<string, string>): boolean {
  try {
    const folders = parseNativeFolders(config["cache.native.folders"]);
    const decimal = (value: string | undefined, fallback: string) => {
      const text = value || fallback;
      return /^\d+$/.test(text) ? Number(text) : NaN;
    };
    const budget = decimal(config["cache.native.writer-mb"], "32");
    const minFile = decimal(config["cache.native.min-file-mb"], "100");
    const chunk = decimal(config["cache.native.chunk-mb"], "64");
    return (
      validateNativeFolders(folders) === null &&
      Number.isInteger(budget) &&
      budget >= 4 &&
      budget <= 256 &&
      Number.isInteger(minFile) &&
      minFile >= 0 &&
      minFile <= 1048576 &&
      Number.isInteger(chunk) &&
      chunk >= 4 &&
      chunk <= 256 &&
      chunk % 4 === 0 &&
      (cacheMode(config) !== "native" || folders.some((folder) => folder.enabled))
    );
  } catch {
    return false;
  }
}
