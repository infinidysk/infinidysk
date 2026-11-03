import { useEffect, useRef, useState } from "react";
import { Alert, Button, Icon, Modal } from "~/components/ui";
import { useMediaPlayer } from "~/components/media-player/use-media-player";
import { PlayerOverlay } from "~/components/watch/player/player-overlay";
import { ProgressReporter } from "~/components/watch/player/progress-reporter";
import { createCatalogPlayback, fetchCatalogSeason } from "~/clients/catalog-client";
import type {
  CatalogPlayback,
  CatalogPlaybackRequest,
  CatalogTitleType,
} from "~/clients/catalog-contract";

export type PlaybackEpisode = {
  season: number;
  episode: number;
  name: string;
};

export type PlaybackTarget = {
  type: CatalogTitleType;
  tmdbId: number;
  season?: number;
  episode?: number;
  /** Dialog title: the movie name, or `S01E02 · Episode name`. */
  label: string;
  /**
   * The season's episodes in air order. Series targets carry it so the player can step
   * back and forth and autoplay the next episode without a page reload.
   */
  lineup?: readonly PlaybackEpisode[];
  /** Season numbers of the title, so a season finale can roll into the next season. */
  seasons?: readonly number[] | undefined;
};

/** What a series target needs: the title, its seasons, and the loaded season's order. */
export type PlaybackLineup = {
  tmdbId: number;
  seasons?: readonly number[] | undefined;
  episodes: readonly PlaybackEpisode[];
};

/** `S01E02 · Episode name`, shared by the title page list and the player's title bar. */
export function episodeLabel(season: number, episode: number, name: string): string {
  return `S${String(season).padStart(2, "0")}E${String(episode).padStart(2, "0")} · ${name}`;
}

/** Playback target for one episode, carrying the season order for navigation. */
export function episodeTarget(lineup: PlaybackLineup, selection: PlaybackEpisode): PlaybackTarget {
  return {
    type: "series",
    tmdbId: lineup.tmdbId,
    season: selection.season,
    episode: selection.episode,
    label: episodeLabel(selection.season, selection.episode, selection.name),
    lineup: lineup.episodes,
    ...(lineup.seasons !== undefined ? { seasons: lineup.seasons } : {}),
  };
}

export type PlaybackDialogProps = {
  target: PlaybackTarget;
  onClose: () => void;
};

/**
 * Finds a stream, then hands it to the full-screen player. Closing (or leaving the
 * route) aborts the in-flight search, and only one submission runs per open — a
 * duplicate click cannot submit a second playback session.
 */
export function PlaybackDialog({ target, onClose }: PlaybackDialogProps) {
  const [attempt, setAttempt] = useState(0);
  const [current, setCurrent] = useState<PlaybackTarget>(target);
  // The playback and the target it was resolved for move together, so the player never
  // renders a new episode against the previous stream while the next search is in flight.
  const [active, setActive] = useState<{
    playback: CatalogPlayback;
    target: PlaybackTarget;
  } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [finding, setFinding] = useState(true);
  const seasonFetch = useRef<AbortController | null>(null);

  useEffect(() => () => seasonFetch.current?.abort(), []);

  const lineup = current.lineup ?? [];
  const index = lineup.findIndex(
    (entry) => entry.season === current.season && entry.episode === current.episode,
  );
  const previous = index > 0 ? lineup[index - 1] : undefined;
  const next = index >= 0 ? lineup[index + 1] : undefined;

  // A finale has no next episode in this season, but the title may still have another
  // season; that is the only case where advancing needs a request.
  const seasonPool = current.seasons ?? [];
  const seasonIndex = current.season === undefined ? -1 : seasonPool.indexOf(current.season);
  const followingSeason = seasonIndex >= 0 ? seasonPool[seasonIndex + 1] : undefined;
  const canAdvance = next !== undefined || followingSeason !== undefined;

  useEffect(() => {
    const controller = new AbortController();
    setFinding(true);
    setError(null);
    setActive(null);

    const request: CatalogPlaybackRequest = {
      type: current.type,
      tmdbId: current.tmdbId,
      ...(current.season !== undefined ? { season: current.season } : {}),
      ...(current.episode !== undefined ? { episode: current.episode } : {}),
    };

    createCatalogPlayback(request, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return;
        setActive({ playback: result, target: current });
        setFinding(false);
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted) return;
        setError(cause instanceof Error ? cause.message : "Could not start playback.");
        setFinding(false);
      });

    return () => controller.abort();
  }, [current, attempt]);

  /** Switch episodes in place: the player remounts, resolves the new stream, autoplays. */
  const open = (selection: PlaybackEpisode, source?: PlaybackLineup) => {
    setActive(null);
    setCurrent(
      episodeTarget(
        source ?? { tmdbId: current.tmdbId, seasons: current.seasons, episodes: lineup },
        selection,
      ),
    );
  };

  /** Advance inside the season, or load the next season's first episode at a finale. */
  const advance = () => {
    if (next !== undefined) {
      open(next);
      return;
    }
    if (followingSeason === undefined) return;

    seasonFetch.current?.abort();
    const controller = new AbortController();
    seasonFetch.current = controller;

    void fetchCatalogSeason(current.tmdbId, followingSeason, controller.signal)
      .then((season) => {
        if (controller.signal.aborted) return;
        const episodes = season.episodes.map((entry) => ({
          season: entry.seasonNumber,
          episode: entry.episodeNumber,
          name: entry.name,
        }));
        const first = episodes[0];
        // An empty season is a dead end: keep the ended frame rather than jump nowhere.
        if (first === undefined) return;
        open(first, { tmdbId: current.tmdbId, seasons: seasonPool, episodes });
      })
      .catch(() => undefined);
  };

  if (active) {
    return (
      <CatalogPlayer
        playback={active.playback}
        target={active.target}
        label={active.target.label}
        onClose={onClose}
        episodeNav={
          active.target.lineup === undefined
            ? undefined
            : {
                onPrevious: previous ? () => open(previous) : null,
                onNext: canAdvance ? advance : null,
              }
        }
        onEnded={canAdvance ? advance : undefined}
      />
    );
  }

  return (
    <Modal open title={current.label} onClose={onClose} size="wide">
      <div className="flex flex-col gap-3">
        {finding && (
          <div className="flex items-center gap-2 text-sm text-base-content/60" role="status">
            <span className="loading loading-spinner loading-xs" />
            Finding a stream…
          </div>
        )}

        {error && (
          <Alert variant="danger" className="alert-soft py-2 text-sm" role="alert">
            <Icon name="error" className="!text-[18px]" />
            <span>{error}</span>
            <Button
              variant="danger"
              size="xsmall"
              onClick={() => setAttempt((current) => current + 1)}
            >
              <Icon name="refresh" className="!text-[16px]" />
              Retry
            </Button>
          </Alert>
        )}
      </div>
    </Modal>
  );
}

function CatalogPlayer({
  playback,
  target,
  label,
  onClose,
  episodeNav,
  onEnded,
}: {
  playback: CatalogPlayback;
  target: PlaybackTarget;
  label: string;
  onClose: () => void;
  episodeNav?:
    | {
        onPrevious: (() => void) | null;
        onNext: (() => void) | null;
      }
    | undefined;
  onEnded?: (() => void) | undefined;
}) {
  const player = useMediaPlayer({ src: playback.directUrl, hlsFallbackSrc: playback.hlsUrl });
  return (
    <>
      <PlayerOverlay
        player={player}
        title={label}
        releaseTitle={playback.releaseTitle}
        onClose={onClose}
        episodeNav={episodeNav}
      />
      <ProgressReporter player={player} target={target} onEnded={onEnded} />
    </>
  );
}
