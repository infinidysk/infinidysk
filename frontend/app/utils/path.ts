export function getLeafDirectoryName(fullPath: string): string {
  // Normalize the path by removing a trailing slash/backslash.
  const normalizedPath = fullPath.replace(/[/\\]$/, "");

  // Find the index of the last separator.
  const lastSlash = normalizedPath.lastIndexOf("/");
  const lastBackslash = normalizedPath.lastIndexOf("\\");
  const lastSeparatorIndex = Math.max(lastSlash, lastBackslash);

  // Extract the final component.
  // Start the substring *after* the last separator.
  const leafName = normalizedPath.substring(lastSeparatorIndex + 1);

  // If the result is empty, it means the path was a root (e.g., '/', 'C:').
  if (leafName.length === 0) {
    // Return the root component itself (e.g., '/')
    return normalizedPath;
  }

  return leafName;
}

/** Explore link for a completed history item's content folder, or null when unavailable. */
export function getExploreContentLink(
  storage: string | null | undefined,
  category: string | null | undefined,
): string | null {
  if (!storage?.trim() || !category?.trim()) return null;
  const downloadFolder = getLeafDirectoryName(storage);
  if (!downloadFolder) return null;
  return `/explore/content/${encodeURIComponent(category.trim())}/${encodeURIComponent(downloadFolder)}`;
}

/** Explore link for a selected decoded breadcrumb directory, including the WebDAV root. */
export function getExploreBreadcrumbHref(parentDirectories: string[], index: number): string {
  if (index === -1) return "/explore";
  return `/explore/${parentDirectories
    .slice(0, index + 1)
    .map(encodeURIComponent)
    .join("/")}`;
}

export type ParsedExploreWebdavPath = { ok: true; path: string } | { ok: false };

/**
 * Decodes and validates an explore wildcard / WebDAV path.
 * Rejects malformed percent-encoding and empty path segments (e.g. content//release).
 */
export function parseExploreWebdavPath(raw: string): ParsedExploreWebdavPath {
  let decoded: string;
  try {
    decoded = decodeURIComponent(raw);
  } catch {
    return { ok: false };
  }

  // Drop only a leading empty segment (from a leading /) and a trailing empty
  // segment (from a trailing /). Internal empty segments from "//" stay and
  // are rejected so legacy /explore/content//{release} links stay not-found.
  const parts = decoded.split("/");
  if (parts.length > 0 && parts[0] === "") parts.shift();
  if (parts.length > 0 && parts[parts.length - 1] === "") parts.pop();

  if (parts.some((segment) => segment === "")) return { ok: false };

  return { ok: true, path: parts.join("/") };
}
