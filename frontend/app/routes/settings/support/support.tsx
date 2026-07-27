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
import { useWebsocketTopic } from "~/utils/shared-websocket";
import type { StreamTracingStatus } from "~/components/stream-tracing-banner";

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

    useEffect(() => {
        let cancelled = false;
        void fetch("/settings/stream-tracing")
            .then(async (response) => {
                if (!response.ok) return;
                const next = await response.json() as StreamTracingStatus;
                if (!cancelled) setStatus(next);
            })
            .catch(() => { /* banner / websocket will catch up */ });
        return () => { cancelled = true; };
    }, []);

    useWebsocketTopic("strt", "state", (messageText) => {
        try {
            setStatus(JSON.parse(messageText) as StreamTracingStatus);
        } catch {
            // ignore malformed payloads
        }
    });

    useEffect(() => {
        if (!status?.enabled) return;
        const id = window.setInterval(() => setNow(Date.now()), 60_000);
        return () => window.clearInterval(id);
    }, [status?.enabled]);

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
        try {
            const form = new FormData();
            form.append("enabled", enabled ? "true" : "false");
            form.append("minutes", String(durationMinutes));
            const response = await fetch("/settings/stream-tracing", { method: "POST", body: form });
            if (!response.ok) {
                const body = await response.json().catch(() => null);
                throw new Error(body?.error || `Could not update stream tracing (${response.status})`);
            }
            const next = await response.json() as StreamTracingStatus;
            setStatus(next);
            setTracingMessage({
                text: enabled
                    ? `Stream tracing enabled for ${durationMinutes} minutes. Reproduce the issue, then download a support pack.`
                    : "Stream tracing turned off and the buffer was released.",
                variant: "success",
            });
        } catch (error) {
            setTracingMessage({
                text: error instanceof Error ? error.message : "Could not update stream tracing.",
                variant: "danger",
            });
        } finally {
            setTracingBusy(false);
        }
    }, [minutes]);

    const enabled = Boolean(status?.enabled);
    const statusLine = enabled && status
        ? `Tracing active — ${formatRemaining(status.expiresAtUnixMs, now)}, ${status.eventCount.toLocaleString()} events across ${status.sessionCount.toLocaleString()} sessions`
        : "Tracing is off.";

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
                    <li>Stream traces, when developer stream tracing is enabled</li>
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
                        appears while it is active.
                    </span>
                </Alert>

                <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
                    <div className="flex-1 space-y-2">
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
                    <Toggle
                        label={enabled ? "Tracing on" : "Tracing off"}
                        checked={enabled}
                        disabled={tracingBusy || status?.source === "env"}
                        onChange={(event) => void setTracing(event.target.checked, minutes)}
                    />
                </div>

                <p className="text-sm text-base-content/70" aria-live="polite">{statusLine}</p>

                {enabled && status?.source === "ui" && (
                    <div>
                        <Button
                            variant="outline"
                            disabled={tracingBusy}
                            onClick={() => void setTracing(false)}
                        >
                            Turn off now
                        </Button>
                    </div>
                )}

                {status?.source === "env" && enabled && (
                    <HelpText>
                        Tracing was enabled by STREAM_TRACE_EVENTS and has no UI expiry. Clear the env var
                        and restart to turn it off.
                    </HelpText>
                )}

                {tracingMessage && <Alert variant={tracingMessage.variant}>{tracingMessage.text}</Alert>}
            </SettingsCard>
        </SettingsPage>
    );
}
