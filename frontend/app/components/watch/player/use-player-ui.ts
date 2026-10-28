import { useEffect, useState } from "react";
import type { RefObject } from "react";

export type PlayerUiState = {
  time: number;
  duration: number;
  bufferedEnd: number;
  playing: boolean;
  volume: number;
  muted: boolean;
  rate: number;
};

const EMPTY: PlayerUiState = {
  time: 0,
  duration: 0,
  bufferedEnd: 0,
  playing: false,
  volume: 1,
  muted: false,
  rate: 1,
};

const EVENTS = [
  "timeupdate",
  "progress",
  "durationchange",
  "loadedmetadata",
  "play",
  "pause",
  "playing",
  "waiting",
  "seeking",
  "seeked",
  "volumechange",
  "ratechange",
  "ended",
  "emptied",
] as const;

/** Best buffered edge at or after the playhead; 0 when the playhead is unbuffered. */
function readBufferedEnd(el: HTMLMediaElement): number {
  try {
    let end = 0;
    for (let index = 0; index < el.buffered.length; index += 1) {
      if (el.buffered.start(index) <= el.currentTime + 0.5) {
        end = Math.max(end, el.buffered.end(index));
      }
    }
    return end;
  } catch {
    // jsdom and some embedded engines do not expose TimeRanges.
    return 0;
  }
}

function readState(el: HTMLMediaElement): PlayerUiState {
  return {
    time: Number.isFinite(el.currentTime) ? el.currentTime : 0,
    duration: Number.isFinite(el.duration) && el.duration > 0 ? el.duration : 0,
    bufferedEnd: readBufferedEnd(el),
    playing: !el.paused && !el.ended,
    volume: Number.isFinite(el.volume) ? el.volume : 1,
    muted: el.muted,
    rate: el.playbackRate || 1,
  };
}

/**
 * On-screen-display state for the Watch player. The media element is owned by
 * `useMediaPlayer`; this only mirrors what the browser reports so the controls can
 * render, which keeps the shared player hook free of per-frame UI concerns.
 *
 * The element is attached by the same commit that renders the hook, so reading the
 * ref in a mount effect is reliable.
 */
export function usePlayerUi(mediaRef: RefObject<HTMLMediaElement | null>): PlayerUiState {
  const [state, setState] = useState<PlayerUiState>(EMPTY);

  useEffect(() => {
    const el = mediaRef.current;
    if (!el) return;

    const sync = () => setState(readState(el));
    sync();
    for (const name of EVENTS) el.addEventListener(name, sync);
    return () => {
      for (const name of EVENTS) el.removeEventListener(name, sync);
    };
  }, [mediaRef]);

  return state;
}
