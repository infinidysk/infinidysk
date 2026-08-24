import { Button } from "~/components/ui/button";
import { Alert, Tooltip } from "~/components/ui/feedback";
import { Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { useCallback, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { withUrlBase } from "~/utils/url-base";

type RecreateStrmFilesProps = {
  savedConfig: Record<string, string>;
};

export function RecreateStrmFiles({ savedConfig }: RecreateStrmFilesProps) {
  const [connected, setConnected] = useState<boolean>(false);
  const [progress, setProgress] = useState<string | null>(null);
  const [isFetching, setIsFetching] = useState<boolean>(false);
  const [runStarted, setRunStarted] = useState<boolean>(false);
  const [rewriteAll, setRewriteAll] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);

  const completedDir = savedConfig["api.completed-downloads-dir"]?.trim();
  const importStrategy = savedConfig["api.import-strategy"] ?? "symlinks";
  const baseUrl = savedConfig["general.base-url"]?.trim();
  const isStrmOutputEnabled =
    importStrategy === "strm" || savedConfig["api.strm-output-enabled"] === "true";
  const canRun = !!completedDir && !!baseUrl && isStrmOutputEnabled;

  const isFinished =
    !!progress &&
    (progress.startsWith("Done") ||
      progress.startsWith("Failed") ||
      progress.startsWith("Aborted"));
  const isRunning = runStarted && !isFinished && (isFetching || progress !== null);
  const isRunButtonEnabled = canRun && connected && !isRunning;
  const runButtonVariant = isRunButtonEnabled ? "primary" : "secondary";
  const runButtonLabel = isRunning ? "Running..." : "Run Task";

  useWebsocketTopic(
    "rstm",
    "state",
    (msg) => {
      if (runStarted) setProgress(msg);
    },
    {
      onOpen: () => setConnected(true),
      onClose: () => {
        setConnected(false);
        if (!runStarted) setProgress(null);
      },
    },
  );

  const onRun = useCallback(async () => {
    setError(null);
    setRunStarted(true);
    setIsFetching(true);
    setProgress(null);
    try {
      const qs = rewriteAll ? "?rewriteAll=true" : "";
      const response = await fetch(withUrlBase(`/api/recreate-strm-files${qs}`));
      if (response.status === 409) {
        setError("Another maintenance task is already running.");
        setRunStarted(false);
        return;
      }
      if (!response.ok) {
        setError(`Request failed (${response.status}).`);
        setRunStarted(false);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : "Request failed.");
      setRunStarted(false);
    } finally {
      setIsFetching(false);
    }
  }, [rewriteAll]);

  return (
    <>
      {!canRun && (
        <Alert className="alert-soft mb-4 items-start text-sm" variant="warning">
          <Icon name="settings_alert" className="!text-[20px]" />
          <div>
            <p className="font-semibold">Configuration required</p>
            <ul className="mt-1 list-disc space-y-1 pl-4 text-xs opacity-80">
              {!isStrmOutputEnabled && <li>Enable STRM output under the SABnzbd tab.</li>}
              {!completedDir && (
                <li>Configure Completed Downloads Directory under the SABnzbd tab.</li>
              )}
              {!baseUrl && (
                <li>Configure Base URL under the SABnzbd tab (used inside STRM URLs).</li>
              )}
            </ul>
          </div>
        </Alert>
      )}
      {error && (
        <Alert className="alert-soft mb-4 text-sm" variant="danger">
          {error}
        </Alert>
      )}
      <div className="space-y-4">
        <p className="text-sm leading-relaxed text-base-content/70">
          Create missing STRM sidecars for mounted videos or refresh sidecars whose target URL has
          changed.
        </p>

        <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
          <Tooltip content="Rewrite every existing STRM sidecar and bump modification times, not only missing or stale ones.">
            <Toggle
              id="recreate-strm-rewrite-all"
              className="cursor-pointer gap-2 p-0"
              checked={rewriteAll}
              onChange={(e) => setRewriteAll(e.target.checked)}
              disabled={isRunning}
              label={
                <span className="text-sm text-base-content">
                  Rewrite every STRM file and update modification times
                </span>
              }
            />
          </Tooltip>
          <div className="mt-3 flex flex-col gap-3 border-t border-base-content/10 pt-3 sm:flex-row sm:items-center sm:justify-between">
            <Button
              className="shrink-0"
              variant={runButtonVariant}
              onClick={() => void onRun()}
              disabled={!isRunButtonEnabled}
            >
              <Icon
                name={isRunning ? "progress_activity" : "refresh"}
                className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`}
              />
              {runButtonLabel}
            </Button>
            <div
              aria-live="polite"
              className="min-w-0 whitespace-pre-line break-words font-mono text-xs text-base-content/70"
            >
              {progress ?? "Ready to recreate sidecars."}
            </div>
          </div>
        </div>

        <div className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55">
          <Icon name="info" className="mt-0.5 !text-[17px] shrink-0 text-base-content/45" />
          <p>
            STRM files are written under{" "}
            <span className="font-mono text-base-content/70">
              {completedDir || "the completed downloads directory"}
            </span>
            . Rewrite-all can trigger a media-server rescan because it updates every file&apos;s
            modification time.
          </p>
        </div>
      </div>
    </>
  );
}
