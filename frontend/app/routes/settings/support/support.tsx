import { useCallback, useEffect, useState } from "react";
import {
    Alert,
    Button,
    HelpText,
    Icon,
    Label,
    Select,
    SettingsCard,
    SettingsIntro,
    SettingsPage,
    Spinner,
    Toggle,
} from "~/components/ui";
import { DiscardTracesConfirmModal } from "~/components/stream-tracing-confirm";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import {
    toStreamTracingStatus,
    type StreamTracingStatus,
} from "~/utils/stream-tracing-status";

type Message = { text: string; variant: "success" | "danger" } | null;

const DURATION_OPTIONS = [15, 30, 60] as const;

function downloadName(response: Response): string {
    const header = response.headers.get("content-disposition");
    const match = header?.match(/filename="?([^";]+)"?/i);
    return match?.[1] ?? "nzbdav-support-pack.zip";
}

function formatRemaining(expiresAtUnixMs: number, nowMs: number): string {
    if (!expiresAtUnixMs) return "until restart";
    const remainingMs = expiresAtUnixMs - nowMs;
    if (remainingMs <= 0) return "expiring…";
    const totalMinutes = Math.ceil(remainingMs / 60_000);
    return `${totalMinutes}m left`;
}

export function SupportSettings() {
    const [busy, setBusy] = useState(false);
    const [message, setMessage] = useState<Message>(null);
    const [tracingBusy, setTracingBusy] = useState(false);
    const [tracingMessage, setTracingMessage] = useState<Message>(null);
    const [minutes, setMinutes] = useState<number>(30);
    const [status, setStatus] = useState<StreamTracingStatus | null>(null);
    const [now, setNow] = useState(() => Date.now());
    const [confirmDiscard, setConfirmDiscard] = useState(false);

    useEffect(() => {
        let cancelled = false;
        void fetch("/settings/stream-tracing")
            .then(async (response) => {
                if (!response.ok) return;
                const next = await response.json() as Record<string, unknown>;
                if (!cancelled) setStatus(toStreamTracingStatus(next));
            })
            .catch(() => { /* banner / websocket will catch up */ });
        return () => { cancelled = true; };
    }, []);

    useWebsocketTopic("strt", "state", (messageText) => {
        try {
            setStatus(toStreamTracingStatus(JSON.parse(messageText) as Record<string, unknown>));
        } catch {
            // ignore malformed payloads
        }
    });

    useEffect(() => {
        if (!status?.enabled && !status?.retained) return;
        const id = window.setInterval(() => setNow(Date.now()), 60_000);
        return () => window.clearInterval(id);
    }, [status?.enabled, status?.retained]);

    const download = useCallback(async () => {
        setBusy(true);
        setMessage(null);
        try {
            const response = await fetch("/api/download-support-pack", { cache: "no-store" });
            if (!response.ok) {
                const body = await response.json().catch(() => null);
                throw new Error(body?.error || `Support pack failed (${response.status})`);
            }

            const blob = await response.blob();
            const url = URL.createObjectURL(blob);
            const anchor = document.createElement("a");
            anchor.href = url;
            anchor.download = downloadName(response);
            document.body.append(anchor);
            anchor.click();
            anchor.remove();
            URL.revokeObjectURL(url);
            setMessage({ text: "Support pack downloaded. Share it only with trusted NzbDAV support.", variant: "success" });
        } catch (error) {
            setMessage({
                text: error instanceof Error ? error.message : "Could not generate the support pack.",
                variant: "danger",
            });
        } finally {
            setBusy(false);
        }
    }, []);

    const setTracing = useCallback(async (enabled: boolean, durationMinutes: number = minutes) => {
        setTracingBusy(true);
        setTracingMessage(null);
        const wasRetained = Boolean(status?.retained);
        try {
            const form = new FormData();
            form.append("enabled", enabled ? "true" : "false");
            form.append("minutes", String(durationMinutes));
            const response = await fetch("/settings/stream-tracing", { method: "POST", body: form });
            if (!response.ok) {
                const body = await response.json().catch(() => null);
                throw new Error(body?.error || `Could not update stream tracing (${response.status})`);
            }
            const next = toStreamTracingStatus(await response.json() as Record<string, unknown>);
            setStatus(next);
            let text: string;
            if (enabled) {
                text = wasRetained
                    ? `Stream tracing resumed for ${durationMinutes} minutes. Reproduce the issue, then download a support pack.`
                    : `Stream tracing enabled for ${durationMinutes} minutes. Reproduce the issue, then download a support pack.`;
            } else if (next.retained) {
                text = `Stream tracing stopped. ${next.eventCount.toLocaleString()} events are still held — generate a support pack, resume tracing, or discard them below.`;
            } else {
                text = "Stream tracing stopped. No events were captured.";
            }
            setTracingMessage({ text, variant: "success" });
        } catch (error) {
            setTracingMessage({
                text: error instanceof Error ? error.message : "Could not update stream tracing.",
                variant: "danger",
            });
        } finally {
            setTracingBusy(false);
        }
    }, [minutes, status?.retained]);

    const discardTraces = useCallback(async () => {
        setTracingBusy(true);
        setTracingMessage(null);
        try {
            const form = new FormData();
            form.append("intent", "discard");
            const response = await fetch("/settings/stream-tracing", { method: "POST", body: form });
            if (!response.ok) {
                const body = await response.json().catch(() => null);
                throw new Error(body?.error || `Could not discard stream traces (${response.status})`);
            }
            const next = toStreamTracingStatus(await response.json() as Record<string, unknown>);
            setStatus(next);
            setTracingMessage({
                text: "Captured stream traces were discarded.",
                variant: "success",
            });
        } catch (error) {
            setTracingMessage({
                text: error instanceof Error ? error.message : "Could not discard stream traces.",
                variant: "danger",
            });
        } finally {
            setTracingBusy(false);
        }
    }, []);

    const enabled = Boolean(status?.enabled);
    const retained = Boolean(status?.retained && (status?.eventCount ?? 0) > 0);
    let statusLine = "Tracing is off.";
    if (enabled && status) {
        statusLine = `Tracing active — ${formatRemaining(status.expiresAtUnixMs, now)}, ${status.eventCount.toLocaleString()} events across ${status.sessionCount.toLocaleString()} sessions`;
    } else if (retained && status) {
        statusLine = `Tracing is off — ${status.eventCount.toLocaleString()} events across ${status.sessionCount.toLocaleString()} sessions retained for a support pack (released automatically in ${formatRemaining(status.retainedUntilUnixMs, now)})`;
    }

    return (
        <SettingsPage>
            <SettingsIntro>
                Generate a technical support pack to help diagnose an NzbDAV problem.
                It is generated in memory and is not saved on the server.
            </SettingsIntro>

            <Alert variant="warning" className="items-start text-sm">
                <Icon name="privacy_tip" className="mt-0.5 !text-[20px]" />
                <span>
                    Passwords, API keys, tokens, URL credentials, sensitive URL parameters, and IP addresses
                    are automatically redacted. File names, paths, account usernames, DNS names, and non-secret
                    URL paths can remain. Review the archive before sharing it.
                </span>
            </Alert>

            <SettingsCard
                icon="support_agent"
                title="Technical support pack"
                description="A ZIP with recent backend diagnostics for troubleshooting.">
                <ul className="list-inside list-disc space-y-1 text-sm text-base-content/70">
                    <li>Current backend logs from the in-memory buffer, plus a separate warnings lane</li>
                    <li>Redacted active settings and runtime information</li>
                    <li>Recent provider outage, failover, and consumption metrics</li>
                    <li>Stream traces, while developer stream tracing is on or a capture is retained</li>
                </ul>
                <p className="text-xs text-base-content/50">
                    It excludes frontend and container logs, databases, backups, NZBs, blobs, environment files,
                    crash dumps, and segment-cache data.
                </p>
                <div className="flex flex-wrap items-center gap-3 pt-1">
                    <Button variant="primary" disabled={busy} onClick={() => void download()}>
                        {busy ? <Spinner size="sm" /> : <Icon name="download" className="!text-[18px]" />}
                        {busy ? "Generating…" : "Generate & download"}
                    </Button>
                    <span className="text-xs text-base-content/50" aria-live="polite">
                        {busy ? "Collecting and redacting diagnostics…" : ""}
                    </span>
                </div>
                {message && <Alert variant={message.variant}>{message.text}</Alert>}
            </SettingsCard>

            <SettingsCard
                icon="bug_report"
                title="Developer stream tracing"
                description="Capture per-segment playback events while you reproduce buffering or seek stalls.">
                <Alert variant="info" className="items-start text-sm">
                    <Icon name="memory" className="mt-0.5 !text-[20px]" />
                    <span>
                        Tracing is memory-only (up to 20,000 events), never written to disk, and resets on
                        restart. Leave it off unless you are collecting a support pack — a warning banner
                        appears while it is active. Turning it off keeps the capture for about an hour so
                        you can still download a pack.
                    </span>
                </Alert>

                <div className="flex flex-col gap-4">
                    <div className="space-y-2">
                        <Label htmlFor="stream-tracing-duration">Duration</Label>
                        <Select
                            id="stream-tracing-duration"
                            className="w-full max-w-xs"
                            value={String(minutes)}
                            disabled={tracingBusy || enabled}
                            onChange={(event) => setMinutes(Number(event.target.value))}
                        >
                            {DURATION_OPTIONS.map((value) => (
                                <option key={value} value={value}>{value} minutes</option>
                            ))}
                        </Select>
                        <HelpText>Auto-disables after the timer so tracing cannot be left on indefinitely.</HelpText>
                    </div>
                    <div className="flex flex-wrap items-center gap-3">
                        <Toggle
                            label={enabled ? "Tracing on" : "Tracing off"}
                            checked={enabled}
                            disabled={tracingBusy}
                            onChange={(event) => {
                                if (event.target.checked) {
                                    void setTracing(true, minutes);
                                } else {
                                    void setTracing(false);
                                }
                            }}
                        />
                        {enabled && (
                            <Button
                                variant="outline"
                                disabled={tracingBusy}
                                onClick={() => void setTracing(false)}
                            >
                                Turn off now
                            </Button>
                        )}
                        {retained && (
                            <Button
                                variant="outline"
                                disabled={tracingBusy}
                                onClick={() => setConfirmDiscard(true)}
                            >
                                Discard captured traces
                            </Button>
                        )}
                    </div>
                </div>

                <p className="text-sm text-base-content/70" aria-live="polite">{statusLine}</p>

                {status?.source === "env" && enabled && (
                    <HelpText>
                        Tracing was started by STREAM_TRACE_EVENTS, so it has no expiry. Turning it off here
                        applies until the next restart; clear the env var to keep it off permanently.
                    </HelpText>
                )}

                {tracingMessage && <Alert variant={tracingMessage.variant}>{tracingMessage.text}</Alert>}
            </SettingsCard>

            <DiscardTracesConfirmModal
                show={confirmDiscard}
                eventCount={status?.eventCount ?? 0}
                sessionCount={status?.sessionCount ?? 0}
                onCancel={() => setConfirmDiscard(false)}
                onConfirm={() => {
                    setConfirmDiscard(false);
                    void discardTraces();
                }}
            />
        </SettingsPage>
    );
}
