// URL_BASE controls the sub-path the app is hosted under, e.g. "/nzbdav".
//
// - Configured via the `URL_BASE` env var. Set as a Docker build arg so it gets baked
//   into the React Router basename, Vite asset paths, and the `__URL_BASE__` global below.
//   Also read at runtime by the Express server so it mounts middleware under the same
//   prefix. Both ends must agree — the build arg and the runtime env var.
// - Empty string ("") and "/" both mean "app is at the root".
// - The Vite `define` in vite.config.ts replaces `__URL_BASE__` with the normalized
//   value (e.g. `"/nzbdav"` or `""`) at compile time.
// - The normalized form never has a trailing slash, so `URL_BASE + "/api"` is always
//   well-formed.

declare const __URL_BASE__: string;

/**
 * Normalize a raw URL_BASE value into "" (root) or "/path" form — single leading
 * slash, no trailing slash. Mirrored (not imported) in react-router.config.ts,
 * vite.config.ts, and server.ts because those run outside the Vite app graph —
 * keep them in sync.
 */
export function normalizeUrlBase(raw: string | undefined): string {
  if (!raw) return "";
  const trimmed = raw.trim();
  if (trimmed === "" || trimmed === "/") return "";
  const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  return withLeading.replace(/\/+$/, "");
}

export const URL_BASE: string =
  typeof __URL_BASE__ !== "undefined"
    ? __URL_BASE__
    : normalizeUrlBase(typeof process !== "undefined" ? process.env?.URL_BASE : "");

/**
 * Prefix a server-relative path with URL_BASE. Always returns a leading slash.
 *   withUrlBase("/api/foo")      // "/nzbdav/api/foo" or "/api/foo"
 *   withUrlBase("api/foo")       // "/nzbdav/api/foo" or "/api/foo"
 *   withUrlBase("/api?mode=x")   // "/nzbdav/api?mode=x" or "/api?mode=x"
 */
export function withUrlBase(path: string): string {
  const normalizedPath = path.startsWith("/") ? path : `/${path}`;
  return `${URL_BASE}${normalizedPath}`;
}
