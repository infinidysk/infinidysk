import { Alert, Button, Icon } from "~/components/ui";
import { formatClock } from "./media-utils";
import type { MediaPlayer } from "./use-media-player";

export type StatusBannerProps = {
  player: MediaPlayer;
  /** Mime type shown in the unsupported copy; empty for streamed catalog titles. */
  mimeType: string;
};

/**
 * Player status line shared by the Explore preview and the Watch player. The
 * Watch player passes an HLS fallback, which adds the mode-switch action.
 */
export function StatusBanner({ player, mimeType }: StatusBannerProps) {
  switch (player.status) {
    case "loading":
      return (
        <div className="flex items-center gap-2 text-sm text-base-content/60" role="status">
          <span className="loading loading-spinner loading-xs" />
          Loading…
        </div>
      );
    case "transcoding":
      return (
        <div className="flex items-center gap-2 text-sm text-base-content/60" role="status">
          <span className="loading loading-spinner loading-xs" />
          Converting for this browser…
        </div>
      );
    case "ready":
      return null;
    case "playing":
      return player.buffering ? (
        <div className="flex items-center gap-2 text-sm text-base-content/60" role="status">
          <span className="loading loading-spinner loading-xs" />
          Buffering…
        </div>
      ) : null;
    case "recovering":
      return (
        <Alert variant="warning" className="alert-soft py-2 text-sm" role="status">
          <span className="loading loading-spinner loading-xs" />
          Stream interrupted — resuming
          {player.lastGoodTimeRef.current > 0
            ? ` from ${formatClock(player.lastGoodTimeRef.current)}`
            : ""}{" "}
          (attempt {player.attempts}/{player.maxAttempts})
        </Alert>
      );
    case "failed":
      return (
        <Alert variant="danger" className="alert-soft py-2 text-sm" role="alert">
          <Icon name="error" className="!text-[18px]" />
          <span>
            Playback failed after {player.maxAttempts} attempts
            {player.error?.message ? ` — ${player.error.message}` : ""}.
          </span>
          <Button variant="danger" size="xsmall" onClick={player.retry}>
            <Icon name="refresh" className="!text-[16px]" />
            Retry
          </Button>
        </Alert>
      );
    case "unsupported":
      return (
        <Alert variant="warning" className="alert-soft py-2 text-sm" role="alert">
          <Icon name="videocam_off" className="!text-[18px]" />
          <span>
            This browser could not play {mimeType || "this stream"}
            {player.error?.message ? ` (${player.error.message})` : ""}.{" "}
            {player.hlsAvailable
              ? player.mode === "direct"
                ? "Compatibility mode converts it for this browser."
                : "The direct stream may still play."
              : "The container or one of its streams may be unsupported here — use Open direct or Download and play it in an external player."}
          </span>
          {player.hlsAvailable && (
            <Button
              variant="secondary"
              size="xsmall"
              onClick={
                player.mode === "direct"
                  ? player.activateCompatibilityMode
                  : player.activateDirectMode
              }
            >
              <Icon name="transform" className="!text-[16px]" />
              {player.mode === "direct" ? "Compatibility mode" : "Direct stream"}
            </Button>
          )}
          {/* Never a dead end: a rejected stream can be retried without closing the player. */}
          <Button variant="warning" size="xsmall" onClick={player.retry}>
            <Icon name="refresh" className="!text-[16px]" />
            Retry
          </Button>
        </Alert>
      );
    case "unavailable":
      return (
        <Alert variant="danger" className="alert-soft py-2 text-sm" role="alert">
          <Icon name="error" className="!text-[18px]" />
          <span>
            The server refused to serve this file
            {player.unavailableStatus !== null ? ` (HTTP ${player.unavailableStatus})` : ""}. Close
            the preview and try again, or use Download.
          </span>
        </Alert>
      );
    case "missing-payload":
      return (
        <Alert variant="danger" className="alert-soft py-2 text-sm" role="alert">
          <Icon name="error" className="!text-[18px]" />
          <span>
            This file cannot be served: its streaming data is missing from the server (often a
            database restore without the blobs/ folder). Remove and re-download the release, or
            restore from a backup that includes blobs.
          </span>
        </Alert>
      );
  }
}
