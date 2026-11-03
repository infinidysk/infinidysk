import { useEffect, useRef } from "react";
import type { MediaPlayer } from "~/components/media-player/use-media-player";
import { reportWatchHistoryProgress } from "~/clients/catalog-client";
import type { CatalogTitleType } from "~/clients/catalog-contract";

export type ProgressTarget = {
  type: CatalogTitleType;
  tmdbId: number;
  season?: number;
  episode?: number;
};

export type ProgressReporterProps = {
  player: MediaPlayer;
  target: ProgressTarget;
  /** Called once the finished report is away — the player's next-episode hook. */
  onEnded?: (() => void) | undefined;
};

/** One report per HLS segment boundary, so each write covers whole transcoded segments. */
const REPORT_INTERVAL_MS = 10_000;

/**
 * Streams playback position to the watch-history endpoint. Reports are fire-and-forget:
 * a failure only costs resume accuracy and must never surface as a playback error.
 */
export function ProgressReporter({ player, target, onEnded }: ProgressReporterProps) {
  const { type, tmdbId, season, episode } = target;

  // Held in a ref so an inline callback from the player cannot restart the interval
  // (which would reset the report cadence on every render).
  const endedRef = useRef(onEnded);
  useEffect(() => {
    endedRef.current = onEnded;
  }, [onEnded]);

  useEffect(() => {
    const controller = new AbortController();
    let reportedPosition = 0;

    const readPosition = () => {
      const element = player.mediaRef.current;
      if (!element) return null;
      const position = Number.isFinite(element.currentTime) ? element.currentTime : 0;
      const duration =
        Number.isFinite(element.duration) && element.duration > 0 ? element.duration : null;
      return { position, duration };
    };

    const report = (finished: boolean, signal?: AbortSignal) => {
      const sample = readPosition();
      if (!sample) return;
      reportedPosition = sample.position;
      void reportWatchHistoryProgress(
        {
          type,
          tmdbId,
          ...(season !== undefined ? { season } : {}),
          ...(episode !== undefined ? { episode } : {}),
          position: sample.position,
          duration: sample.duration,
          finished,
        },
        signal,
      ).catch(() => undefined);
    };

    const timer = setInterval(() => report(false, controller.signal), REPORT_INTERVAL_MS);
    const element = player.mediaRef.current;
    const handleEnded = () => {
      // No abort signal: autoplay unmounts this reporter immediately, and the finished
      // write still has to land. The fetch is keepalive for the same reason.
      report(true);
      endedRef.current?.();
    };
    element?.addEventListener("ended", handleEnded);

    return () => {
      clearInterval(timer);
      element?.removeEventListener("ended", handleEnded);
      controller.abort();
      // Closing the dialog (the only close path is Escape/Exit) unmounts this reporter:
      // report the final position so the row never lags a whole interval behind.
      if (reportedPosition > 0) return;
      const sample = readPosition();
      if (!sample || sample.position <= 0) return;
      report(false);
    };
  }, [player.mediaRef, type, tmdbId, season, episode]);

  return null;
}
