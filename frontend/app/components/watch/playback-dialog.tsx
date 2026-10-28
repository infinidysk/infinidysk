import { useEffect, useState } from "react";
import { Alert, Button, Icon, Modal } from "~/components/ui";
import { useMediaPlayer } from "~/components/media-player/use-media-player";
import { PlayerOverlay } from "~/components/watch/player/player-overlay";
import { createCatalogPlayback } from "~/clients/catalog-client";
import type {
  CatalogPlayback,
  CatalogPlaybackRequest,
  CatalogTitleType,
} from "~/clients/catalog-contract";

export type PlaybackTarget = {
  type: CatalogTitleType;
  tmdbId: number;
  season?: number;
  episode?: number;
  /** Dialog title: the movie name, or `S01E02 · Episode name`. */
  label: string;
};

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
  const [playback, setPlayback] = useState<CatalogPlayback | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [finding, setFinding] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    setFinding(true);
    setError(null);
    setPlayback(null);

    const request: CatalogPlaybackRequest = {
      type: target.type,
      tmdbId: target.tmdbId,
      ...(target.season !== undefined ? { season: target.season } : {}),
      ...(target.episode !== undefined ? { episode: target.episode } : {}),
    };

    createCatalogPlayback(request, controller.signal)
      .then((result) => {
        if (controller.signal.aborted) return;
        setPlayback(result);
        setFinding(false);
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted) return;
        setError(cause instanceof Error ? cause.message : "Could not start playback.");
        setFinding(false);
      });

    return () => controller.abort();
  }, [target.type, target.tmdbId, target.season, target.episode, attempt]);

  if (playback) {
    return <CatalogPlayer playback={playback} label={target.label} onClose={onClose} />;
  }

  return (
    <Modal open title={target.label} onClose={onClose} size="wide">
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
  label,
  onClose,
}: {
  playback: CatalogPlayback;
  label: string;
  onClose: () => void;
}) {
  const player = useMediaPlayer({ src: playback.directUrl, hlsFallbackSrc: playback.hlsUrl });
  return (
    <PlayerOverlay
      player={player}
      title={label}
      releaseTitle={playback.releaseTitle}
      onClose={onClose}
    />
  );
}
