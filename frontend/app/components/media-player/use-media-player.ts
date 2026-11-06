import { useCallback, useEffect, useRef, useState } from "react";
import type HlsJs from "hls.js";
import {
  backoffMs,
  classifyMediaError,
  MAX_AUTO_ATTEMPTS,
  probeSource,
  STALL_THRESHOLD_MS,
} from "./media-utils";

export type PlayerStatus =
  | "loading"
  | "ready"
  | "playing"
  | "recovering"
  | "transcoding"
  | "failed"
  | "unsupported"
  | "unavailable"
  | "missing-payload";

/** Direct streaming of the signed `/view` URL, or the HLS compatibility fallback. */
export type PlaybackMode = "direct" | "hls";

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

const HLS_MIME_TYPE = "application/vnd.apple.mpegurl";

/** A requested resume is dropped if the element never became seekable in this window. */
const PENDING_SEEK_TTL_MS = 15_000;

export const HLS_UNSUPPORTED_MESSAGE = "This browser cannot play direct or HLS streams.";

export type UseMediaPlayerArgs = {
  /** Signed direct `/view` URL (or an HLS manifest when `hlsFallbackSrc` is omitted). */
  src: string;
  /** HLS compatibility URL. Absent for callers that only ever play direct streams. */
  hlsFallbackSrc?: string;
};

/**
 * Drives a native media element pointed at a signed /view URL: autoplay on
 * open, progress tracking, and bounded recovery that reopens the same URL
 * (fresh range request) and resumes from the last good position when the
 * backend aborts a stream after exhausted transient retries.
 *
 * When a compatibility URL is supplied, playback starts in HLS mode and the direct
 * stream stays available as an explicit choice. The only automatic mode change is HLS
 * falling back to direct when the browser cannot run HLS at all; network and decode
 * failures never change modes.
 */
export function useMediaPlayer({ src, hlsFallbackSrc }: UseMediaPlayerArgs) {
  const fallbackSrc = hlsFallbackSrc ?? "";
  const hlsAvailable = fallbackSrc !== "";

  const mediaRef = useRef<HTMLMediaElement | null>(null);
  const [status, setStatus] = useState<PlayerStatus>("loading");
  const [buffering, setBuffering] = useState(false);
  const [attempts, setAttempts] = useState(0);
  const [error, setError] = useState<PlayerError | null>(null);
  const [startupMs, setStartupMs] = useState<number | null>(null);
  const [events, setEvents] = useState<PlayerEvent[]>([]);
  const [unavailableStatus, setUnavailableStatus] = useState<number | null>(null);
  // Catalog playback defaults to the converted HLS stream: it carries AAC audio the
  // browser always decodes and seeks by segment instead of by a slow byte range.
  const [mode, setMode] = useState<PlaybackMode>(() => (hlsFallbackSrc ? "hls" : "direct"));
  // Recovery inside compatibility mode re-attaches the stream instead of reloading `src`.
  const [reloadToken, setReloadToken] = useState(0);

  const attemptsRef = useRef(0);
  const framesBaselineRef = useRef(0);
  const lastGoodTimeRef = useRef(0);
  const lastProgressAtRef = useRef<number | null>(null);
  const loadStartedAtRef = useRef<number>(Date.now());
  const startupRecordedRef = useRef(false);
  const pendingSeekRef = useRef<number | null>(null);
  const pendingSeekAtRef = useRef(0);
  const wasPlayingRef = useRef(true); // opening click implies intent to play
  const generationRef = useRef(0);
  const recoveryTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastRecoveryAtRef = useRef<number | null>(null);
  const hlsRef = useRef<{ destroy: () => void } | null>(null);

  const log = useCallback((kind: string, detail?: string) => {
    setEvents((prev) => {
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

  const destroyHls = useCallback(() => {
    const instance = hlsRef.current;
    hlsRef.current = null;
    if (!instance) return;
    try {
      instance.destroy();
    } catch {
      // hls.js may already have torn itself down after a fatal error.
    }
  }, []);

  /**
   * Requests a one-shot resume for the next usable metadata event. A position at the
   * very start is never requested: a fresh open must not look like a pending seek, or a
   * later metadata event would yank playback back to the beginning.
   */
  const requestResume = useCallback((time: number) => {
    if (!(time > 0.5)) return;
    pendingSeekRef.current = time;
    pendingSeekAtRef.current = Date.now();
  }, []);

  /**
   * Applies a requested resume as soon as the element can accept one. MSE reports an
   * unknown duration until the manifest is parsed, so the caller retries on every
   * metadata-shaped event instead of relying on `loadedmetadata` alone.
   */
  const applyPendingSeek = useCallback(() => {
    const el = mediaRef.current;
    const target = pendingSeekRef.current;
    if (!el || target === null) return;
    if (!Number.isFinite(el.duration) || el.duration <= 0) return;
    if (Date.now() - pendingSeekAtRef.current > PENDING_SEEK_TTL_MS) {
      pendingSeekRef.current = null;
      return;
    }

    pendingSeekRef.current = null;
    const clamped = Math.min(target, Math.max(0, el.duration - 0.25));
    if (Math.abs(el.currentTime - clamped) > 1) el.currentTime = clamped;
  }, []);

  const reload = useCallback(() => {
    const el = mediaRef.current;
    if (!el) return;
    generationRef.current += 1;
    if (mode === "hls") {
      // Re-attach the HLS stream rather than handing the element a direct URL:
      // recovery must never silently change modes.
      loadStartedAtRef.current = Date.now();
      requestResume(lastGoodTimeRef.current);
      setReloadToken((token) => token + 1);
      return;
    }
    // Same URL on purpose: playerSession continuity keeps the read session
    // correlated, and load() already forces a fresh request.
    el.src = src;
    loadStartedAtRef.current = Date.now();
    el.load();
  }, [mode, requestResume, src]);

  const beginRecovery = useCallback(
    (reason: string) => {
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
      requestResume(lastGoodTimeRef.current);

      clearRecoveryTimer();
      recoveryTimerRef.current = setTimeout(reload, backoffMs(attempt));
    },
    [clearRecoveryTimer, log, reload, requestResume],
  );

  /**
   * One-way direct→HLS switch that keeps the current position. An automatic switch
   * follows a rejected source (nothing ever played), so it keeps the user's intent to
   * play; a manual switch preserves whether playback was actually running.
   */
  const switchToCompatibilityMode = useCallback(
    (reason: string, autoPlay = false) => {
      if (!hlsAvailable) return;
      const el = mediaRef.current;
      // A failed attempt can leave the element at 0; keep the last position that played.
      if (el && Number.isFinite(el.currentTime) && el.currentTime > 0) {
        lastGoodTimeRef.current = el.currentTime;
      }
      requestResume(lastGoodTimeRef.current);
      wasPlayingRef.current = autoPlay || (el ? !el.paused : true);
      attemptsRef.current = 0;
      setAttempts(0);
      setError(null);
      setBuffering(true);
      log("compatibility-mode", reason);
      setMode((current) => (current === "hls" ? current : "hls"));
    },
    [hlsAvailable, log, requestResume],
  );

  const activateCompatibilityMode = useCallback(
    () => switchToCompatibilityMode("manual"),
    [switchToCompatibilityMode],
  );

  /**
   * Returns to the signed direct stream, keeping the current position. Used when the
   * browser cannot run HLS at all and by the explicit mode control.
   */
  const switchToDirectMode = useCallback(
    (reason: string, autoPlay = false) => {
      const el = mediaRef.current;
      // A failed attempt can leave the element at 0; keep the last position that played.
      if (el && Number.isFinite(el.currentTime) && el.currentTime > 0) {
        lastGoodTimeRef.current = el.currentTime;
      }
      requestResume(lastGoodTimeRef.current);
      wasPlayingRef.current = autoPlay || (el ? !el.paused : true);
      attemptsRef.current = 0;
      setAttempts(0);
      // The direct path reports readiness through canplay, which only leaves
      // loading/recovering/transcoding states.
      setStatus("loading");
      setError(null);
      setBuffering(true);
      log("direct-mode", reason);
      setMode("direct");
    },
    [log, requestResume],
  );

  const activateDirectMode = useCallback(() => switchToDirectMode("manual"), [switchToDirectMode]);

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
  const setMediaEl = useCallback(
    (el: HTMLMediaElement | null) => {
      mediaRef.current = el;
      if (!el) return undefined;
      return () => {
        destroyHls();
        el.removeAttribute("src");
        el.load();
      };
    },
    [destroyHls],
  );

  // Reset all per-source state when a different file is previewed. Bumping
  // the generation invalidates async work (recovery timers, source checks)
  // still in flight for the previous source.
  useEffect(() => {
    clearRecoveryTimer();
    destroyHls();
    generationRef.current += 1;
    // VideoPlaybackQuality counters are cumulative for the element's
    // lifetime; snapshot them so progress checks only count this source.
    framesBaselineRef.current = totalVideoFrames(mediaRef.current) ?? 0;
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
    setUnavailableStatus(null);
    setMode(hlsFallbackSrc ? "hls" : "direct");
  }, [src, hlsFallbackSrc, clearRecoveryTimer, destroyHls]);

  // The source is applied imperatively, not via the JSX src attribute:
  // React 19 StrictMode's simulated unmount runs callback-ref cleanups
  // (which strip src) without re-applying props, permanently wiping a
  // JSX-set attribute. An effect re-establishes the source on every cycle.
  useEffect(() => {
    const el = mediaRef.current;
    if (!el) return;
    // Compatibility mode owns the element's source.
    if (mode !== "direct") return;
    el.src = src;
    loadStartedAtRef.current = Date.now();
    el.load();
  }, [src, mode]);

  // Compatibility attachment: native HLS where the engine has it, MSE via hls.js
  // otherwise. hls.js is imported lazily so direct-only callers never download it.
  useEffect(() => {
    if (mode !== "hls" || !hlsAvailable) return;
    const el = mediaRef.current;
    if (!el) return;

    const generation = ++generationRef.current;
    requestResume(lastGoodTimeRef.current);
    setError(null);
    setBuffering(true);
    setStatus("transcoding");

    if (el.canPlayType(HLS_MIME_TYPE) !== "") {
      el.src = fallbackSrc;
      loadStartedAtRef.current = Date.now();
      el.load();
      return () => destroyHls();
    }

    let disposed = false;
    const attach = async () => {
      // Dynamic so direct-only callers never download the hls.js bundle: the
      // module is only needed once compatibility mode actually activates.
      let Hls: typeof HlsJs;
      try {
        Hls = (await import("hls.js")).default;
      } catch {
        if (disposed || generationRef.current !== generation) return;
        switchToDirectMode(HLS_UNSUPPORTED_MESSAGE, true);
        return;
      }

      if (disposed || generationRef.current !== generation) return;
      if (!Hls.isSupported()) {
        switchToDirectMode(HLS_UNSUPPORTED_MESSAGE, true);
        return;
      }

      const instance = new Hls({
        manifestLoadingTimeOut: 100_000,
        fragLoadingTimeOut: 130_000,
        manifestLoadingMaxRetry: 6,
        fragLoadingMaxRetry: 6,
        // Segments are produced on demand; a deeper forward buffer absorbs a slow one
        // (a cold Usenet read or an encoder hiccup) instead of stalling playback.
        maxBufferLength: 60,
        maxMaxBufferLength: 120,
        // Keep memory bounded after long watches and seeks.
        backBufferLength: 30,
      });
      hlsRef.current = instance;

      instance.on(Hls.Events.MANIFEST_PARSED, () => {
        if (generationRef.current !== generation) return;
        applyPendingSeek();
        setBuffering(false);
        setStatus((prev) => (prev === "transcoding" || prev === "loading" ? "ready" : prev));
        if (wasPlayingRef.current) {
          // play() may return undefined in non-standard environments.
          void Promise.resolve(el.play()).catch(() => {
            /* autoplay blocked — controls remain */
          });
        }
      });

      instance.on(Hls.Events.ERROR, (_event, data) => {
        if (generationRef.current !== generation || !data.fatal) return;
        // A failed fragment is usually transient (a cold segment, a dropped stream).
        // Re-attach the stream at the current position rather than killing playback;
        // the bounded attempt budget still ends in a terminal state when it is real.
        log("hls-error", String(data.details));
        setError({ code: null, message: String(data.details) });
        beginRecovery(
          data.type === Hls.ErrorTypes.NETWORK_ERROR
            ? "HLS network error"
            : `HLS ${data.type === Hls.ErrorTypes.MEDIA_ERROR ? "decode" : "stream"} error`,
        );
      });

      instance.attachMedia(el);
      instance.loadSource(fallbackSrc);
    };

    void attach();

    return () => {
      disposed = true;
      destroyHls();
    };
  }, [
    mode,
    hlsAvailable,
    src,
    fallbackSrc,
    reloadToken,
    beginRecovery,
    applyPendingSeek,
    requestResume,
    destroyHls,
    log,
    switchToDirectMode,
  ]);

  // On unmount/close: stop pending recovery and release the HLS pipeline. The
  // source release itself is handled by the callback-ref cleanup in setMediaEl
  // (refs are already detached by the time effect cleanups run).
  useEffect(() => {
    return () => {
      clearRecoveryTimer();
      destroyHls();
    };
  }, [clearRecoveryTimer, destroyHls]);

  // Progress watchdog: the primary stall signal (browsers fire stalled /
  // waiting inconsistently). Only fires when playback should be advancing
  // but hasn't for the whole backend retry budget.
  useEffect(() => {
    const interval = setInterval(() => {
      const el = mediaRef.current;
      if (!el || el.paused || el.ended || el.seeking) return;
      if (
        status === "recovering" ||
        status === "transcoding" ||
        status === "failed" ||
        status === "unsupported" ||
        status === "unavailable" ||
        status === "missing-payload"
      )
        return;
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

  // Frames decoded for the current source are the strongest evidence the
  // decoder pipeline worked. Audio elements have no frame counter and fall
  // back to the progress watchdog timestamp (which a recovery seek can also
  // set — a weaker signal, but the retry budget bounds the damage).
  const hasDecodedProgress = (el: HTMLMediaElement | null): boolean => {
    const frames = totalVideoFrames(el);
    if (frames !== null) return frames > framesBaselineRef.current;
    return lastProgressAtRef.current !== null;
  };

  // Chromium reports a media fetch that failed at the HTTP layer with the
  // same code 4 as an unsupported stream, so verify the source before
  // deciding which failure this is.
  const handleUnsupported = (code: number | null) => {
    if (code !== 4) {
      setStatus("unsupported");
      return;
    }
    if (mode === "hls") {
      // The manifest is not probe-able as direct media. A rejected HLS stream is usually
      // a bad fragment after a seek, so recover at the current position before giving up.
      beginRecovery("HLS playback error");
      return;
    }
    const generation = generationRef.current;
    void probeSource(src).then((outcome) => {
      if (generationRef.current !== generation) return;
      switch (outcome.kind) {
        case "served":
          // Bytes were served; the browser rejected them.
          setStatus("unsupported");
          break;
        case "missing-payload":
          // Local file metadata is gone — re-downloading won't help
          // it, so surface a terminal state instead of retry loops.
          log("source-check", "backend reports the file data is missing");
          setStatus("missing-payload");
          break;
        case "denied":
          log("source-check", `media request refused with HTTP ${outcome.status}`);
          setUnavailableStatus(outcome.status);
          setStatus("unavailable");
          break;
        case "server-error":
          log(
            "source-check",
            outcome.status !== null
              ? `media request failed with HTTP ${outcome.status}`
              : "media request failed (network or timeout)",
          );
          beginRecovery("media request failed");
          break;
      }
    });
  };

  const handlers = {
    onLoadStart: () => log("loadstart"),
    onLoadedMetadata: () => {
      log("loadedmetadata");
      applyPendingSeek();
    },
    // MSE reports an unknown duration until the manifest is parsed, so a resume
    // requested before that lands here instead of on the first loadedmetadata.
    onDurationChange: () => applyPendingSeek(),
    onCanPlay: () => {
      const el = mediaRef.current;
      recordStartup();
      applyPendingSeek();
      setBuffering(false);
      // Recovered/loaded while paused: leave the recover/loading banner
      // even though no play event will follow.
      setStatus((prev) =>
        prev === "recovering" || prev === "loading" || prev === "transcoding" ? "ready" : prev,
      );
      if (el && wasPlayingRef.current) {
        // play() may return undefined in non-standard environments.
        void Promise.resolve(el.play()).catch(() => {
          /* autoplay blocked — controls remain */
        });
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
      setStatus((prev) => (prev === "playing" ? "ready" : prev));
      log("pause");
    },
    onTimeUpdate: () => {
      const el = mediaRef.current;
      if (!el) return;
      lastGoodTimeRef.current = el.currentTime;
      lastProgressAtRef.current = Date.now();
      setBuffering(false);
      // A recovery that sustains playback re-arms the attempt budget.
      if (
        attemptsRef.current > 0 &&
        lastRecoveryAtRef.current !== null &&
        Date.now() - lastRecoveryAtRef.current > RECOVERY_STABLE_MS
      ) {
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
    onSeeking: () => {
      const el = mediaRef.current;
      if (
        el &&
        pendingSeekRef.current !== null &&
        Math.abs(el.currentTime - pendingSeekRef.current) > 5
      ) {
        // The user scrubbed somewhere else; their position wins over a queued resume.
        pendingSeekRef.current = null;
      }
      log("seeking");
    },
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
      const kind = classifyMediaError(code, hasDecodedProgress(el));
      // ABORTED fires for our own close/reload — never a real failure.
      if (kind === "aborted") return;
      const message = mediaError?.message ?? null;
      setError({ code, message });
      log("error", `code ${code ?? "?"}${message ? `: ${message}` : ""}`);
      if (kind === "unsupported") {
        handleUnsupported(code);
        return;
      }
      // Code 3 lands here only when frames already decoded — name the
      // real trigger so the event log and diagnostics stay truthful.
      beginRecovery(
        code === 3
          ? `decode error after playback progress${message ? ` (${message})` : ""}`
          : "network error",
      );
    },
  };

  return {
    mediaRef,
    setMediaEl,
    handlers,
    status,
    mode,
    hlsAvailable,
    buffering,
    attempts,
    maxAttempts: MAX_AUTO_ATTEMPTS,
    error,
    startupMs,
    events,
    unavailableStatus,
    retry,
    activateCompatibilityMode,
    activateDirectMode,
    lastGoodTimeRef,
    lastProgressAtRef,
  };
}

function formatSeekDetail(time: number): string {
  return `to ${time.toFixed(1)}s`;
}

/** null when the element cannot report frame counts (audio, older engines). */
function totalVideoFrames(el: HTMLMediaElement | null): number | null {
  if (!(el instanceof HTMLVideoElement) || typeof el.getVideoPlaybackQuality !== "function")
    return null;
  return el.getVideoPlaybackQuality().totalVideoFrames;
}

export type MediaPlayer = ReturnType<typeof useMediaPlayer>;
