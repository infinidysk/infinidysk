/**
 * Display-name resolution for active reads. The queue deobfuscates most
 * video files at import time, but when an obfuscated leaf survives (e.g.
 * "b082fa0beaa644d3aa01045d5b8d0b36.mkv") the release folder in the WebDAV
 * path is a far more useful label than the leaf.
 */

// Structural mount roots that are never useful as display names.
const IGNORED_PARENTS = new Set(["content", "nzbs", ".ids", "completed-symlinks"]);

/**
 * Port of the backend's ObfuscationUtil.IsProbablyObfuscated (itself a port
 * of sabnzbd's deobfuscate_filenames heuristic). Keep the two in sync.
 */
export function isProbablyObfuscated(fileName: string): boolean {
  const base = fileName.replace(/\.[^.]*$/, "");

  // Certainly obfuscated
  if (/^[a-f0-9]{32}$/.test(base)) return true;
  if (/^[a-f0-9.]{40,}$/.test(base)) return true;
  if (/[a-f0-9]{30}/.test(base) && (base.match(/\[\w+\]/g) ?? []).length >= 2) return true;
  if (/^abc\.xyz/.test(base)) return true;

  // Signals of a typical, clear name
  const decimals = countChars(base, (c) => c >= "0" && c <= "9");
  const upper = countChars(base, (c) => c >= "A" && c <= "Z");
  const lower = countChars(base, (c) => c >= "a" && c <= "z");
  const spacesDots = countChars(base, (c) => c === " " || c === "." || c === "_");

  if (upper >= 2 && lower >= 2 && spacesDots >= 1) return false;
  if (spacesDots >= 3) return false;
  if (upper + lower >= 4 && decimals >= 4 && spacesDots >= 1) return false;
  if (base.length > 0 && base[0]! >= "A" && base[0]! <= "Z" && lower > 2 && upper / lower <= 0.25)
    return false;

  return true;
}

export type DisplayName = {
  name: string;
  /** True when the name comes from the release folder because the leaf is obfuscated. */
  isReleaseFallback: boolean;
};

export function displayNameForRead(fileName: string, path: string): DisplayName {
  const leaf = fileName || lastPathSegment(path);
  if (!isProbablyObfuscated(leaf)) return { name: leaf, isReleaseFallback: false };

  const segments = path.split("/").filter(Boolean);
  const parent = segments.length >= 2 ? segments[segments.length - 2]! : "";
  if (IGNORED_PARENTS.has(parent.toLowerCase())) return { name: leaf, isReleaseFallback: false };

  const ext = leaf.includes(".") ? leaf.slice(leaf.lastIndexOf(".")) : "";
  return { name: parent + ext, isReleaseFallback: true };
}

export function lastPathSegment(path: string): string {
  const idx = path.lastIndexOf("/");
  return idx >= 0 ? path.slice(idx + 1) : path;
}

function countChars(value: string, predicate: (char: string) => boolean): number {
  let count = 0;
  for (const char of value) if (predicate(char)) count++;
  return count;
}
