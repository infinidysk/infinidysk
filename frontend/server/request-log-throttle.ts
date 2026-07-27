/**
 * Throttles repeated client-error access-log lines.
 *
 * A client that writes metadata sidecars into the read-only WebDAV tree re-attempts
 * on every scan, so a large library produces a steady stream of 4xx lines — enough to
 * bury every other log line and, in the reported case, roughly 10 requests/sec forever.
 * Keeping the first line per client and collapsing the rest preserves the signal that
 * something is failing without the flood.
 */
export const CLIENT_ERROR_LOG_THROTTLE_MS = 60_000;

type ThrottleEntry = { lastLogAt: number; suppressed: number };

/** Parked on `process` so duplicated bundles of this module share one window. */
const throttleState = process as typeof process & {
  __nzbdavClientErrorLogState?: Map<string, ThrottleEntry>;
};

function getState(): Map<string, ThrottleEntry> {
  return throttleState.__nzbdavClientErrorLogState ??= new Map();
}

/**
 * Coarse enough that a per-release path storm collapses to one key, specific enough
 * that a different client, method, status or mount stays independently visible.
 */
export function clientErrorKey(
  method: string,
  status: number,
  path: string,
  client: string,
): string {
  const [mount = ""] = path.replace(/^\/+/, "").split(/[/?#]/, 1);
  return `${method} ${status} /${mount} ${client}`;
}

/**
 * Returns whether the caller should emit a line for this key, plus how many lines were
 * suppressed since the last emitted one so the message can say what was skipped.
 */
export function shouldLogClientError(
  key: string,
  now = Date.now(),
): { log: boolean; suppressed: number } {
  const state = getState();
  const entry = state.get(key);

  if (entry && now - entry.lastLogAt < CLIENT_ERROR_LOG_THROTTLE_MS) {
    entry.suppressed += 1;
    return { log: false, suppressed: 0 };
  }

  const suppressed = entry?.suppressed ?? 0;
  state.set(key, { lastLogAt: now, suppressed: 0 });
  return { log: true, suppressed };
}

/** Test-only: reset throttle state between cases. */
export function resetClientErrorLogThrottleForTests(): void {
  getState().clear();
}
