import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { useCallback, useEffect, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { withUrlBase } from "~/utils/url-base";

type RemoveUnlinkedFilesProps = {
  savedConfig: Record<string, string>;
};

export function RemoveUnlinkedFiles({ savedConfig }: RemoveUnlinkedFilesProps) {
  // stateful variables
  const [connected, setConnected] = useState<boolean>(false);
  const [progress, setProgress] = useState<string | null>(null);
  const [isFetching, setIsFetching] = useState<boolean>(false);
  // Replayed non-terminal ctp state must not look like an active run until the user clicks.
  const [runStarted, setRunStarted] = useState<boolean>(false);
  const [statusError, setStatusError] = useState<string | null>(null);
  const progressMessage = progress?.replace("Dry Run - ", "");

  // derived variables
  const libraryDir = savedConfig["media.library-dir"];
  const isDone = progressMessage?.startsWith("Done");
  const isFinished =
    progressMessage?.startsWith("Done") ||
    progressMessage?.startsWith("Failed") ||
    progressMessage?.startsWith("Aborted");
  const isRunning = !isFinished && (isFetching || runStarted);
  const isRunButtonEnabled = !!libraryDir && connected && !isRunning;
  const runButtonVariant = isRunButtonEnabled ? "danger" : "secondary";
  const runButtonLabel = isRunning ? "Running..." : "Run Task";

  useWebsocketTopic("ctp", "state", setProgress, {
    onOpen: () => setConnected(true),
    onClose: () => setProgress(null),
  });

  useEffect(() => {
    if (isFinished) setRunStarted(false);
  }, [isFinished]);

  const startTask = useCallback(async (url: string) => {
    setStatusError(null);
    // Clear any stale terminal message so isFinished doesn't mask the new run
    // until its first progress message arrives.
    setProgress(null);
    setRunStarted(true);
    setIsFetching(true);
    try {
      const response = await fetch(url);
      if (response.status === 409) {
        setStatusError("Task already running.");
        setRunStarted(false);
        return;
      }
      if (!response.ok) {
        setStatusError(`Request failed (${response.status}).`);
        setRunStarted(false);
      }
    } catch {
      setStatusError("Request failed.");
      setRunStarted(false);
    } finally {
      setIsFetching(false);
    }
  }, []);

  // events
  const onRun = useCallback(async () => {
    await startTask("/api/remove-unlinked-files");
  }, [startTask]);

  const onDryRun = useCallback(async () => {
    await startTask("/api/remove-unlinked-files/dry-run");
  }, [startTask]);

  return (
    <>
      {!libraryDir && (
        <Alert className="alert-soft mb-4 items-start text-sm" variant="warning">
          <Icon name="folder_off" className="!text-[20px]" />
          <div>
            <p className="font-semibold">Library directory required</p>
            <p className="mt-0.5 text-xs opacity-80">
              Configure the Library Directory under Repairs before running this task.
            </p>
          </div>
        </Alert>
      )}
      {libraryDir && (
        <Alert className="alert-soft mb-4 items-start py-3 text-sm" variant="warning">
          <Icon name="backup" className="!text-[20px]" />
          <div>
            <p className="font-semibold">Back up before cleanup</p>
            <p className="mt-0.5 text-xs opacity-80">
              Unlinked WebDAV files are permanently removed and can only be recovered from a
              database backup.
            </p>
          </div>
        </Alert>
      )}
      <div className="space-y-4">
        <p className="text-sm leading-relaxed text-base-content/70">
          Scan the organized media library for symlink and STRM references, then remove WebDAV files
          that are no longer linked from the library.
        </p>

        <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-wrap items-center gap-2">
              <Button
                className={"shrink-0"}
                variant={runButtonVariant}
                onClick={() => void onRun()}
                disabled={!isRunButtonEnabled}
              >
                <Icon
                  name={isRunning ? "progress_activity" : "play_arrow"}
                  className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`}
                />
                {runButtonLabel}
              </Button>
              <Button
                className="shrink-0"
                disabled={!isRunButtonEnabled}
                onClick={() => void onDryRun()}
                variant="outline"
                size="small"
              >
                <Icon name="science" className="!text-[18px]" />
                Dry Run
              </Button>
            </div>
            <div
              aria-live="polite"
              className="min-w-0 whitespace-pre-line break-words font-mono text-xs text-base-content/70"
            >
              {statusError ?? progress ?? "Ready to scan."}
              {isDone && (
                <>
                  {" "}
                  <a
                    className="link link-primary"
                    href={withUrlBase("/api/remove-unlinked-files/audit")}
                  >
                    View audit
                  </a>
                </>
              )}
            </div>
          </div>
          <p className="mt-3 border-t border-base-content/10 pt-2.5 text-xs text-base-content/50">
            Dry Run previews the files that would be removed without changing anything.
          </p>
        </div>

        <div
          className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55"
          id="cleanup-task-progress-help"
        >
          <Icon name="history" className="mt-0.5 !text-[17px] shrink-0 text-base-content/45" />
          <p>
            <span className="font-medium text-base-content/70">History items are protected.</span>{" "}
            Files still in SAB history are left intact so Arrs can finish importing them before
            cleanup.
          </p>
        </div>
      </div>
    </>
  );
}
