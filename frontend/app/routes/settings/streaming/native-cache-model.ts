export type NativeFolder = {
  id: string;
  name: string;
  path: string;
  maxBytes: number;
  minFreeBytes: number;
  maxAgeDays: number;
  priority: number;
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
  return parsed.map(
    (item: Record<string, unknown>) =>
      Object.fromEntries(
        Object.entries(item).map(([key, val]) => [key[0]!.toLowerCase() + key.slice(1), val]),
      ) as NativeFolder,
  );
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
  }
  return null;
}

export const NATIVE_CACHE_KEYS = [
  "cache.mode",
  "cache.native.folders",
  "cache.native.metadata-path",
  "cache.native.writer-mb",
];

export function nativeSettingsValid(config: Record<string, string>): boolean {
  try {
    const folders = parseNativeFolders(config["cache.native.folders"]);
    const budget = Number(config["cache.native.writer-mb"] || "32");
    return (
      validateNativeFolders(folders) === null &&
      Number.isInteger(budget) &&
      budget >= 4 &&
      budget <= 256 &&
      (cacheMode(config) !== "native" || folders.some((folder) => folder.enabled))
    );
  } catch {
    return false;
  }
}
