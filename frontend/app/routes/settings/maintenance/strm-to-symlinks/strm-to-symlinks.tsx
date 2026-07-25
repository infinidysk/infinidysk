import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { useCallback, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";

type ConvertStrmToSymlinksProps = {
    savedConfig: Record<string, string>
};

export function ConvertStrmToSymlinks({ savedConfig }: ConvertStrmToSymlinksProps) {
    // stateful variables
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);

    // derived variables
    const libraryDir = savedConfig["media.library-dir"];
    const isFinished = progress?.startsWith("Done") || progress?.startsWith("Failed");
    const isRunning = !isFinished && (isFetching || progress !== null);
    const isRunButtonEnabled = !!libraryDir && connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? 'danger' : 'secondary';
    const runButtonLabel = isRunning ? "Running..." : "Run Task";

    useWebsocketTopic("st2sy", "state", setProgress, {
        onOpen: () => setConnected(true),
        onClose: () => setProgress(null),
    });

    // events
    const onRun = useCallback(async () => {
        setIsFetching(true);
        await fetch("/api/convert-strm-to-symlinks");
        setIsFetching(false);
    }, [setIsFetching]);

    return (
        <>
            {!libraryDir &&
                <Alert className="alert-soft mb-4 items-start text-sm" variant="warning">
                    <Icon name="folder_off" className="!text-[20px]" />
                    <div>
                        <p className="font-semibold">Library directory required</p>
                        <p className="mt-0.5 text-xs opacity-80">
                            Configure the Library Directory under Repairs before running this task.
                        </p>
                    </div>
                </Alert>
            }
            {libraryDir &&
                <Alert className="alert-soft mb-4 items-start py-3 text-sm" variant="warning">
                    <Icon name="backup" className="!text-[20px]" />
                    <div>
                        <p className="font-semibold">Back up the library first</p>
                        <p className="mt-0.5 text-xs opacity-80">
                            STRM files under <span className="font-mono">{libraryDir}</span> are replaced and
                            cannot be recovered without a backup.
                        </p>
                    </div>
                </Alert>
            }
            <div className="space-y-4">
                <p className="text-sm leading-relaxed text-base-content/70">
                    Replace NzbDav STRM files in the organized media library with filesystem symlinks to
                    the corresponding files in the rclone mount.
                </p>

                <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
                    <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                        <Button
                            className="shrink-0"
                            variant={runButtonVariant}
                            onClick={onRun}
                            disabled={!isRunButtonEnabled}
                        >
                            <Icon name={isRunning ? "progress_activity" : "play_arrow"} className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`} />
                            {runButtonLabel}
                        </Button>
                        <div
                            aria-live="polite"
                            className="min-w-0 whitespace-pre-line break-words font-mono text-xs text-base-content/70"
                        >
                            {progress ?? "Ready to convert."}
                        </div>
                    </div>
                    <p className="mt-3 border-t border-base-content/10 pt-2.5 text-xs text-base-content/50">
                        Only STRM files that link to NzbDav media are converted.
                    </p>
                </div>

                <div
                    className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55"
                    id="convert-strm-to-symlinks-help"
                >
                    <Icon name="link" className="mt-0.5 !text-[17px] shrink-0 text-base-content/45" />
                    <p>
                        <span className="font-medium text-base-content/70">The rclone mount must remain available.</span>
                        {" "}Created symlinks point directly to their matching mounted files.
                    </p>
                </div>
            </div>
        </>
    );
}
