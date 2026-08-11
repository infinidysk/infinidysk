import { useMemo, useState } from "react";
import { Alert, Button, Icon, Modal } from "~/components/ui";
import { formatFileSize } from "~/utils/file-size";
import { isVideoFile } from "../file-kind/file-kind";
import { MediaDiagnostics } from "./media-diagnostics";
import { appendQueryParam, buildMediaSrc, formatClock } from "./media-utils";
import { useMediaPlayer } from "./use-media-player";

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
    const playerSession = useMemo(() => crypto.randomUUID(), []);
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
        <Modal open title={fileName} onClose={onClose} size="wide">
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
                        onClick={() => setShowDiagnostics(x => !x)}
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

function StatusBanner({ player, mimeType }: { player: ReturnType<typeof useMediaPlayer>, mimeType: string }) {
    switch (player.status) {
        case "loading":
            return (
                <div className="flex items-center gap-2 text-sm text-base-content/60" role="status">
                    <span className="loading loading-spinner loading-xs" />
                    Loading…
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
                    {player.lastGoodTimeRef.current > 0 ? ` from ${formatClock(player.lastGoodTimeRef.current)}` : ""}
                    {" "}(attempt {player.attempts}/{player.maxAttempts})
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
                        This browser could not play {mimeType || "this file"}
                        {player.error?.message ? ` (${player.error.message})` : ""}.
                        {" "}The container or one of its streams may be unsupported here —
                        use Open direct or Download and play it in an external player.
                    </span>
                </Alert>
            );
        case "unavailable":
            return (
                <Alert variant="danger" className="alert-soft py-2 text-sm" role="alert">
                    <Icon name="error" className="!text-[18px]" />
                    <span>
                        The server refused to serve this file
                        {player.unavailableStatus !== null ? ` (HTTP ${player.unavailableStatus})` : ""}.
                        {" "}Close the preview and try again, or use Download.
                    </span>
                </Alert>
            );
        case "missing-payload":
            return (
                <Alert variant="danger" className="alert-soft py-2 text-sm" role="alert">
                    <Icon name="error" className="!text-[18px]" />
                    <span>
                        This file cannot be served: its streaming data is missing from the server
                        (often a database restore without the blobs/ folder).
                        {" "}Remove and re-download the release, or restore from a backup that includes blobs.
                    </span>
                </Alert>
            );
    }
}
