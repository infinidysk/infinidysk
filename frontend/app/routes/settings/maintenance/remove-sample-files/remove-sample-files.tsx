import { Alert, Button, Form } from "react-bootstrap";
import styles from "./remove-sample-files.module.css"
import { useCallback, useEffect, useState } from "react";
import { receiveMessage } from "~/utils/websocket-util";

const sampleCleanupTaskTopic = { 'sctp': 'state' };

export function RemoveSampleFiles() {
    // stateful variables
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);
    const [triggerArrSearch, setTriggerArrSearch] = useState<boolean>(true);
    const progressMessage = progress?.replace('Dry Run - ', '');

    // derived variables
    const isDone = progressMessage?.startsWith("Done");
    const isFinished = progressMessage?.startsWith("Done") || progressMessage?.startsWith("Failed") || progressMessage?.startsWith("Aborted");
    const isRunning = !isFinished && (isFetching || progress !== null);
    const isRunButtonEnabled = connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? 'success' : 'secondary';
    const runButtonLabel = isRunning ? "⌛ Running.." : '▶ Run Task';

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => setProgress(message));
            ws.onopen = () => { setConnected(true); ws.send(JSON.stringify(sampleCleanupTaskTopic)); }
            ws.onclose = () => { !disposed && setTimeout(() => connect(), 1000); setProgress(null) };
            ws.onerror = () => { ws.close() };
            return () => { disposed = true; ws.close(); }
        }
        return connect();
    }, [setProgress, setConnected]);

    // events
    const onRun = useCallback(async () => {
        setIsFetching(true);
        await fetch(`/api/remove-sample-files?triggerArrSearch=${triggerArrSearch}`);
        setIsFetching(false);
    }, [setIsFetching, triggerArrSearch]);

    const onDryRun = useCallback(async (event: any) => {
        setIsFetching(true);
        await fetch(`/api/remove-sample-files/dry-run?triggerArrSearch=${triggerArrSearch}`);
        setIsFetching(false);
    }, [setIsFetching, triggerArrSearch]);

    // view
    const dryRunButton =
        <Button
            className={styles["dryrun-button"]}
            disabled={!isRunButtonEnabled}
            onClick={onDryRun}
            variant="secondary"
            size="sm"
        >
            perform a dry-run
        </Button>;

    return (
        <>
            <Alert className={styles.alert} variant="danger">
                <span style={{ fontWeight: 'bold' }}>Danger</span>
                <ul className={styles.list}>
                    <li className={styles["list-item"]}>
                        Make a backup of your NzbDAV database prior to running this task
                    </li>
                    <li className={styles["list-item"]}>
                        Sample files will be removed from the webdav and will not be recoverable without a backup
                    </li>
                    <li className={styles["list-item"]}>
                        If a release turns out to be sample-only, this task can delete the corresponding
                        episode/movie file in Radarr/Sonarr and trigger a new search
                    </li>
                </ul>
            </Alert>
            <div className={styles.task}>
                <Form.Group>
                    <Form.Check
                        className={styles.option}
                        type="checkbox"
                        id="trigger-arr-search-checkbox"
                        aria-describedby="trigger-arr-search-help"
                        label="Trigger Arr search for sample-only releases"
                        disabled={isRunning}
                        checked={triggerArrSearch}
                        onChange={e => setTriggerArrSearch(e.target.checked)} />
                    <Form.Text id="trigger-arr-search-help" muted>
                        When enabled, releases that turn out to be sample-only will have their episode/movie file
                        removed from Radarr/Sonarr and a new search triggered. If more than 10 sample-only releases
                        are found in a single run, this is skipped automatically to avoid triggering a burst of
                        searches — the sample files are still removed in that case.
                    </Form.Text>
                    <div className={styles.run}>
                        <Button
                            className={styles["run-button"]}
                            variant={runButtonVariant}
                            onClick={onRun}
                            disabled={!isRunButtonEnabled}
                        >
                            {runButtonLabel}
                        </Button>
                        <div className={styles["task-progress"]}>
                            {progress}
                            {isDone && <>
                                &nbsp;<a href="/api/remove-sample-files/audit">Audit.</a>
                            </>}
                        </div>
                    </div>
                    <Form.Text id="sample-cleanup-task-progress-help" muted>
                        <br />
                        This task scans the webdav for already-imported video files that look like "sample" preview
                        clips. If a real, full-length video also exists for the same release, the sample is removed.
                        If only a sample was imported for a release, the sample is removed and (unless disabled above)
                        Radarr/Sonarr is instructed to search for a proper release again.
                        If you would like to see what would happen without running the task, you can {dryRunButton}.
                        The dry-run will not delete anything or contact Radarr/Sonarr.
                    </Form.Text>
                </Form.Group>
            </div>
        </>
    );
}
