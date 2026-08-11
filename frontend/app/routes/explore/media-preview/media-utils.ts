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

/**
 * MEDIA_ERR_* codes: 1 aborted, 2 network, 3 decode, 4 src-not-supported.
 *
 * A decode error after frames have already played means the decoder works and
 * the bytes went bad (missing or zero-filled segments), so it is worth a
 * reload; a decode error before any progress means the browser never had a
 * working pipeline for this file.
 */
export function classifyMediaError(code: number | null, hadPlaybackProgress = false): MediaErrorKind {
    switch (code) {
        case 1: return "aborted";
        case 3: return hadPlaybackProgress ? "retry" : "unsupported";
        case 4: return "unsupported";
        default: return "retry";
    }
}

export type CanPlayType = (type: string) => string;

/** Baseline every browser with a working media stack decodes. */
const BASELINE_TYPE = 'video/mp4; codecs="avc1.42E01E"';

/**
 * Codecs common in Usenet releases that browsers frequently cannot decode.
 * A label counts as unsupported only when every equivalent type string is
 * rejected (HEVC is spelled both hvc1 and hev1 depending on the platform).
 */
const CODEC_PROBES: { label: string; types: string[] }[] = [
    { label: "HEVC / H.265", types: ['video/mp4; codecs="hvc1.1.6.L93.B0"', 'video/mp4; codecs="hev1.1.6.L93.B0"'] },
    { label: "HEVC 10-bit", types: ['video/mp4; codecs="hvc1.2.4.L120.B0"', 'video/mp4; codecs="hev1.2.4.L120.B0"'] },
    { label: "AV1", types: ['video/mp4; codecs="av01.0.05M.08"'] },
    { label: "Dolby Digital (AC-3)", types: ['audio/mp4; codecs="ac-3"'] },
    { label: "Dolby Digital Plus (E-AC-3)", types: ['audio/mp4; codecs="ec-3"'] },
];

/**
 * True when canPlayType gives informative answers: a browser with a working
 * media stack always accepts baseline H.264, so a rejection there means the
 * probe environment cannot tell us anything (e.g. jsdom).
 */
export function canProbeCodecs(canPlayType: CanPlayType): boolean {
    return canPlayType(BASELINE_TYPE) !== "";
}

/**
 * Labels of the probed codecs this browser reports no support for. Returns
 * nothing when even the baseline is rejected — that means canPlayType is
 * uninformative (non-browser environment), not that every codec is missing.
 */
export function probeUnsupportedCodecs(canPlayType: CanPlayType): string[] {
    if (!canProbeCodecs(canPlayType)) return [];
    return CODEC_PROBES
        .filter(probe => probe.types.every(type => canPlayType(type) === ""))
        .map(probe => probe.label);
}

/**
 * Chromium reports a media fetch that failed at the HTTP layer with the same
 * MEDIA_ERR_SRC_NOT_SUPPORTED code as a missing decoder. A one-byte ranged
 * GET reproduces the media request cheaply and tells the two apart: an OK
 * response means the server served bytes and the browser rejected them.
 */
export async function probeSourceReachable(
    src: string,
    fetchFn: typeof fetch = fetch,
): Promise<boolean> {
    try {
        const response = await fetchFn(src, {
            headers: { Range: "bytes=0-0" },
            cache: "no-store",
            signal: AbortSignal.timeout(10_000),
        });
        return response.ok;
    } catch {
        return false;
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
