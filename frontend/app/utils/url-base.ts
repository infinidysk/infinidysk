// NZBDAV_URL_BASE controls the sub-path the app is hosted under, e.g. "/nzbdav".
//
// - Configured via the `NZBDAV_URL_BASE` env var (bare `URL_BASE` is accepted as
//   a fallback). Set as a Docker build arg so it gets baked into the React Router
//   basename, Vite asset paths, and the `__URL_BASE__` global below. Also read at
//   runtime by the Express server so it mounts middleware under the same prefix.
//   Both ends must agree — server.ts refuses to start on a mismatch.
// - Empty string ("") and "/" both mean "app is at the root".
// - The Vite `define` in vite.config.ts replaces `__URL_BASE__` with the normalized
//   value (e.g. `"/nzbdav"` or `""`) at compile time.
// - The normalized form never has a trailing slash, so `URL_BASE + "/api"` is always
//   well-formed.

declare const __URL_BASE__: string;

// Path segments only: unreserved URL characters plus "/". Anything else is
// rejected — Express 5 hands the mount prefix to path-to-regexp v8, where "("
// or "{" crash at boot with a raw stack and ":" or "*" silently turn the
// prefix into a route pattern (e.g. "/nzb:dav" would serve under "/nzbanything").
const SAFE_URL_BASE = /^[A-Za-z0-9._~\-/]+$/;

/**
 * Normalize a raw URL_BASE value into "" (root) or "/path" form — single leading
 * slash, no trailing slash. Throws on characters that Express would misparse as
 * a route pattern. Mirrored (not imported) in server.ts, which is compiled by
 * tsc into dist-node without the app graph — keep them in sync (url-base.test.ts
 * asserts parity).
 */
export function normalizeUrlBase(raw: string | undefined): string {
  if (!raw) return "";
  const trimmed = raw.trim();
  if (trimmed === "" || trimmed === "/") return "";
  if (!SAFE_URL_BASE.test(trimmed)) {
    throw new Error(
      `Invalid URL base ${JSON.stringify(raw)}: only letters, digits, ".", "_", "~", "-", and "/" are allowed`,
    );
  }
  const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  return withLeading.replace(/\/+$/, "");
}

/** Read the URL base from the environment: NZBDAV_URL_BASE, then bare URL_BASE. */
export function urlBaseFromEnv(env: Record<string, string | undefined>): string {
  return normalizeUrlBase(env["NZBDAV_URL_BASE"] ?? env["URL_BASE"]);
}

export const URL_BASE: string =
  typeof __URL_BASE__ !== "undefined"
    ? __URL_BASE__
    : urlBaseFromEnv(typeof process !== "undefined" ? (process.env ?? {}) : {});

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
