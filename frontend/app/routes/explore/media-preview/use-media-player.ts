import { useCallback, useEffect, useRef, useState } from "react";
import {
    backoffMs,
    classifyMediaError,
    MAX_AUTO_ATTEMPTS,
    STALL_THRESHOLD_MS,
} from "./media-utils";

export type PlayerStatus =
    | "loading"
    | "ready"
    | "playing"
    | "recovering"
    | "failed"
    | "unsupported";

export type PlayerEvent = {
    at: number;
    kind: string;
    detail?: string;
};

export type PlayerError = {
    code: number | null;
    message: string | null;
};

const MAX_EVENTS = 200;

/** A recovery that holds playback for this long counts as stable and
 *  re-arms the automatic attempt budget. */
const RECOVERY_STABLE_MS = 10_000;

/**
 * Drives a native media element pointed at a signed /view URL: autoplay on
 * open, progress tracking, and bounded recovery that reopens the same URL
 * (fresh range request) and resumes from the last good position when the
 * backend aborts a stream after exhausted transient retries.
 */
export function useMediaPlayer({ src }: { src: string }) {
    const mediaRef = useRef<HTMLMediaElement | null>(null);
    const [status, setStatus] = useState<PlayerStatus>("loading");
    const [buffering, setBuffering] = useState(false);
    const [attempts, setAttempts] = useState(0);
    const [error, setError] = useState<PlayerError | null>(null);
    const [startupMs, setStartupMs] = useState<number | null>(null);
    const [events, setEvents] = useState<PlayerEvent[]>([]);

    const attemptsRef = useRef(0);
    const lastGoodTimeRef = useRef(0);
    const lastProgressAtRef = useRef<number | null>(null);
    const loadStartedAtRef = useRef<number>(Date.now());
    const startupRecordedRef = useRef(false);
    const pendingSeekRef = useRef<number | null>(null);
    const wasPlayingRef = useRef(true); // opening click implies intent to play
    const generationRef = useRef(0);
    const recoveryTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const lastRecoveryAtRef = useRef<number | null>(null);

    const log = useCallback((kind: string, detail?: string) => {
        setEvents(prev => {
            const next = [...prev, { at: Date.now(), kind, ...(detail ? { detail } : {}) }];
            return next.length > MAX_EVENTS ? next.slice(next.length - MAX_EVENTS) : next;
        });
    }, []);

    const clearRecoveryTimer = useCallback(() => {
        if (recoveryTimerRef.current !== null) {
            clearTimeout(recoveryTimerRef.current);
            recoveryTimerRef.current = null;
        }
    }, []);

    const reload = useCallback(() => {
        const el = mediaRef.current;
        if (!el) return;
        generationRef.current += 1;
        // Same URL on purpose: playerSession continuity keeps the read session
        // correlated, and load() already forces a fresh request.
        el.src = src;
        loadStartedAtRef.current = Date.now();
        el.load();
    }, [src]);

    const beginRecovery = useCallback((reason: string) => {
        if (attemptsRef.current >= MAX_AUTO_ATTEMPTS) {
            setStatus("failed");
            log("failed", reason);
            return;
        }
        const attempt = attemptsRef.current;
        attemptsRef.current = attempt + 1;
        lastRecoveryAtRef.current = Date.now();
        setAttempts(attempt + 1);
        setStatus("recovering");
        setError(null);
        log("recovering", `attempt ${attempt + 1}/${MAX_AUTO_ATTEMPTS}: ${reason}`);

        const el = mediaRef.current;
        wasPlayingRef.current = el ? !el.paused && !el.ended : true;
        pendingSeekRef.current = lastGoodTimeRef.current;

        clearRecoveryTimer();
        recoveryTimerRef.current = setTimeout(reload, backoffMs(attempt));
    }, [clearRecoveryTimer, log, reload]);

    /** Manual retry from the failed state — resets the automatic attempt budget. */
    const retry = useCallback(() => {
        clearRecoveryTimer();
        attemptsRef.current = 0;
        setAttempts(0);
        setError(null);
        setStatus("loading");
        log("retry", "manual");
        reload();
    }, [clearRecoveryTimer, log, reload]);

    // React 19 ref cleanup: runs when the element detaches (unmount/close),
    // which passive-effect cleanups cannot guarantee — refs are nulled before
    // they run. Removing src + load() aborts the in-flight range request.
    const setMediaEl = useCallback((el: HTMLMediaElement | null) => {
        mediaRef.current = el;
        if (!el) return undefined;
        return () => {
            el.removeAttribute("src");
            el.load();
        };
    }, []);

    // Reset all per-source state when a different file is previewed.
    useEffect(() => {
        clearRecoveryTimer();
        attemptsRef.current = 0;
        lastGoodTimeRef.current = 0;
        lastProgressAtRef.current = null;
        pendingSeekRef.current = null;
        wasPlayingRef.current = true;
        loadStartedAtRef.current = Date.now();
        startupRecordedRef.current = false;
        setStatus("loading");
        setBuffering(false);
        setAttempts(0);
        setError(null);
        setStartupMs(null);
        setEvents([]);
    }, [src, clearRecoveryTimer]);

    // The source is applied imperatively, not via the JSX src attribute:
    // React 19 StrictMode's simulated unmount runs callback-ref cleanups
    // (which strip src) without re-applying props, permanently wiping a
    // JSX-set attribute. An effect re-establishes the source on every cycle.
    useEffect(() => {
        const el = mediaRef.current;
        if (!el) return;
        el.src = src;
        loadStartedAtRef.current = Date.now();
        el.load();
    }, [src]);

    // On unmount/close: stop pending recovery. The source release itself is
    // handled by the callback-ref cleanup in setMediaEl (refs are already
    // detached by the time effect cleanups run).
    useEffect(() => {
        return () => clearRecoveryTimer();
    }, [clearRecoveryTimer]);

    // Progress watchdog: the primary stall signal (browsers fire stalled /
    // waiting inconsistently). Only fires when playback should be advancing
    // but hasn't for the whole backend retry budget.
    useEffect(() => {
        const interval = setInterval(() => {
            const el = mediaRef.current;
            if (!el || el.paused || el.ended || el.seeking) return;
            if (status === "recovering" || status === "failed" || status === "unsupported") return;
            const last = lastProgressAtRef.current;
            if (last !== null && Date.now() - last > STALL_THRESHOLD_MS) {
                beginRecovery(`no playback progress for ${Math.round(STALL_THRESHOLD_MS / 1000)}s`);
            }
        }, 1000);
        return () => clearInterval(interval);
    }, [status, beginRecovery]);

    const recordStartup = useCallback(() => {
        if (startupRecordedRef.current) return;
        startupRecordedRef.current = true;
        setStartupMs(Date.now() - loadStartedAtRef.current);
    }, []);

    const handlers = {
        onLoadStart: () => log("loadstart"),
        onLoadedMetadata: () => {
            const el = mediaRef.current;
            log("loadedmetadata");
            if (el && pendingSeekRef.current !== null && Number.isFinite(el.duration)) {
                el.currentTime = Math.min(pendingSeekRef.current, Math.max(0, el.duration - 0.25));
                pendingSeekRef.current = null;
            }
        },
        onCanPlay: () => {
            const el = mediaRef.current;
            recordStartup();
            setBuffering(false);
            // Recovered/loaded while paused: leave the recover/loading banner
            // even though no play event will follow.
            setStatus(prev => (prev === "recovering" || prev === "loading" ? "ready" : prev));
            if (el && wasPlayingRef.current) {
                // play() may return undefined in non-standard environments.
                void Promise.resolve(el.play()).catch(() => { /* autoplay blocked — controls remain */ });
            }
        },
        onPlay: () => {
            setStatus("playing");
            log("play");
        },
        onPlaying: () => {
            setStatus("playing");
            setBuffering(false);
        },
        onPause: () => {
            setStatus(prev => (prev === "playing" ? "ready" : prev));
            log("pause");
        },
        onTimeUpdate: () => {
            const el = mediaRef.current;
            if (!el) return;
            lastGoodTimeRef.current = el.currentTime;
            lastProgressAtRef.current = Date.now();
            setBuffering(false);
            // A recovery that sustains playback re-arms the attempt budget.
            if (attemptsRef.current > 0
                && lastRecoveryAtRef.current !== null
                && Date.now() - lastRecoveryAtRef.current > RECOVERY_STABLE_MS) {
                attemptsRef.current = 0;
                setAttempts(0);
                log("recovered", "playback stable");
            }
        },
        onSeeked: () => {
            const el = mediaRef.current;
            if (!el) return;
            lastGoodTimeRef.current = el.currentTime;
            lastProgressAtRef.current = Date.now();
            log("seeked", formatSeekDetail(el.currentTime));
        },
        onSeeking: () => log("seeking"),
        onWaiting: () => {
            setBuffering(true);
            log("waiting");
        },
        onStalled: () => log("stalled"),
        onEmptied: () => log("emptied"),
        onError: () => {
            const el = mediaRef.current;
            const mediaError = el?.error ?? null;
            const code = mediaError?.code ?? null;
            const kind = classifyMediaError(code);
            // ABORTED fires for our own close/reload — never a real failure.
            if (kind === "aborted") return;
            const message = mediaError?.message ?? null;
            setError({ code, message });
            log("error", `code ${code ?? "?"}${message ? `: ${message}` : ""}`);
            if (kind === "unsupported") {
                setStatus("unsupported");
                return;
            }
            beginRecovery("network error");
        },
    };

    return {
        mediaRef,
        setMediaEl,
        handlers,
        status,
        buffering,
        attempts,
        maxAttempts: MAX_AUTO_ATTEMPTS,
        error,
        startupMs,
        events,
        retry,
        lastGoodTimeRef,
        lastProgressAtRef,
    };
}

function formatSeekDetail(time: number): string {
    return `to ${time.toFixed(1)}s`;
}

export type MediaPlayer = ReturnType<typeof useMediaPlayer>;
