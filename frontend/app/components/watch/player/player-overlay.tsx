import { useCallback, useEffect, useRef, useState } from "react";
import { Icon } from "~/components/ui";
import { StatusBanner } from "~/components/media-player/status-banner";
import { formatClock } from "~/components/media-player/media-utils";
import type { MediaPlayer } from "~/components/media-player/use-media-player";
import { usePlayerUi } from "./use-player-ui";

/** Jellyfin-style idle window before the chrome fades out during playback. */
const HIDE_DELAY_MS = 2500;
const SKIP_BACK_SECONDS = 10;
const SKIP_FORWARD_SECONDS = 30;
const VOLUME_STEP = 0.05;
const RATES = [0.75, 1, 1.25, 1.5, 2] as const;

// Painting translucent fills over the video forces a full-area composite every frame,
// which drops frames in browsers without GPU compositing. Chrome therefore carries no
// background; contrast comes from text shadows, which are rasterised once.
const CHROME_SHADOW = {
  textShadow: "0 1px 3px rgba(0, 0, 0, 0.95), 0 2px 10px rgba(0, 0, 0, 0.7)",
};

const RING = "focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white";
const CHROME_BUTTON = `grid h-10 w-10 place-items-center rounded-full text-white/75 transition hover:bg-white/10 hover:text-white ${RING}`;

const FAILURE_STATUSES: Record<string, true> = {
  recovering: true,
  failed: true,
  unsupported: true,
  unavailable: true,
  "missing-payload": true,
};

export type PlayerOverlayProps = {
  player: MediaPlayer;
  /** Item title shown in the top bar. */
  title: string;
  /** Release name the stream was resolved from. */
  releaseTitle: string;
  onClose: () => void;
  /**
   * Episode stepping for series. Omitted for movies; a null handler means the player is
   * at the first/last episode of the loaded season, so that button renders disabled.
   */
  episodeNav?:
    | {
        onPrevious: (() => void) | null;
        onNext: (() => void) | null;
      }
    | undefined;
};

/**
 * Full-screen player chrome: the video fills the screen and an on-screen display
 * carries the title, transport, scrub bar, volume, speed, mode switch and fullscreen.
 * The chrome fades while playback runs and returns on pointer or keyboard activity.
 * Keyboard: space/k play, j/l or arrows seek, up/down volume, m mute, f fullscreen,
 * 0-9 jump, [ / ] previous/next episode, Escape fullscreen/close.
 */
export function PlayerOverlay({
  player,
  title,
  releaseTitle,
  onClose,
  episodeNav,
}: PlayerOverlayProps) {
  const shellRef = useRef<HTMLDivElement>(null);
  const rateMenuRef = useRef<HTMLDivElement>(null);
  const hideTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const clickTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const ui = usePlayerUi(player.mediaRef);

  const [osdVisible, setOsdVisible] = useState(true);
  const [rateMenuOpen, setRateMenuOpen] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [pipAvailable, setPipAvailable] = useState(false);
  const [statsOpen, setStatsOpen] = useState(false);
  const [stats, setStats] = useState<PlaybackStats | null>(null);
  const stallsRef = useRef(0);

  const loading = player.status === "loading" || player.status === "transcoding";
  const failure = FAILURE_STATUSES[player.status] === true;

  const media = useCallback(() => player.mediaRef.current, [player.mediaRef]);

  // Count rebuffer events for the whole session, independent of the stats panel.
  useEffect(() => {
    const el = player.mediaRef.current;
    if (!el) return;
    const onWaiting = () => {
      stallsRef.current += 1;
    };
    el.addEventListener("waiting", onWaiting);
    return () => el.removeEventListener("waiting", onWaiting);
  }, [player.mediaRef]);

  // Playback data mirrors what the engine reports, so a stutter can be attributed:
  // climbing dropped frames means client decode, stalls and a falling buffer mean the
  // stream cannot be produced fast enough.
  useEffect(() => {
    if (!statsOpen) return;
    const sample = () => {
      const el = player.mediaRef.current;
      if (!el) return;
      const video = el instanceof HTMLVideoElement ? el : null;
      const quality = video?.getVideoPlaybackQuality?.();
      let lastSegmentMs: number | null = null;
      let segments = 0;
      try {
        const entries = performance
          .getEntriesByType("resource")
          .filter((entry) => /\/segments\/\d+\.ts/.test(entry.name));
        segments = entries.length;
        const last = entries[entries.length - 1];
        lastSegmentMs = last ? Math.round(last.duration) : null;
      } catch {
        // Resource timing is unavailable in some engines.
      }
      const ahead =
        el.buffered.length > 0 ? el.buffered.end(el.buffered.length - 1) - el.currentTime : 0;
      setStats({
        mode: player.mode,
        width: video?.videoWidth ?? 0,
        height: video?.videoHeight ?? 0,
        ahead: Math.max(0, ahead),
        dropped: quality?.droppedVideoFrames ?? 0,
        frames: quality?.totalVideoFrames ?? 0,
        stalls: stallsRef.current,
        segments,
        lastSegmentMs,
        error: player.error?.message ?? null,
      });
    };
    sample();
    const timer = setInterval(sample, 500);
    return () => clearInterval(timer);
  }, [player.error?.message, player.mediaRef, player.mode, statsOpen]);

  const togglePlay = useCallback(() => {
    const el = media();
    if (!el) return;
    if (el.paused || el.ended) {
      void Promise.resolve(el.play()).catch(() => {
        /* autoplay blocked — controls remain */
      });
    } else {
      el.pause();
    }
  }, [media]);

  const seekTo = useCallback(
    (time: number) => {
      const el = media();
      if (!el) return;
      const total = Number.isFinite(el.duration) && el.duration > 0 ? el.duration : ui.duration;
      const upper = total > 0 ? total - 0.25 : 0;
      el.currentTime = Math.max(0, Math.min(time, upper));
    },
    [media, ui.duration],
  );

  const seekBy = useCallback(
    (delta: number) => seekTo((media()?.currentTime ?? 0) + delta),
    [media, seekTo],
  );

  const setVolume = useCallback(
    (value: number) => {
      const el = media();
      if (!el) return;
      const next = Math.max(0, Math.min(1, value));
      el.volume = next;
      el.muted = next === 0;
    },
    [media],
  );

  const toggleMute = useCallback(() => {
    const el = media();
    if (!el) return;
    el.muted = !el.muted;
  }, [media]);

  const setRate = useCallback(
    (rate: number) => {
      const el = media();
      if (!el) return;
      el.playbackRate = rate;
      setRateMenuOpen(false);
    },
    [media],
  );

  const toggleFullscreen = useCallback(async () => {
    const node = shellRef.current;
    try {
      if (document.fullscreenElement) {
        if (document.exitFullscreen) await document.exitFullscreen();
        return;
      }
      if (node?.requestFullscreen) await node.requestFullscreen();
    } catch {
      // Denied by the browser; nothing to restore.
    }
  }, []);

  const togglePip = useCallback(async () => {
    const el = media();
    if (!(el instanceof HTMLVideoElement) || !document.pictureInPictureEnabled) return;
    try {
      if (document.pictureInPictureElement) await document.exitPictureInPicture();
      else await el.requestPictureInPicture();
    } catch {
      // Picture-in-picture is optional.
    }
  }, [media]);

  const bump = useCallback(() => {
    setOsdVisible(true);
    clearTimeout(hideTimerRef.current);
    if (ui.playing && !rateMenuOpen) {
      hideTimerRef.current = setTimeout(() => setOsdVisible(false), HIDE_DELAY_MS);
    }
  }, [rateMenuOpen, ui.playing]);

  // A speed menu is a transient popover: clicking anywhere else dismisses it.
  useEffect(() => {
    if (!rateMenuOpen) return;
    const onPointerDown = (event: PointerEvent) => {
      if (rateMenuRef.current?.contains(event.target as Node)) return;
      setRateMenuOpen(false);
    };
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [rateMenuOpen]);

  // Idle hide only while playing; pausing or opening a menu pins the chrome.
  useEffect(() => {
    if (!ui.playing || rateMenuOpen) {
      clearTimeout(hideTimerRef.current);
      setOsdVisible(true);
      return;
    }
    bump();
    return () => clearTimeout(hideTimerRef.current);
  }, [bump, rateMenuOpen, ui.playing]);

  useEffect(() => {
    if (typeof document === "undefined") return;
    setPipAvailable(document.pictureInPictureEnabled === true);
    const sync = () => setFullscreen(document.fullscreenElement === shellRef.current);
    document.addEventListener("fullscreenchange", sync);
    sync();
    return () => document.removeEventListener("fullscreenchange", sync);
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      // Leave typing and native range dragging alone.
      if (target && /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return;

      switch (event.key) {
        case " ":
        case "k":
          event.preventDefault();
          togglePlay();
          break;
        case "ArrowLeft":
        case "j":
          event.preventDefault();
          seekBy(-SKIP_BACK_SECONDS);
          break;
        case "ArrowRight":
        case "l":
          event.preventDefault();
          seekBy(SKIP_FORWARD_SECONDS);
          break;
        case "ArrowUp":
          event.preventDefault();
          setVolume((media()?.volume ?? 1) + VOLUME_STEP);
          break;
        case "ArrowDown":
          event.preventDefault();
          setVolume((media()?.volume ?? 1) - VOLUME_STEP);
          break;
        case "m":
          toggleMute();
          break;
        case "i":
          setStatsOpen((open) => !open);
          break;
        case "f":
          void toggleFullscreen();
          break;
        case "[":
          if (episodeNav?.onPrevious) {
            event.preventDefault();
            episodeNav.onPrevious();
          }
          break;
        case "]":
          if (episodeNav?.onNext) {
            event.preventDefault();
            episodeNav.onNext();
          }
          break;
        case "Escape":
          // Menus dismiss first, then fullscreen, and only then the player.
          if (rateMenuOpen) setRateMenuOpen(false);
          else if (document.fullscreenElement) void document.exitFullscreen();
          else onClose();
          break;
        default:
          if (/^[0-9]$/.test(event.key)) seekTo((ui.duration * Number(event.key)) / 10);
      }
      bump();
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [
    bump,
    episodeNav,
    media,
    onClose,
    rateMenuOpen,
    seekBy,
    seekTo,
    setVolume,
    toggleFullscreen,
    toggleMute,
    togglePlay,
    ui.duration,
  ]);

  // Click plays or pauses; a second click inside the window is a double-click
  // asking for fullscreen, so the pending toggle is cancelled.
  const handleSurfaceClick = () => {
    if (clickTimerRef.current !== undefined) {
      clearTimeout(clickTimerRef.current);
      clickTimerRef.current = undefined;
      return;
    }
    clickTimerRef.current = setTimeout(() => {
      clickTimerRef.current = undefined;
      togglePlay();
    }, 220);
  };

  const handleSurfaceDoubleClick = () => {
    clearTimeout(clickTimerRef.current);
    clickTimerRef.current = undefined;
    void toggleFullscreen();
  };

  useEffect(() => {
    return () => {
      clearTimeout(clickTimerRef.current);
      clearTimeout(hideTimerRef.current);
    };
  }, []);

  const chromeVisible = osdVisible || !ui.playing || loading || failure || rateMenuOpen;
  const volume = ui.muted ? 0 : ui.volume;
  const modeLabel = player.mode === "hls" ? "Direct stream" : "Compatibility mode";

  // The pointer belongs to the chrome, so it disappears with it. A fullscreen player that
  // leaves the cursor parked over the video is the artifact viewers notice first.
  return (
    <div
      ref={shellRef}
      data-osd={chromeVisible ? "visible" : "hidden"}
      className={`fixed inset-0 z-[100] overflow-hidden bg-black text-white ${
        chromeVisible ? "" : "cursor-none"
      }`}
      onPointerMove={bump}
      onPointerDown={bump}
    >
      <video
        ref={player.setMediaEl}
        {...player.handlers}
        playsInline
        preload="metadata"
        aria-label={title}
        onClick={handleSurfaceClick}
        onDoubleClick={handleSurfaceDoubleClick}
        className="absolute inset-0 h-full w-full bg-black object-contain"
      />

      {(player.buffering || loading) && (
        <div className="pointer-events-none absolute inset-0 grid place-items-center" role="status">
          <span className="loading loading-spinner loading-lg text-white/80" aria-label="Loading" />
        </div>
      )}

      {!ui.playing && !loading && !failure && (
        <button
          type="button"
          onClick={togglePlay}
          aria-label="Play"
          className={`absolute top-1/2 left-1/2 grid h-20 w-20 -translate-x-1/2 -translate-y-1/2 place-items-center rounded-full bg-black/55 text-white shadow-lg transition hover:scale-105 hover:bg-black/70 ${RING}`}
        >
          <Icon name="play_arrow" className="!text-[46px]" />
        </button>
      )}

      {/* Chrome sits on compact pills rather than full-width translucent bands: a large
          gradient over the video forces a per-frame software blend, which drops frames in
          browsers without GPU compositing. */}
      <div
        className={`absolute inset-x-0 top-0 z-10 flex items-start justify-between gap-3 px-3 pt-3 transition-opacity duration-300 ${
          chromeVisible ? "opacity-100" : "pointer-events-none opacity-0"
        }`}
      >
        <div className="flex min-w-0 items-center gap-2" style={CHROME_SHADOW}>
          <button
            type="button"
            onClick={onClose}
            aria-label="Exit player"
            className={CHROME_BUTTON}
          >
            <Icon name="arrow_back" className="!text-[22px]" />
          </button>
          <div className="min-w-0">
            <p className="truncate text-base leading-tight font-semibold">{title}</p>
            <p className="truncate text-xs text-white/55" title={releaseTitle}>
              {releaseTitle}
            </p>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-1" style={CHROME_SHADOW}>
          <button
            type="button"
            onClick={() => setStatsOpen((open) => !open)}
            aria-label="Playback data"
            aria-pressed={statsOpen}
            title="Playback data"
            className={`${CHROME_BUTTON} max-sm:hidden`}
          >
            <Icon name="monitoring" className="!text-[22px]" />
          </button>
          {player.hlsAvailable && (
            <button
              type="button"
              onClick={
                player.mode === "hls" ? player.activateDirectMode : player.activateCompatibilityMode
              }
              aria-label={modeLabel}
              title={
                player.mode === "hls"
                  ? "Play the original file directly; no conversion"
                  : "Convert to H.264/AAC with HLS"
              }
              className={`flex h-9 items-center gap-1.5 rounded-full px-3 text-xs font-medium text-white/75 transition hover:bg-white/10 hover:text-white ${RING}`}
            >
              <Icon name={player.mode === "hls" ? "link" : "transform"} className="!text-[18px]" />
              <span className="hidden sm:inline">
                {player.mode === "hls" ? "Direct" : "Compat"}
              </span>
            </button>
          )}
          <button type="button" onClick={onClose} aria-label="Close" className={CHROME_BUTTON}>
            <Icon name="close" className="!text-[22px]" />
          </button>
        </div>
      </div>

      <div
        className={`absolute inset-x-0 bottom-0 z-10 px-3 pb-3 transition-opacity duration-300 ${
          chromeVisible ? "opacity-100" : "pointer-events-none opacity-0"
        }`}
      >
        {failure && (
          <div className="mx-auto mb-3 w-[min(100%,40rem)] text-base-content">
            <StatusBanner player={player} mimeType="this stream" />
          </div>
        )}

        <div className="px-3 py-2" style={CHROME_SHADOW}>
          <div className="flex items-center gap-3">
            <span className="w-12 shrink-0 text-right font-mono text-xs tabular-nums text-white/70">
              {formatClock(ui.time)}
            </span>
            <Timeline
              time={ui.time}
              duration={ui.duration}
              bufferedEnd={ui.bufferedEnd}
              onSeek={seekTo}
            />
            <span className="w-12 shrink-0 font-mono text-xs tabular-nums text-white/70">
              {formatClock(ui.duration)}
            </span>
          </div>

          <div className="mt-1 flex items-center justify-between gap-2">
            <div className="flex items-center gap-0.5">
              {episodeNav && (
                <button
                  type="button"
                  onClick={episodeNav.onPrevious ?? undefined}
                  disabled={!episodeNav.onPrevious}
                  aria-label="Previous episode"
                  title="Previous episode"
                  className={`${CHROME_BUTTON} disabled:pointer-events-none disabled:opacity-30`}
                >
                  <Icon name="skip_previous" className="!text-[22px]" />
                </button>
              )}
              <button
                type="button"
                onClick={() => seekBy(-SKIP_BACK_SECONDS)}
                aria-label={`Back ${SKIP_BACK_SECONDS} seconds`}
                title={`Back ${SKIP_BACK_SECONDS}s`}
                className={CHROME_BUTTON}
              >
                <Icon name="replay_10" className="!text-[24px]" />
              </button>
              <button
                type="button"
                onClick={togglePlay}
                aria-label={ui.playing ? "Pause" : "Play"}
                className={`${CHROME_BUTTON} h-11 w-11`}
              >
                <Icon name={ui.playing ? "pause" : "play_arrow"} className="!text-[30px]" />
              </button>
              <button
                type="button"
                onClick={() => seekBy(SKIP_FORWARD_SECONDS)}
                aria-label={`Forward ${SKIP_FORWARD_SECONDS} seconds`}
                title={`Forward ${SKIP_FORWARD_SECONDS}s`}
                className={CHROME_BUTTON}
              >
                <Icon name="forward_30" className="!text-[24px]" />
              </button>
              {episodeNav && (
                <button
                  type="button"
                  onClick={episodeNav.onNext ?? undefined}
                  disabled={!episodeNav.onNext}
                  aria-label="Next episode"
                  title="Next episode"
                  className={`${CHROME_BUTTON} disabled:pointer-events-none disabled:opacity-30`}
                >
                  <Icon name="skip_next" className="!text-[22px]" />
                </button>
              )}
              <div className="ml-1 flex items-center gap-1">
                <button
                  type="button"
                  onClick={toggleMute}
                  aria-label={ui.muted || ui.volume === 0 ? "Unmute" : "Mute"}
                  className={CHROME_BUTTON}
                >
                  <Icon
                    name={ui.muted || ui.volume === 0 ? "volume_off" : "volume_up"}
                    className="!text-[22px]"
                  />
                </button>
                <input
                  type="range"
                  min={0}
                  max={1}
                  step={VOLUME_STEP}
                  value={volume}
                  onChange={(event) => setVolume(Number(event.target.value))}
                  aria-label="Volume"
                  className="h-1 w-20 cursor-pointer accent-white max-sm:hidden"
                />
              </div>
            </div>

            <div className="flex items-center gap-0.5">
              <div ref={rateMenuRef} className="relative">
                <button
                  type="button"
                  onClick={() => setRateMenuOpen((open) => !open)}
                  aria-label={`Playback speed, ${ui.rate}x`}
                  aria-expanded={rateMenuOpen}
                  className={`${CHROME_BUTTON} w-auto px-2 text-xs font-semibold`}
                >
                  {ui.rate}×
                </button>
                {rateMenuOpen && (
                  <div
                    role="menu"
                    className="absolute right-0 bottom-12 w-28 rounded-lg bg-black/95 p-1 shadow-xl ring-1 ring-white/15"
                  >
                    {RATES.map((rate) => (
                      <button
                        key={rate}
                        type="button"
                        role="menuitemradio"
                        aria-checked={ui.rate === rate}
                        onClick={() => setRate(rate)}
                        className="flex w-full items-center justify-between rounded px-3 py-1.5 text-left text-sm text-white/85 hover:bg-white/10"
                      >
                        {rate}×{ui.rate === rate && <Icon name="check" className="!text-[16px]" />}
                      </button>
                    ))}
                  </div>
                )}
              </div>
              {pipAvailable && (
                <button
                  type="button"
                  onClick={() => void togglePip()}
                  aria-label="Picture in picture"
                  title="Picture in picture"
                  className={`${CHROME_BUTTON} max-sm:hidden`}
                >
                  <Icon name="picture_in_picture_alt" className="!text-[22px]" />
                </button>
              )}
              <button
                type="button"
                onClick={() => void toggleFullscreen()}
                aria-label={fullscreen ? "Exit fullscreen" : "Fullscreen"}
                title={fullscreen ? "Exit fullscreen" : "Fullscreen"}
                className={CHROME_BUTTON}
              >
                <Icon
                  name={fullscreen ? "fullscreen_exit" : "fullscreen"}
                  className="!text-[24px]"
                />
              </button>
            </div>
          </div>
        </div>
      </div>

      {statsOpen && stats && <StatsPanel stats={stats} releaseTitle={releaseTitle} />}
    </div>
  );
}

type PlaybackStats = {
  mode: "direct" | "hls";
  width: number;
  height: number;
  ahead: number;
  dropped: number;
  frames: number;
  stalls: number;
  segments: number;
  lastSegmentMs: number | null;
  error: string | null;
};

/** Jellyfin-style playback data: enough to tell decode problems from buffering. */
function StatsPanel({ stats, releaseTitle }: { stats: PlaybackStats; releaseTitle: string }) {
  const rows: [string, string][] = [
    ["Mode", stats.mode === "hls" ? "HLS (converted)" : "Direct stream"],
    ["Video", stats.width > 0 ? `${stats.width}×${stats.height}` : "—"],
    ["Buffer", `${stats.ahead.toFixed(1)} s ahead`],
    ["Frames", `${stats.dropped} dropped / ${stats.frames}`],
    ["Stalls", String(stats.stalls)],
    [
      "Segments",
      stats.lastSegmentMs === null ? "—" : `${stats.segments} · last ${stats.lastSegmentMs} ms`,
    ],
    ["Error", stats.error ?? "—"],
  ];

  return (
    <div
      data-testid="playback-data"
      className="pointer-events-none absolute top-16 right-3 z-30 w-72 rounded-lg bg-black/90 p-3 font-mono text-[11px] leading-5 text-white/75 shadow-lg ring-1 ring-white/15"
    >
      <p className="mb-1 font-sans text-xs font-semibold text-white">Playback data</p>
      <p className="mb-2 truncate font-sans text-[11px] text-white/45" title={releaseTitle}>
        {releaseTitle}
      </p>
      <dl className="grid grid-cols-[5.5rem_1fr] gap-x-2">
        {rows.map(([label, value]) => (
          <div key={label} className="contents">
            <dt className="text-white/45">{label}</dt>
            <dd
              className={value === "—" ? "text-white/35" : "truncate text-white/85"}
              title={value}
            >
              {value}
            </dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

type TimelineProps = {
  time: number;
  duration: number;
  bufferedEnd: number;
  onSeek: (time: number) => void;
};

/** Buffered rail, played rail and a native range for pointer and keyboard scrubbing. */
function Timeline({ time, duration, bufferedEnd, onSeek }: TimelineProps) {
  const percent = (value: number) => (duration > 0 ? Math.min(100, (value / duration) * 100) : 0);

  return (
    <div className="group/timeline relative h-5 flex-1">
      <div className="absolute inset-x-0 top-1/2 h-1 -translate-y-1/2 rounded-full bg-white/20">
        <div
          className="absolute inset-y-0 left-0 rounded-full bg-white/35"
          style={{ width: `${percent(bufferedEnd)}%` }}
        />
        <div
          className="absolute inset-y-0 left-0 rounded-full bg-primary"
          style={{ width: `${percent(time)}%` }}
        />
      </div>
      <div
        className="pointer-events-none absolute top-1/2 h-3 w-3 -translate-x-1/2 -translate-y-1/2 rounded-full bg-primary opacity-0 shadow transition group-hover/timeline:opacity-100 group-focus-within/timeline:opacity-100"
        style={{ left: `${percent(time)}%` }}
      />
      <input
        type="range"
        min={0}
        max={Math.max(duration, 0)}
        step={0.1}
        value={Math.min(time, duration || 0)}
        onChange={(event) => onSeek(Number(event.target.value))}
        aria-label="Seek"
        aria-valuetext={`${formatClock(time)} of ${formatClock(duration)}`}
        className="absolute inset-0 h-full w-full cursor-pointer opacity-0"
      />
    </div>
  );
}
