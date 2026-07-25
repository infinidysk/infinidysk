/** Shared frontend-process clock for expected backend-not-ready noise. */
export const BACKEND_STARTUP_GRACE_MS = 30_000;
export const BACKEND_FAILURE_LOG_THROTTLE_MS = 60_000;

/** Synthetic code for HTTP 503 / migration handoff from the backend client. */
export const BACKEND_MIGRATING_CODE = "MIGRATING";

const EXPECTED_NETWORK_CODES = new Set([
  "ECONNREFUSED",
  "ECONNRESET",
  "EPIPE",
  "ETIMEDOUT",
  "ENOTFOUND",
  "UND_ERR_CONNECT_TIMEOUT",
  "UND_ERR_HEADERS_TIMEOUT",
  "UND_ERR_BODY_TIMEOUT",
  "UND_ERR_SOCKET",
  BACKEND_MIGRATING_CODE,
]);

let lastExpectedBackendUnavailableLogAt = 0;

/** Uses process uptime so Express and SSR bundles agree even if this module is duplicated. */
export function isWithinBackendStartupGrace(uptimeMs = process.uptime() * 1000): boolean {
  return uptimeMs < BACKEND_STARTUP_GRACE_MS;
}

function collectErrorCandidates(error: unknown): unknown[] {
  const candidates: unknown[] = [error];
  if (!error || typeof error !== "object") return candidates;

  const withCause = error as { cause?: unknown; errors?: unknown[] };
  if (withCause.cause) {
    candidates.push(withCause.cause);
    if (typeof withCause.cause === "object") {
      const nested = withCause.cause as { cause?: unknown; errors?: unknown[] };
      if (nested.cause) candidates.push(nested.cause);
      if (Array.isArray(nested.errors)) candidates.push(...nested.errors);
    }
  }
  if (Array.isArray(withCause.errors)) candidates.push(...withCause.errors);
  return candidates;
}

export function isExpectedNetworkCode(code: string | undefined): boolean {
  return typeof code === "string" && EXPECTED_NETWORK_CODES.has(code);
}

export function isExpectedBackendConnectionError(error: unknown): boolean {
  return collectErrorCandidates(error).some((candidate) => {
    if (!candidate || typeof candidate !== "object") return false;
    return isExpectedNetworkCode((candidate as { code?: string }).code);
  });
}

/**
 * Expected backend-unreachable failures: network connection/timeout codes on a
 * BackendUnavailableError (or raw connection error), or the migration/503 handoff.
 * A BackendUnavailableError with no network code (e.g. wrapped TypeError from a
 * bad BACKEND_URL) stays unexpected so it keeps ERR + stack.
 */
export function isExpectedBackendUnavailableError(error: unknown): boolean {
  if (error instanceof Error && error.name === "BackendUnavailableError") {
    const code = (error as { code?: string }).code;
    if (isExpectedNetworkCode(code)) return true;
    if (error.message.includes("backend is starting or migrating")) return true;
    return isExpectedBackendConnectionError(error);
  }
  return isExpectedBackendConnectionError(error);
}

export function formatBackendUnavailableReason(error: unknown): string {
  if (error instanceof Error) return error.message;
  return String(error);
}

/**
 * Returns true when the caller should emit a throttled warn for an expected
 * backend-unavailable failure. Shared by unhandledRejection and SSR handleError.
 */
export function shouldEmitThrottledBackendUnavailableLog(now = Date.now()): boolean {
  if (now - lastExpectedBackendUnavailableLogAt < BACKEND_FAILURE_LOG_THROTTLE_MS) {
    return false;
  }
  lastExpectedBackendUnavailableLogAt = now;
  return true;
}

/** Test-only: reset throttle state between cases. */
export function resetBackendUnavailableLogThrottleForTests(): void {
  lastExpectedBackendUnavailableLogAt = 0;
}
