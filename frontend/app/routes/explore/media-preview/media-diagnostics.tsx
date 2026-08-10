import { useEffect, useRef, useState } from "react";
import type {
    ActiveRead,
    ActiveReadsMessage,
    LiveStatsMessage,
} from "~/clients/backend-client.server";
import { Button, Icon } from "~/components/ui";
import { formatFileSize } from "~/utils/file-size";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { toStreamTracingStatus, type StreamTracingStatus } from "~/utils/stream-tracing-status";
import { withUrlBase } from "~/utils/url-base";
import {
    bufferedAhead,
    formatClock,
    formatTimeRanges,
    networkStateLabel,
    readyStateLabel,
} from "./media-utils";
import {
    describeTraceEvent,
    redactTraceEvents,
    summarizeTrace,
    type StreamTraceEvent,
} from "./media-trace";
import type { MediaPlayer } from "./use-media-player";

const TOPIC_ACTIVE_READS = "ar";
const TOPIC_LIVE_STATS = "ls";
const STATUS_POLL_MS = 5_000;
const EVENTS_POLL_MS = 3_000;
const RECENT_EVENTS_SHOWN = 50;

export type MediaDiagnosticsProps = {
    player: MediaPlayer;
    getMedia: () => HTMLMediaElement | null;
    playerSession: string;
    fileName: string;
    filePath: string;
    mimeType: string;
    sizeBytes: number | null;
};

export function MediaDiagnostics(props: MediaDiagnosticsProps) {
    const { player, getMedia, playerSession } = props;

    // Low-frequency tick drives all derived readouts; the media element
    // itself is outside this subtree, so ticking never re-renders it.
    const [now, setNow] = useState(() => Date.now());
    useEffect(() => {
        const interval = setInterval(() => setNow(Date.now()), 500);
        return () => clearInterval(interval);
    }, []);

    const [reads, setReads] = useState<ActiveRead[]>([]);
    const [liveStats, setLiveStats] = useState<LiveStatsMessage | null>(null);
    const prevBytesRef = useRef<{ id: string, bytes: number, at: number, rate: number } | null>(null);

    useWebsocketTopic(TOPIC_ACTIVE_READS, "state", message => {
        try {
            const payload = JSON.parse(message) as ActiveReadsMessage;
            setReads(payload.reads ?? []);
        } catch { /* ignore malformed payloads */ }
    });
    useWebsocketTopic(TOPIC_LIVE_STATS, "state", message => {
        try {
            setLiveStats(JSON.parse(message) as LiveStatsMessage);
        } catch { /* ignore malformed payloads */ }
    });

    const matchedRead = reads.find(r => r.playerSession === playerSession) ?? null;
    const readRate = matchedRead ? smoothRate(prevBytesRef, matchedRead, now) : 0;

    const [traceStatus, setTraceStatus] = useState<StreamTracingStatus | null>(null);
    const [traceEvents, setTraceEvents] = useState<StreamTraceEvent[] | null>(null);
    const [traceBusy, setTraceBusy] = useState(false);
    const [traceError, setTraceError] = useState<string | null>(null);
    const [copyNote, setCopyNote] = useState<string | null>(null);

    useEffect(() => {
        let cancelled = false;
        const load = () => {
            void fetch(withUrlBase("/settings/stream-tracing"))
                .then(async response => {
                    if (!response.ok || cancelled) return;
                    const data = await response.json() as Record<string, unknown>;
                    if (!cancelled) setTraceStatus(toStreamTracingStatus(data));
                })
                .catch(() => { /* status stays null; row shows unavailable */ });
        };
        load();
        const interval = setInterval(load, STATUS_POLL_MS);
        return () => { cancelled = true; clearInterval(interval); };
    }, []);

    const sessionId = matchedRead?.id ?? null;
    const tracingActive = Boolean(traceStatus && (traceStatus.enabled || traceStatus.retained));

    useEffect(() => {
        if (!sessionId || !tracingActive) return;
        let cancelled = false;
        const load = () => {
            void fetch(withUrlBase(`/api/get-stream-trace?sessionId=${encodeURIComponent(sessionId)}`))
                .then(async response => {
                    if (!response.ok || cancelled) return;
                    const data = await response.json() as { events?: StreamTraceEvent[] };
                    if (!cancelled) setTraceEvents(data.events ?? []);
                })
                .catch(() => { /* keep last events on transient failure */ });
        };
        load();
        const interval = setInterval(load, EVENTS_POLL_MS);
        return () => { cancelled = true; clearInterval(interval); };
    }, [sessionId, tracingActive]);

    const startTrace = async () => {
        setTraceBusy(true);
        setTraceError(null);
        try {
            const form = new FormData();
            form.append("enabled", "true");
            form.append("minutes", "15");
            form.append("capacity", "100000");
            const response = await fetch(withUrlBase("/settings/stream-tracing"), { method: "POST", body: form });
            if (!response.ok) {
                const body = await response.json().catch(() => null) as { error?: string } | null;
                throw new Error(body?.error || `Could not start tracing (${response.status})`);
            }
            const data = await response.json() as Record<string, unknown>;
            setTraceStatus(toStreamTracingStatus(data));
        } catch (e) {
            setTraceError(e instanceof Error ? e.message : "Could not start tracing");
        } finally {
            setTraceBusy(false);
        }
    };

    const el = getMedia();
    const summary = traceEvents ? summarizeTrace(traceEvents) : null;
    const recentEvents = traceEvents ? traceEvents.slice(-RECENT_EVENTS_SHOWN).reverse() : [];

    const copyText = (text: string, note: string) => {
        void navigator.clipboard.writeText(text)
            .then(() => {
                setCopyNote(note);
                setTimeout(() => setCopyNote(null), 2000);
            })
            .catch(() => setCopyNote("Copy failed"));
    };

    const copySnapshot = () => {
        const snapshot = buildDiagnosticsSnapshot(props, matchedRead, liveStats, el, summary, traceEvents);
        copyText(JSON.stringify(snapshot, null, 2), "Diagnostics copied");
    };

    return (
        <div className="flex flex-col gap-4 rounded-lg border border-base-content/10 bg-base-200 p-3 text-xs">
            <Section title="Player">
                <Grid>
                    <Field label="Status" value={player.status + (player.buffering ? " · buffering" : "")} />
                    <Field label="Startup" value={player.startupMs != null ? `${player.startupMs} ms` : "—"} />
                    <Field
                        label="Position"
                        value={el ? `${formatClock(el.currentTime)} / ${formatClock(el.duration)}` : "—"}
                        mono
                    />
                    <Field label="Buffer ahead" value={el ? `${bufferedAhead(el.buffered, el.currentTime).toFixed(1)} s` : "—"} mono />
                    <Field label="Buffered" value={el ? formatTimeRanges(el.buffered) : "—"} mono />
                    <Field label="Ready state" value={el ? readyStateLabel(el.readyState) : "—"} mono />
                    <Field label="Network state" value={el ? networkStateLabel(el.networkState) : "—"} mono />
                    <Field label="Resolution" value={videoDimensions(el)} mono />
                    <Field label="Playback rate" value={el ? `${el.playbackRate}×` : "—"} mono />
                    <Field label="Paused" value={el ? (el.paused ? "yes" : "no") : "—"} />
                    <Field label="Seeking" value={el ? (el.seeking ? "yes" : "no") : "—"} />
                    <Field label="Dropped frames" value={droppedFrames(el)} mono />
                    <Field
                        label="Last progress"
                        value={player.lastProgressAtRef.current != null
                            ? `${((now - player.lastProgressAtRef.current) / 1000).toFixed(1)} s ago`
                            : "—"}
                        mono
                    />
                    <Field label="Recovery attempts" value={`${player.attempts}/${player.maxAttempts}`} mono />
                    <Field label="Online" value={navigator.onLine ? "yes" : "no"} />
                    <Field label="Page visible" value={document.visibilityState === "visible" ? "yes" : "no"} />
                </Grid>
            </Section>

            <Section title="Backend read">
                {matchedRead ? (
                    <>
                        <Grid>
                            <Field
                                label="Session"
                                value={matchedRead.id.slice(0, 8)}
                                mono
                                action={
                                    <CopyButton
                                        label="Copy read session ID"
                                        onCopy={() => copyText(matchedRead.id, "Session ID copied")}
                                    />
                                }
                            />
                            <Field
                                label="Offset"
                                value={matchedRead.fileSize
                                    ? `${formatFileSize(matchedRead.currentOffset)} / ${formatFileSize(matchedRead.fileSize)}`
                                    : formatFileSize(matchedRead.currentOffset)}
                                mono
                            />
                            <Field label="Served" value={formatFileSize(matchedRead.bytesRead)} mono />
                            <Field
                                label="Fetched"
                                value={matchedRead.bytesFetched != null ? formatFileSize(matchedRead.bytesFetched) : "—"}
                                mono
                            />
                            <Field label="Rate" value={`${formatFileSize(Math.round(readRate))}/s`} mono />
                            <Field
                                label="Last activity"
                                value={`${Math.max(0, Math.round((now - matchedRead.lastActivityAt) / 1000))} s ago`}
                                mono
                            />
                        </Grid>
                        {matchedRead.providers.length > 0 && (
                            <div className="mt-1 flex flex-wrap gap-1.5">
                                {matchedRead.providers.map(p => (
                                    <span key={p.host} className="badge badge-ghost badge-sm font-mono">
                                        {p.nickname ?? p.host} · {p.segments}
                                    </span>
                                ))}
                            </div>
                        )}
                    </>
                ) : (
                    <p className="text-base-content/50">
                        No matching active read. The backend session appears once bytes start flowing.
                    </p>
                )}
                {liveStats && (
                    <p className="mt-1 text-base-content/45">
                        Server-wide: {liveStats.activeReads} active read{liveStats.activeReads === 1 ? "" : "s"}
                        {liveStats.inFlightArticleBytes != null
                            ? ` · ${formatFileSize(liveStats.inFlightArticleBytes)} article RAM in flight`
                            : ""}
                        {` · ${formatFileSize(Math.round(liveStats.bytesServedPerMinute / 60))}/s served`}
                    </p>
                )}
            </Section>

            <Section title="Stream trace">
                {traceError && (
                    <p role="alert" className="alert alert-error alert-soft mb-2 py-2">{traceError}</p>
                )}
                {!traceStatus && (
                    <p className="text-base-content/50">Trace status unavailable.</p>
                )}
                {traceStatus && !tracingActive && (
                    <div className="flex flex-wrap items-center gap-2">
                        <p className="flex-1 text-base-content/50">
                            Developer stream tracing is off. Segment-level events are only captured while tracing is on.
                        </p>
                        <Button size="xsmall" variant="outline" onClick={() => void startTrace()} disabled={traceBusy}>
                            {traceBusy ? <span className="loading loading-spinner loading-xs" /> : <Icon name="fiber_manual_record" className="!text-[14px]" />}
                            Record 15 min
                        </Button>
                    </div>
                )}
                {traceStatus?.enabled && (
                    <p className="text-base-content/50">
                        Recording until {new Date(traceStatus.expiresAtUnixMs).toLocaleTimeString()}
                        {" · "}{traceStatus.eventCount.toLocaleString()} events
                    </p>
                )}
                {traceStatus?.overflowed && (
                    <p className="alert alert-warning alert-soft py-2">
                        Trace buffer wrapped — events shown are a partial window
                        ({traceStatus.retainedEventCount.toLocaleString()} of {traceStatus.eventCount.toLocaleString()} retained).
                    </p>
                )}
                {tracingActive && !sessionId && (
                    <p className="text-base-content/50">Waiting for this playback session to appear in the trace…</p>
                )}
                {summary && (
                    <>
                        <Grid>
                            <Field label="Ranges" value={`${summary.rangeOpens} opened · ${summary.rangeEnds} ended`} mono />
                            <Field label="Seeks" value={String(summary.seeks)} mono />
                            <Field label="Segments" value={String(summary.segments)} mono />
                            <Field label="Retries" value={String(summary.retries)} mono />
                            <Field label="Failovers" value={String(summary.failovers)} mono />
                            <Field label="Zero fills" value={String(summary.zeroFills)} mono />
                            <Field label="Prefetch changes" value={String(summary.prefetchChanges)} mono />
                            <Field label="Bytes served" value={formatFileSize(summary.bytesServed)} mono />
                            {summary.lastEndReason && (
                                <Field
                                    label="Last range end"
                                    value={summary.lastEndReason + (summary.lastEndMessage ? ` · ${summary.lastEndMessage}` : "")}
                                />
                            )}
                        </Grid>
                        {Object.keys(summary.segmentsByStatus).length > 0 && (
                            <div className="mt-1 flex flex-wrap gap-1.5">
                                {Object.entries(summary.segmentsByStatus).map(([statusName, count]) => (
                                    <span key={statusName} className="badge badge-ghost badge-sm font-mono">
                                        {statusName} · {count}
                                    </span>
                                ))}
                            </div>
                        )}
                        {summary.stallTotalsMs && (
                            <p className="mt-1 font-mono text-base-content/50">
                                last range waits — conn {summary.stallTotalsMs["connWait"]} ms
                                · provider {summary.stallTotalsMs["providerWait"]} ms
                                · body {summary.stallTotalsMs["bodyDrain"]} ms
                                · consumer {summary.stallTotalsMs["consumerWait"]} ms
                                · client {summary.stallTotalsMs["clientWrite"]} ms
                            </p>
                        )}
                        <div className="mt-2 max-h-48 overflow-y-auto rounded border border-base-content/10 bg-base-300 p-2 font-mono text-[11px] leading-relaxed">
                            {recentEvents.length === 0 && (
                                <p className="text-base-content/45">No events recorded for this session yet.</p>
                            )}
                            {recentEvents.map(e => (
                                <div key={e.seq} className="flex gap-2">
                                    <span className="shrink-0 text-base-content/40">
                                        {new Date(e.at).toLocaleTimeString(undefined, { hour12: false })}.
                                        {String(e.at % 1000).padStart(3, "0")}
                                    </span>
                                    <span className="shrink-0 font-semibold text-base-content/70">{e.kind}</span>
                                    <span className="min-w-0 break-all text-base-content/55">{describeTraceEvent(e)}</span>
                                </div>
                            ))}
                        </div>
                    </>
                )}
            </Section>

            <div className="flex flex-wrap items-center gap-2 border-t border-base-content/10 pt-2">
                <Button size="xsmall" variant="ghost" onClick={copySnapshot}>
                    <Icon name="content_copy" className="!text-[16px]" />
                    Copy diagnostics JSON
                </Button>
                {copyNote && <span className="text-success">{copyNote}</span>}
                <span className="ml-auto text-base-content/40">player session {playerSession.slice(0, 8)}</span>
            </div>

            <details className="text-base-content/50">
                <summary className="cursor-pointer select-none">Player event log ({player.events.length})</summary>
                <div className="mt-1 max-h-40 overflow-y-auto rounded border border-base-content/10 bg-base-300 p-2 font-mono text-[11px] leading-relaxed">
                    {player.events.length === 0 && <p className="text-base-content/45">No events yet.</p>}
                    {[...player.events].reverse().map((e, i) => (
                        <div key={`${e.at}-${i}`} className="flex gap-2">
                            <span className="shrink-0 text-base-content/40">
                                {new Date(e.at).toLocaleTimeString(undefined, { hour12: false })}
                            </span>
                            <span className="shrink-0 font-semibold text-base-content/70">{e.kind}</span>
                            {e.detail && <span className="min-w-0 break-all text-base-content/55">{e.detail}</span>}
                        </div>
                    ))}
                </div>
            </details>
        </div>
    );
}

function smoothRate(
    prevRef: { current: { id: string, bytes: number, at: number, rate: number } | null },
    read: ActiveRead,
    now: number,
): number {
    const prev = prevRef.current;
    let rate = prev?.id === read.id ? prev.rate : 0;
    if (prev && prev.id === read.id && now > prev.at) {
        const dt = (now - prev.at) / 1000;
        const db = read.bytesRead - prev.bytes;
        if (dt > 0 && db >= 0) {
            rate = prev.rate * 0.4 + (db / dt) * 0.6;
        }
    }
    prevRef.current = { id: read.id, bytes: read.bytesRead, at: now, rate };
    return rate;
}

function droppedFrames(el: HTMLMediaElement | null): string {
    if (!(el instanceof HTMLVideoElement) || typeof el.getVideoPlaybackQuality !== "function") return "—";
    const quality = el.getVideoPlaybackQuality();
    return `${quality.droppedVideoFrames} / ${quality.totalVideoFrames}`;
}

function videoDimensions(el: HTMLMediaElement | null): string {
    if (!(el instanceof HTMLVideoElement) || el.videoWidth === 0) return "—";
    return `${el.videoWidth}×${el.videoHeight}`;
}

export function buildDiagnosticsSnapshot(
    props: MediaDiagnosticsProps,
    read: ActiveRead | null,
    liveStats: LiveStatsMessage | null,
    el: HTMLMediaElement | null,
    summary: ReturnType<typeof summarizeTrace> | null,
    traceEvents: StreamTraceEvent[] | null,
) {
    const { player, playerSession, fileName, filePath, mimeType, sizeBytes } = props;
    return {
        capturedAt: new Date().toISOString(),
        file: { name: fileName, path: filePath, mimeType, sizeBytes },
        playerSession,
        player: {
            status: player.status,
            buffering: player.buffering,
            attempts: player.attempts,
            maxAttempts: player.maxAttempts,
            startupMs: player.startupMs,
            error: player.error,
            events: player.events,
        },
        mediaElement: el ? {
            currentTime: el.currentTime,
            duration: Number.isFinite(el.duration) ? el.duration : null,
            paused: el.paused,
            seeking: el.seeking,
            ended: el.ended,
            playbackRate: el.playbackRate,
            readyState: readyStateLabel(el.readyState),
            networkState: networkStateLabel(el.networkState),
            buffered: formatTimeRanges(el.buffered),
            bufferAheadSeconds: bufferedAhead(el.buffered, el.currentTime),
            lastGoodTime: player.lastGoodTimeRef.current,
        } : null,
        backendRead: read ? {
            sessionId: read.id,
            bytesRead: read.bytesRead,
            bytesFetched: read.bytesFetched ?? null,
            currentOffset: read.currentOffset,
            fileSize: read.fileSize,
            startedAt: new Date(read.startedAt).toISOString(),
            lastActivityAt: new Date(read.lastActivityAt).toISOString(),
            clientUserAgent: read.clientUserAgent ?? null,
            providers: read.providers,
        } : null,
        liveStats: liveStats ? {
            activeReads: liveStats.activeReads,
            bytesServedPerMinute: liveStats.bytesServedPerMinute,
            inFlightArticleBytes: liveStats.inFlightArticleBytes ?? null,
        } : null,
        // The signed URL, downloadKey, client IP, and full segment ids are
        // deliberately excluded from copied diagnostics.
        traceSummary: summary,
        recentTraceEvents: traceEvents ? redactTraceEvents(traceEvents.slice(-RECENT_EVENTS_SHOWN)) : null,
    };
}

function Section({ title, children }: { title: string, children: React.ReactNode }) {
    return (
        <section>
            <h4 className="mb-1.5 text-[11px] font-semibold uppercase tracking-wide text-base-content/50">
                {title}
            </h4>
            {children}
        </section>
    );
}

function Grid({ children }: { children: React.ReactNode }) {
    return <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 md:grid-cols-3">{children}</div>;
}

function Field({ label, value, mono = false, action }: {
    label: string,
    value: string,
    mono?: boolean,
    action?: React.ReactNode,
}) {
    return (
        <div className="flex min-w-0 items-baseline gap-1">
            <span className="shrink-0 text-base-content/45">{label}</span>
            <span className={`min-w-0 break-all text-base-content ${mono ? "font-mono" : ""}`}>{value}</span>
            {action}
        </div>
    );
}

function CopyButton({ label, onCopy }: { label: string, onCopy: () => void }) {
    return (
        <button
            type="button"
            className="btn btn-ghost btn-xs btn-circle -my-1"
            title={label}
            aria-label={label}
            onClick={onCopy}
        >
            <Icon name="content_copy" className="!text-[13px]" />
        </button>
    );
}
