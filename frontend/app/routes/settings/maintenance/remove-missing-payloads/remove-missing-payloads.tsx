import { useCallback, useEffect, useState } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { withUrlBase } from "~/utils/url-base";

type RemoveMissingPayloadsProps = {
  savedConfig: Record<string, string>;
};

export function RemoveMissingPayloads({ savedConfig }: RemoveMissingPayloadsProps) {
  const [connected, setConnected] = useState(false);
  const [progress, setProgress] = useState<string | null>(null);
  const [isFetching, setIsFetching] = useState(false);
  const [runStarted, setRunStarted] = useState(false);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [showConfirm, setShowConfirm] = useState(false);
  const [previewToken, setPreviewToken] = useState<string | null>(null);
  const [terminalMessage, setTerminalMessage] = useState<string | null>(null);

  const libraryDir = savedConfig["media.library-dir"];
  const progressMessage = progress?.replace("Dry Run - ", "");
  const isFinished =
    progressMessage?.startsWith("Done") ||
    progressMessage?.startsWith("Failed") ||
    progressMessage?.startsWith("Aborted");
  const isRunning = isFetching || (!isFinished && runStarted);
  const canScan = !!libraryDir && connected && !isRunning;
  const canRun = canScan && previewToken !== null;
  const displayedMessage = statusError ?? progress ?? terminalMessage;
  const displayedIsDone = displayedMessage?.replace("Dry Run - ", "").startsWith("Done") === true;

  useWebsocketTopic("mpcp", "state", setProgress, {
    onOpen: () => setConnected(true),
    onClose: () => {
      setConnected(false);
      setProgress(null);
    },
  });

  useEffect(() => {
    if (isFinished) {
      setRunStarted(false);
      setTerminalMessage(progress);
    }
  }, [isFinished, progress]);

  const startTask = useCallback(
    async (path: string, dryRun: boolean) => {
      setShowConfirm(false);
      setStatusError(null);
      setProgress(null);
      setTerminalMessage(null);
      setRunStarted(true);
      setIsFetching(true);
      if (dryRun) setPreviewToken(null);
      try {
        const response = await fetch(withUrlBase(path), {
          method: "POST",
          ...(!dryRun && previewToken
            ? { headers: { "X-InfiniDysk-Cleanup-Preview": previewToken } }
            : {}),
        });
        const body = (await response.json().catch(() => null)) as {
          error?: string;
          message?: string;
          previewToken?: string;
        } | null;
        const error = body?.error;
        const message = body?.message;
        const token = body?.previewToken;
        if (!response.ok) {
          throw new Error(error || `Request failed (${response.status})`);
        }
        if (dryRun && !token) {
          throw new Error("Dry run completed without a cleanup approval. Run it again.");
        }

        const terminal = `${dryRun ? "Dry Run - " : ""}${message ?? "Done."}`;
        setProgress(terminal);
        setTerminalMessage(terminal);
        setPreviewToken(dryRun ? (token ?? null) : null);
      } catch (error) {
        setPreviewToken(null);
        setStatusError(
          error instanceof Error ? error.message : "Missing-payload cleanup request failed.",
        );
      } finally {
        setIsFetching(false);
        setRunStarted(false);
      }
    },
    [previewToken],
  );

  return (
    <>
      {!libraryDir && (
        <Alert className="alert-soft mb-4 items-start text-sm" variant="warning">
          <Icon name="folder_off" className="!text-[20px]" />
          <div>
            <p className="font-semibold">Library directory required</p>
            <p className="mt-0.5 text-xs opacity-80">
              Configure the Library Directory under Repairs so links can be verified before
              deletion.
            </p>
          </div>
        </Alert>
      )}

      {libraryDir && (
        <Alert className="alert-soft mb-4 items-start py-3 text-sm" variant="warning">
          <Icon name="backup" className="!text-[20px]" />
          <div>
            <p className="font-semibold">Back up /config and preview first</p>
            <p className="mt-0.5 text-xs opacity-80">
              Cleanup permanently removes broken WebDAV rows and verified library links. Restore
              missing blobs from backup before running if recovery is still possible.
            </p>
          </div>
        </Alert>
      )}

      <div className="space-y-4">
        <p className="text-sm leading-relaxed text-base-content/70">
          Find WebDAV files whose streaming payload and legacy database metadata are both gone. The
          dry run lists every affected path and the action planned for its library links.
        </p>

        <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-wrap items-center gap-2">
              <Button
                variant={canRun ? "danger" : "secondary"}
                disabled={!canRun}
                className="shrink-0"
                onClick={() => setShowConfirm(true)}
              >
                <Icon
                  name={isRunning ? "progress_activity" : "delete_sweep"}
                  className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`}
                />
                {isRunning ? "Running..." : "Run Cleanup"}
              </Button>
              <Button
                variant="outline"
                disabled={!canScan}
                className="shrink-0"
                onClick={() => void startTask("/api/remove-missing-payloads/dry-run", true)}
              >
                <Icon name="science" className="!text-[18px]" />
                Dry Run
              </Button>
            </div>
            <div
              aria-live="polite"
              className={`min-w-0 whitespace-pre-line break-words font-mono text-xs ${
                statusError
                  ? "text-error"
                  : displayedIsDone
                    ? "text-success"
                    : "text-base-content/70"
              }`}
            >
              {displayedMessage ??
                (previewToken ? "Dry run reviewed. Cleanup is ready." : "Run a dry run first.")}
              {displayedIsDone && (
                <>
                  {" "}
                  <a
                    className="link link-primary"
                    href={withUrlBase("/api/remove-missing-payloads/audit")}
                  >
                    View audit
                  </a>
                </>
              )}
            </div>
          </div>
          <p className="mt-3 border-t border-base-content/10 pt-2.5 text-xs text-base-content/50">
            Cleanup stays locked until a dry run completes. Approval expires after 15 minutes or
            whenever the candidate/link snapshot changes.
          </p>
        </div>

        <Alert className="alert-soft items-start py-3 text-sm" variant="info">
          <Icon name="manage_search" className="!text-[20px]" />
          <div>
            <p className="font-semibold">Matched Arr files are reacquired safely</p>
            <p className="mt-0.5 text-xs opacity-80">
              Sonarr or Radarr removes the broken media-file record and receives a budget-limited
              replacement search. The original download is not marked failed or blocklisted.
              Ambiguous or unreachable Arr matches are skipped.
            </p>
          </div>
        </Alert>
      </div>

      <ConfirmModal
        show={showConfirm}
        title="Clean missing streaming payloads?"
        message={
          <>
            Verified library links and their broken WebDAV rows will be permanently removed. Matched
            Sonarr and Radarr files will request replacement searches without blocklisting the
            original release.
          </>
        }
        checkboxMessage="I reviewed the dry-run audit and have a current /config backup"
        requireCheckbox
        errorMessage="Pause Arr imports while cleanup runs. Items with unreachable or ambiguous Arr ownership will be left untouched."
        cancelText="Cancel"
        confirmText="Run cleanup"
        onCancel={() => setShowConfirm(false)}
        onConfirm={() => void startTask("/api/remove-missing-payloads", false)}
      />
    </>
  );
}
