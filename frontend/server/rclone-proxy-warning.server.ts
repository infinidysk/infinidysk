export const RCLONE_PROXY_WARNING_INTERVAL_MS = 30 * 60 * 1000;

export type RcloneProxyObservation = {
  detected: boolean;
  shouldLog: boolean;
  suppressed: number;
};

type WarnLogger = (message: string) => void;

type RcloneProxyWarningState = {
  lastDetectedAtMs: number | null;
  lastLogAtMs: number | null;
  suppressed: number;
};

const processState = process as typeof process & {
  __infinidyskRcloneProxyWarningState?: RcloneProxyWarningState;
};

function state(): RcloneProxyWarningState {
  processState.__infinidyskRcloneProxyWarningState ??= {
    lastDetectedAtMs: null,
    lastLogAtMs: null,
    suppressed: 0,
  };
  return processState.__infinidyskRcloneProxyWarningState;
}

export function isRcloneUserAgent(userAgent: string | string[] | undefined): boolean {
  const value = Array.isArray(userAgent) ? userAgent.join(" ") : userAgent;
  return /^rclone(?:\/|\s|$)/i.test(value?.trim() ?? "");
}

export function recordRcloneProxyRequest(
  userAgent: string | string[] | undefined,
  nowMs = Date.now(),
): RcloneProxyObservation {
  if (!isRcloneUserAgent(userAgent)) {
    return { detected: false, shouldLog: false, suppressed: 0 };
  }

  const current = state();
  current.lastDetectedAtMs = nowMs;
  if (
    current.lastLogAtMs !== null &&
    nowMs - current.lastLogAtMs < RCLONE_PROXY_WARNING_INTERVAL_MS
  ) {
    current.suppressed += 1;
    return { detected: true, shouldLog: false, suppressed: 0 };
  }

  const suppressed = current.suppressed;
  current.lastLogAtMs = nowMs;
  current.suppressed = 0;
  return { detected: true, shouldLog: true, suppressed };
}

export function observeRcloneProxyRequest(
  userAgent: string | string[] | undefined,
  warn: WarnLogger,
  nowMs = Date.now(),
): boolean {
  const observation = recordRcloneProxyRequest(userAgent, nowMs);
  if (!observation.shouldLog) return observation.detected;

  const suppressed =
    observation.suppressed > 0
      ? ` Suppressed ${observation.suppressed} repeated detection(s) since the previous warning.`
      : "";
  warn(
    "rclone WebDAV traffic is using the frontend proxy on port 3000. Point the rclone sidecar directly at backend port 8080 on the trusted Docker network to avoid proxying streamed bytes." +
      suppressed,
  );
  return true;
}

export function isRcloneProxyWarningActive(nowMs = Date.now()): boolean {
  const lastDetectedAtMs = state().lastDetectedAtMs;
  return lastDetectedAtMs !== null && nowMs - lastDetectedAtMs < RCLONE_PROXY_WARNING_INTERVAL_MS;
}

export function resetRcloneProxyWarningForTests(): void {
  delete processState.__infinidyskRcloneProxyWarningState;
}
