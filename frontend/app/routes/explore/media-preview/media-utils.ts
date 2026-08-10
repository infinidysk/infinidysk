/** Delay before auto-recovery attempt n (0-based): 1s, 2s, 4s. */
export function backoffMs(attempt: number): number {
    return 1000 * 2 ** Math.min(attempt, 4);
}

export const MAX_AUTO_ATTEMPTS = 3;

/**
 * Well above the default per-segment timeout/retry budget (~8s x 3) so the
 * watchdog only fires when the backend's own recovery has failed and the
 * response is not coming back — short `waiting` events never trigger it.
 */
export const STALL_THRESHOLD_MS = 30_000;

export type MediaErrorKind = "aborted" | "retry" | "unsupported";

/** MEDIA_ERR_* codes: 1 aborted, 2 network, 3 decode, 4 src-not-supported. */
export function classifyMediaError(code: number | null): MediaErrorKind {
    switch (code) {
        case 1: return "aborted";
        case 3:
        case 4: return "unsupported";
        default: return "retry";
    }
}

/** Append a query parameter, choosing `?` vs `&` from the existing URL. */
export function appendQueryParam(url: string, key: string, value: string): string {
    const separator = url.includes("?") ? "&" : "?";
    return `${url}${separator}${key}=${encodeURIComponent(value)}`;
}

export function buildMediaSrc(previewUrl: string, playerSession: string): string {
    return appendQueryParam(previewUrl, "playerSession", playerSession);
}

export function formatClock(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return "—";
    const total = Math.floor(seconds);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    const mm = h > 0 ? String(m).padStart(2, "0") : String(m);
    return `${h > 0 ? `${h}:` : ""}${mm}:${String(s).padStart(2, "0")}`;
}

export type TimeRangesLike = {
    length: number;
    start(index: number): number;
    end(index: number): number;
};

export function formatTimeRanges(ranges: TimeRangesLike): string {
    const parts: string[] = [];
    for (let i = 0; i < ranges.length; i++) {
        parts.push(`${formatClock(ranges.start(i))}–${formatClock(ranges.end(i))}`);
    }
    return parts.join(", ") || "—";
}

/** Seconds buffered ahead of the playhead (0 when the playhead is unbuffered). */
export function bufferedAhead(ranges: TimeRangesLike, currentTime: number): number {
    for (let i = 0; i < ranges.length; i++) {
        if (ranges.start(i) <= currentTime && currentTime <= ranges.end(i)) {
            return Math.max(0, ranges.end(i) - currentTime);
        }
    }
    return 0;
}

const READY_STATE_LABELS: Record<number, string> = {
    0: "HAVE_NOTHING",
    1: "HAVE_METADATA",
    2: "HAVE_CURRENT_DATA",
    3: "HAVE_FUTURE_DATA",
    4: "HAVE_ENOUGH_DATA",
};

const NETWORK_STATE_LABELS: Record<number, string> = {
    0: "NETWORK_EMPTY",
    1: "NETWORK_IDLE",
    2: "NETWORK_LOADING",
    3: "NETWORK_NO_SOURCE",
};

export function readyStateLabel(value: number): string {
    return READY_STATE_LABELS[value] ?? `unknown (${value})`;
}

export function networkStateLabel(value: number): string {
    return NETWORK_STATE_LABELS[value] ?? `unknown (${value})`;
}
