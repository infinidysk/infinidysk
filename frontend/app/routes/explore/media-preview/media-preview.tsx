import { useMemo, useState } from "react";
import { Button, Icon, Modal } from "~/components/ui";
import { formatFileSize } from "~/utils/file-size";
import { generateUuid } from "~/utils/uuid";
import { isVideoFile } from "~/utils/file-kind";
import { MediaDiagnostics } from "./media-diagnostics";
import { appendQueryParam, buildMediaSrc } from "~/components/media-player/media-utils";
import { StatusBanner } from "~/components/media-player/status-banner";
import { useMediaPlayer } from "~/components/media-player/use-media-player";

export type MediaPreviewProps = {
  fileName: string;
  filePath: string;
  mimeType: string;
  sizeBytes: number | null;
  /** Signed /view URL (already includes downloadKey). */
  previewUrl: string;
  onClose: () => void;
};

/**
 * In-app native player for Explore. Mounted iff the preview is open — unmount
 * releases the media source, aborting the in-flight range request so the
 * backend stops pulling Usenet segments for a player nobody is watching.
 */
export function MediaPreview(props: MediaPreviewProps) {
  const { fileName, filePath, mimeType, sizeBytes, previewUrl, onClose } = props;

  // One non-secret correlation id per mounted player; stable across retries
  // so all range requests map to one backend read session.
  const [playerSession] = useState(() => generateUuid());
  const src = useMemo(() => buildMediaSrc(previewUrl, playerSession), [previewUrl, playerSession]);

  const player = useMediaPlayer({ src });
  const [showDiagnostics, setShowDiagnostics] = useState(false);

  const kind = isVideoFile({ name: fileName, mimeType }) ? "video" : "audio";
  const downloadUrl = appendQueryParam(previewUrl, "download", "true");

  // src is applied by the player hook's effect (see useMediaPlayer); a JSX
  // src attribute would be wiped by StrictMode's simulated unmount cycle.
  const mediaElementProps = {
    ref: player.setMediaEl,
    controls: true,
    playsInline: true,
    preload: "metadata" as const,
    ...player.handlers,
  };

  return (
    // Stable gutter: a toggling scrollbar resizes the video, which re-toggles the scrollbar.
    <Modal
      open
      title={fileName}
      onClose={onClose}
      size="wide"
      className="[scrollbar-gutter:stable]"
    >
      <div className="flex flex-col gap-3">
        <StatusBanner player={player} mimeType={mimeType} />

        {kind === "video" ? (
          <video
            {...mediaElementProps}
            className="max-h-[65dvh] w-full rounded-lg bg-black"
            aria-label={fileName}
          />
        ) : (
          <audio {...mediaElementProps} className="w-full" aria-label={fileName} />
        )}

        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant={showDiagnostics ? "secondary" : "ghost"}
            size="small"
            onClick={() => setShowDiagnostics((x) => !x)}
            aria-expanded={showDiagnostics}
          >
            <Icon name="troubleshoot" className="!text-[18px]" />
            Diagnostics
          </Button>
          <a
            href={previewUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="btn btn-ghost btn-sm gap-2"
          >
            <Icon name="open_in_new" className="!text-[18px]" />
            Open direct
          </a>
          <a href={downloadUrl} className="btn btn-ghost btn-sm gap-2">
            <Icon name="download" className="!text-[18px]" />
            Download
          </a>
          {sizeBytes != null && (
            <span className="ml-auto font-mono text-xs text-base-content/50">
              {formatFileSize(sizeBytes)}
            </span>
          )}
        </div>

        {showDiagnostics && (
          <MediaDiagnostics
            player={player}
            getMedia={() => player.mediaRef.current}
            playerSession={playerSession}
            fileName={fileName}
            filePath={filePath}
            mimeType={mimeType}
            sizeBytes={sizeBytes}
          />
        )}
      </div>
    </Modal>
  );
}
