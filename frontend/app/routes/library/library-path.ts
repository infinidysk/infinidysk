import type { LibraryCatalogItem } from "~/clients/backend-client.server";

export function fileName(path: string): string {
  return path.split("/").pop() || path;
}

export function fullLibraryLinkPath(linkPath: string, libraryRoot: string | null): string {
  if (linkPath.startsWith("/") || !libraryRoot) return linkPath;
  return `${libraryRoot.replace(/\/+$/, "")}/${linkPath}`;
}

export function libraryPath(item: LibraryCatalogItem, libraryRoot: string | null): string | null {
  const linkPath = item.mappings[0]?.linkPath;
  return linkPath ? fullLibraryLinkPath(linkPath, libraryRoot) : (item.contentPath ?? null);
}
