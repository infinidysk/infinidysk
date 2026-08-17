const BACKEND_PATH_PREFIXES = [
  "/api",
  "/ready",
  "/metrics",
  "/view",
  "/.ids",
  "/nzbs",
  "/content",
  "/completed-symlinks",
  "/adapters/",
];

/** Decode a path; return null on malformed percent-encoding instead of throwing. */
export function safeDecodePath(path: string): string | null {
  try {
    return decodeURIComponent(path);
  } catch {
    return null;
  }
}

/** Match path on segment boundaries so `/apifoo` does not match `/api`. */
export function matchesBackendPathPrefix(decodedPath: string): boolean {
  return BACKEND_PATH_PREFIXES.some((prefix) => {
    const normalized = prefix.endsWith("/") ? prefix.slice(0, -1) : prefix;
    return decodedPath === normalized || decodedPath.startsWith(normalized + "/");
  });
}

/** True when the path is under `/api` (decoded, segment-bounded). */
export function isBackendApiPath(pathname: string): boolean {
  const decodedPath = safeDecodePath(pathname);
  if (decodedPath === null) return false;
  return decodedPath === "/api" || decodedPath.startsWith("/api/");
}

/** True when the path is the Prometheus metrics endpoint. */
export function isBackendMetricsPath(pathname: string): boolean {
  const decodedPath = safeDecodePath(pathname);
  return decodedPath === "/metrics";
}

export function shouldProxyToBackend(method: string, pathname: string): boolean {
  const normalizedMethod = method.toUpperCase();
  if (normalizedMethod === "PROPFIND" || normalizedMethod === "OPTIONS") {
    return true;
  }

  const decodedPath = safeDecodePath(pathname);
  if (decodedPath === null) return false;

  return matchesBackendPathPrefix(decodedPath);
}

/** True when compression should be skipped for backend-proxied media/API paths. */
export function shouldSkipCompression(pathname: string): boolean {
  const decodedPath = safeDecodePath(pathname);
  if (decodedPath === null) return false;
  return matchesBackendPathPrefix(decodedPath);
}
