import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { useCallback, useEffect, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { withUrlBase } from "~/utils/url-base";

type RenameWindowsInvalidDavPathsProps = {
    savedConfig: Record<string, string>
};

export function RenameWindowsInvalidDavPaths({ savedConfig }: RenameWindowsInvalidDavPathsProps) {
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [runStarted, setRunStarted] = useState<boolean>(false);
    const [statusError, setStatusError] = useState<string | null>(null);
    const progressMessage = progress?.replace("Dry Run - ", "");

    const windowsSafeEnabled = savedConfig["webdav.windows-safe-paths"] !== "false";
    const isDone = progressMessage?.startsWith("Done");
    const isFinished = progressMessage?.startsWith("Done")
        || progressMessage?.startsWith("Failed")
        || progressMessage?.startsWith("Aborted");
    const isRunning = !isFinished && (isFetching || runStarted);
    const isRunButtonEnabled = windowsSafeEnabled && connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? "warning" : "secondary";
    const runButtonLabel = isRunning ? "Running..." : "Apply Renames";

    useWebsocketTopic("rwip", "state", setProgress, {
        onOpen: () => setConnected(true),
        onClose: () => setProgress(null),
    });

    useEffect(() => {
        if (isFinished)
            setRunStarted(false);
    }, [isFinished]);

    const startTask = useCallback(async (url: string) => {
        setStatusError(null);
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

    const onApply = useCallback(async () => {
        await startTask("/api/rename-windows-invalid-dav-paths");
    }, [startTask]);

    const onDryRun = useCallback(async () => {
        await startTask("/api/rename-windows-invalid-dav-paths/dry-run");
    }, [startTask]);

    return (
        <>
            {!windowsSafeEnabled &&
                <Alert className="alert-soft mb-4 items-start text-sm" variant="warning">
                    <Icon name="settings_alert" className="!text-[20px]" />
                    <div>
                        <p className="font-semibold">Windows-safe paths required</p>
                        <p className="mt-0.5 text-xs opacity-80">
                            Enable Windows-safe paths under WebDAV settings before running this task.
                        </p>
                    </div>
                </Alert>
            }
            {windowsSafeEnabled &&
                <Alert className="alert-soft mb-4 items-start py-3 text-sm" variant="warning">
                    <Icon name="backup" className="!text-[20px]" />
                    <div>
                        <p className="font-semibold">Back up before renaming</p>
                        <p className="mt-0.5 text-xs opacity-80">
                            Review a dry run first, then back up the database before applying path changes.
                        </p>
                    </div>
                </Alert>
            }
            <div className="space-y-4">
                <p className="text-sm leading-relaxed text-base-content/70">
                    Find WebDAV names that Windows cannot use and rename those path components with the
                    configured Windows-safe sanitizer.
                </p>

                <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
                    <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                        <div className="flex flex-wrap items-center gap-2">
                            <Button
                                className="shrink-0"
                                variant={runButtonVariant}
                                onClick={() => void onApply()}
                                disabled={!isRunButtonEnabled}
                            >
                                <Icon name={isRunning ? "progress_activity" : "drive_file_rename_outline"} className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`} />
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
                            {isDone && <>
                                {" "}<a className="link link-primary" href={withUrlBase("/api/rename-windows-invalid-dav-paths/audit")}>View report</a>
                            </>}
                        </div>
                    </div>
                    <p className="mt-3 border-t border-base-content/10 pt-2.5 text-xs text-base-content/50">
                        Dry Run generates a report without changing paths. Name collisions receive a
                        {" "}<code className="font-mono">_2</code>, <code className="font-mono">_3</code>, or later suffix.
                    </p>
                </div>

                <div
                    className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55"
                    id="rename-windows-invalid-paths-help"
                >
                    <Icon name="link" className="mt-0.5 !text-[17px] shrink-0 text-base-content/45" />
                    <p>
                        <span className="font-medium text-base-content/70">Organized links remain valid.</span>
                        {" "}STRM files and symlinks target stable DavItem IDs, although browse paths and
                        mirrored on-disk STRM locations will change.
                    </p>
                </div>
            </div>
        </>
    );
}
