import { withUrlBase } from "~/utils/url-base";
import type { Route } from "./+types/route";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useWebsocketTopics } from "~/utils/shared-websocket";
import { DndContext, PointerSensor, KeyboardSensor, useSensor, useSensors, closestCenter, type DragEndEvent } from "@dnd-kit/core";
import { SortableContext, arrayMove, verticalListSortingStrategy, sortableKeyboardCoordinates } from "@dnd-kit/sortable";
import { LiveTiles } from "./components/live-tiles/live-tiles";
import { LiveReadsPanel } from "./components/live-reads-panel/live-reads-panel";
import { ActivityHeatmap } from "./components/activity-heatmap/activity-heatmap";
import { ThroughputChart } from "./components/throughput-chart/throughput-chart";
import { LatencyHistogram } from "./components/latency-histogram/latency-histogram";
import { ErrorDonut } from "./components/error-donut/error-donut";
import { ProviderScoreboard } from "./components/provider-scoreboard/provider-scoreboard";
import { IndexerScoreboard } from "./components/indexer-scoreboard/indexer-scoreboard";
import { IndexerApiUsage } from "./components/indexer-api-usage/indexer-api-usage";
import { SessionsBlock } from "./components/sessions-block/sessions-block";
import { CatalogueBlock } from "./components/catalogue-block/catalogue-block";
import { LifetimeBlock } from "./components/lifetime-block/lifetime-block";
import { RecordsBlock } from "./components/records-block/records-block";
import { FailoverSaves } from "./components/failover-saves/failover-saves";
import { ArrHealth } from "./components/arr-health/arr-health";
import { SortableRow } from "./components/sortable-row/sortable-row";
import { backendClient, type ArrHealthResponse } from "~/clients/backend-client.server";
import { useRowOrder } from "./utils/use-row-order";
import { hasConfiguredIndexers } from "./utils/has-configured-indexers";
import { hasConfiguredArrs, isArrHealthEnabled } from "./utils/has-configured-arrs";
import {
    EMPTY_OVERVIEW_STATS,
    mergeOverviewStats,
    mergeProviderCircuitBreakers,
    type LiveStatsMessage,
    type OverviewStatsResponse,
    type OverviewWindow,
} from "./utils/merge-overview-stats";

const topicNames = {
    liveStats: 'ls',
};
const topicSubscriptions = {
    [topicNames.liveStats]: 'state',
} as const;

const WINDOWS: { value: OverviewWindow, label: string }[] = [
    { value: "1h", label: "1h" },
    { value: "24h", label: "24h" },
    { value: "7d", label: "7d" },
    { value: "30d", label: "30d" },
    { value: "all", label: "All" },
];

const DEFAULT_ROW_ORDER = [
    "liveTiles",
    "throughput",
    "providers",
    "activity",
    "latency",
    "errorsSessions",
    "failover",
    "arrHealth",
    "indexers",
    "indexerApiUsage",
    "recordsCatalogue",
    "lifetime",
] as const;

/** Shell-only loader — stats load client-side in sections so first paint is instant. */
export async function loader() {
    const config = await backendClient.getConfig(["indexers.instances", "arr.instances", "arr.health-enabled"]);
    return {
        stats: null as OverviewStatsResponse | null,
        hasConfiguredIndexers: hasConfiguredIndexers(
            config.find(item => item.configName === "indexers.instances")?.configValue,
        ),
        hasConfiguredArrs: isArrHealthEnabled(
            config.find(item => item.configName === "arr.health-enabled")?.configValue,
        ) && hasConfiguredArrs(config.find(item => item.configName === "arr.instances")?.configValue),
    };
}

export function shouldRevalidate() {
    return false;
}

function Skeleton({ height = 120 }: { height?: number }) {
    return (
        <div
            className="skeleton w-full rounded-box"
            style={{ minHeight: height }}
            aria-hidden="true"
        />
    );
}

export default function Overview({ loaderData }: Route.ComponentProps) {
    const [stats, setStats] = useState<OverviewStatsResponse>(EMPTY_OVERVIEW_STATS);
    const [window, setWindow] = useState<OverviewWindow>("24h");
    const [editMode, setEditMode] = useState(false);
    const [connectedAt, setConnectedAt] = useState<number | null>(null);
    const [lastLiveStatsAt, setLastLiveStatsAt] = useState<number | null>(null);
    const [transportFailed, setTransportFailed] = useState(false);
    const [liveClock, setLiveClock] = useState(() => Date.now());
    const [windowLoaded, setWindowLoaded] = useState(false);
    const [detailLoaded, setDetailLoaded] = useState(false);
    const [staticLoaded, setStaticLoaded] = useState(false);
    const [arrHealth, setArrHealth] = useState<ArrHealthResponse | null>(null);
    const [arrHealthLoaded, setArrHealthLoaded] = useState(false);
    const { order, save, reset } = useRowOrder(DEFAULT_ROW_ORDER);
    const editModeRef = useRef(editMode);
    editModeRef.current = editMode;

    const liveTiles = stats.tiles;
    const isLongWindow = window === "7d" || window === "30d" || window === "all";

    // Window section: load on mount / window change, poll every 30s while visible.
    useEffect(() => {
        let cancelled = false;
        setWindowLoaded(false);
        if (isLongWindow) setDetailLoaded(true);
        else setDetailLoaded(false);

        const fetchWindow = async () => {
            if (typeof document !== "undefined" && document.hidden) return;
            try {
                const res = await fetch(withUrlBase(`/api/get-overview-stats?window=${window}&sections=window`));
                if (!res.ok || cancelled) return;
                // /api/get-overview-stats returns OverviewStatsResponse
                const data = await res.json() as OverviewStatsResponse;
                if (cancelled) return;
                setStats(s => mergeOverviewStats(s, data));
                setWindowLoaded(true);
            } catch { /* network blip, retry next tick */ }
        };

        void fetchWindow(); // fire-and-forget: polled on interval below
        const interval = setInterval(() => {
            if (editModeRef.current) return;
            void fetchWindow();
        }, 30_000);
        const onVisible = () => {
            if (editModeRef.current) return;
            if (!document.hidden) void fetchWindow();
        };
        document.addEventListener("visibilitychange", onVisible);
        return () => {
            cancelled = true;
            clearInterval(interval);
            document.removeEventListener("visibilitychange", onVisible);
        };
    }, [window, isLongWindow]);

    // Arr Health: poll on the same 30s cadence, only when Arr instances are configured.
    useEffect(() => {
        if (!loaderData.hasConfiguredArrs) return;
        let cancelled = false;
        setArrHealthLoaded(false);

        const fetchArrHealth = async () => {
            if (typeof document !== "undefined" && document.hidden) return;
            try {
                const res = await fetch(withUrlBase(`/api/get-arr-health?window=${window}`));
                if (!res.ok || cancelled) return;
                const data = await res.json() as ArrHealthResponse;
                if (cancelled) return;
                setArrHealth(data);
                setArrHealthLoaded(true);
            } catch { /* network blip, retry next tick */ }
        };

        void fetchArrHealth();
        const interval = setInterval(() => {
            if (editModeRef.current) return;
            void fetchArrHealth();
        }, 30_000);
        const onVisible = () => {
            if (editModeRef.current) return;
            if (!document.hidden) void fetchArrHealth();
        };
        document.addEventListener("visibilitychange", onVisible);
        return () => {
            cancelled = true;
            clearInterval(interval);
            document.removeEventListener("visibilitychange", onVisible);
        };
    }, [window, loaderData.hasConfiguredArrs]);

    // Detail (latency + errors): once per 24h window selection — not on the 30s poll.
    useEffect(() => {
        if (isLongWindow) return;
        let cancelled = false;
        void (async () => {
            try {
                const res = await fetch(withUrlBase(`/api/get-overview-stats?window=${window}&sections=detail`));
                if (!res.ok || cancelled) return;
                // /api/get-overview-stats returns OverviewStatsResponse
                const data = await res.json() as OverviewStatsResponse;
                if (cancelled) return;
                setStats(s => mergeOverviewStats(s, data));
                setDetailLoaded(true);
            } catch { /* ignore */ }
        })();
        return () => { cancelled = true; };
    }, [window, isLongWindow]);

    // Static blocks: once per page visit.
    useEffect(() => {
        let cancelled = false;
        void (async () => {
            try {
                const res = await fetch(withUrlBase(`/api/get-overview-stats?window=${window}&sections=static`));
                if (!res.ok || cancelled) return;
                // /api/get-overview-stats returns OverviewStatsResponse
                const data = await res.json() as OverviewStatsResponse;
                if (cancelled) return;
                setStats(s => mergeOverviewStats(s, data));
                setStaticLoaded(true);
            } catch { /* ignore */ }
        })();
        return () => { cancelled = true; };
    }, []); // eslint-disable-line react-hooks/exhaustive-deps -- once per visit

    const onWsMessage = useCallback((topic: string, message: string) => {
        if (topic !== topicNames.liveStats) return;
        try {
            // live-stats websocket message ('ls' topic) carries LiveStatsMessage
            const live = JSON.parse(message) as LiveStatsMessage;
            setStats(s => ({
                ...s,
                tiles: {
                    activeReads: live.activeReads,
                    articlesPerMinute: live.articlesPerMinute,
                    errorsPerMinute: live.errorsPerMinute,
                    bytesServedPerMinute: live.bytesServedPerMinute,
                    inFlightArticleBytes: live.inFlightArticleBytes ?? s.tiles.inFlightArticleBytes ?? 0,
                    inFlightArticleBudgetBytes: live.inFlightArticleBudgetBytes ?? s.tiles.inFlightArticleBudgetBytes ?? 0,
                    inFlightArticleThrottleEvents: live.inFlightArticleThrottleEvents ?? s.tiles.inFlightArticleThrottleEvents ?? 0,
                },
                providers: mergeProviderCircuitBreakers(s.providers, live.providerBreakers),
            }));
            setLastLiveStatsAt(Date.now());
            setTransportFailed(false);
        } catch { /* ignore */ }
    }, []);

    useEffect(() => {
        const interval = setInterval(() => setLiveClock(Date.now()), 5_000);
        return () => clearInterval(interval);
    }, []);

    useWebsocketTopics(topicSubscriptions, onWsMessage, {
        enabled: !editMode,
        onOpen: () => setConnectedAt(Date.now()),
        onClose: () => {
            setConnectedAt(null);
            setTransportFailed(true);
        },
    });

    const heartbeatAge = liveClock - (lastLiveStatsAt ?? connectedAt ?? liveClock);
    const liveStatsStale = transportFailed || (connectedAt !== null && heartbeatAge > 15_000);
    const metricsError = stats.metricsHealth?.lastFlushError;
    const droppedMetrics = stats.metricsHealth?.dropped ?? 0;

    const rowContent = useMemo<Record<string, ReactNode>>(() => ({
        liveTiles: <LiveTiles tiles={liveTiles} />,
        throughput: windowLoaded
            ? (
                <ThroughputChart
                    points={stats.throughput}
                    totalArticles={stats.totalArticles}
                    totalMisses={stats.totalMisses}
                    totalErrors={stats.totalErrors}
                    totalBytesServed={stats.sessions.totalBytesServed}
                    bucketSizeMs={stats.throughputBucketSizeMs}
                    window={window}
                />
            )
            : <Skeleton height={180} />,
        activity: windowLoaded
            ? (
                <ActivityHeatmap
                    maxCell={stats.heatmap.maxCell}
                    mode={stats.heatmap.mode}
                    windowStartMs={stats.heatmap.windowStartMs}
                    windowEndMs={stats.heatmap.windowEndMs}
                    bucketSizeMs={stats.heatmap.bucketSizeMs}
                    cells={stats.heatmap.cells}
                />
            )
            : <Skeleton height={140} />,
        latency: !isLongWindow
            ? (detailLoaded
                ? (
                    <LatencyHistogram
                        p50Ms={stats.latency.p50Ms}
                        p95Ms={stats.latency.p95Ms}
                        p99Ms={stats.latency.p99Ms}
                        samples={stats.latency.samples}
                        buckets={stats.latency.buckets}
                    />
                )
                : <Skeleton height={160} />)
            : null,
        errorsSessions: (
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                {!isLongWindow && (
                    detailLoaded
                        ? <ErrorDonut errors={stats.errors} />
                        : <Skeleton height={160} />
                )}
                {windowLoaded
                    ? <SessionsBlock sessions={stats.sessions} window={window} />
                    : <Skeleton height={160} />}
            </div>
        ),
        providers: windowLoaded
            ? <ProviderScoreboard providers={stats.providers} window={window} />
            : <Skeleton height={160} />,
        failover: windowLoaded
            ? <FailoverSaves failover={stats.failover} window={window} />
            : <Skeleton height={180} />,
        arrHealth: loaderData.hasConfiguredArrs
            ? (arrHealthLoaded && arrHealth
                ? <ArrHealth data={arrHealth} window={window} />
                : <Skeleton height={140} />)
            : null,
        indexers: loaderData.hasConfiguredIndexers && staticLoaded
            ? <IndexerScoreboard indexers={stats.indexers} />
            : loaderData.hasConfiguredIndexers ? <Skeleton height={140} /> : null,
        indexerApiUsage: loaderData.hasConfiguredIndexers && staticLoaded
            ? <IndexerApiUsage rows={stats.indexerApiUsage} />
            : loaderData.hasConfiguredIndexers ? <Skeleton height={120} /> : null,
        recordsCatalogue: (
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                {staticLoaded
                    ? <RecordsBlock records={stats.records} />
                    : <Skeleton height={120} />}
                {staticLoaded
                    ? <CatalogueBlock catalogue={stats.catalogue} />
                    : <Skeleton height={120} />}
            </div>
        ),
        lifetime: staticLoaded
            ? <LifetimeBlock lifetime={stats.lifetime} />
            : <Skeleton height={120} />,
    }), [liveTiles, stats, window, isLongWindow, windowLoaded, detailLoaded, staticLoaded, editMode, loaderData.hasConfiguredIndexers, loaderData.hasConfiguredArrs, arrHealth, arrHealthLoaded]);

    const visibleOrder = useMemo(
        () => order.filter(id => {
            if (!loaderData.hasConfiguredIndexers && (id === "indexers" || id === "indexerApiUsage")) return false;
            if (!loaderData.hasConfiguredArrs && id === "arrHealth") return false;
            return true;
        }),
        [loaderData.hasConfiguredIndexers, loaderData.hasConfiguredArrs, order],
    );

    const sensors = useSensors(
        useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
        useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
    );

    const onDragEnd = (event: DragEndEvent) => {
        const { active, over } = event;
        if (!over || active.id === over.id) return;
        const oldIndex = order.indexOf(String(active.id));
        const newIndex = order.indexOf(String(over.id));
        if (oldIndex < 0 || newIndex < 0) return;
        save(arrayMove(order, oldIndex, newIndex));
    };

    return (
        <div className="mx-auto flex w-full max-w-[1680px] flex-col gap-4 p-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="m-0 text-xl font-semibold tracking-tight text-base-content">Overview</h2>
                <div className="inline-flex flex-wrap items-center gap-2">
                    {editMode && (
                        <button
                            type="button"
                            className="btn btn-ghost btn-sm"
                            onClick={reset}
                            title="Restore default order">
                            Reset
                        </button>
                    )}
                    <button
                        type="button"
                        className={`btn btn-sm ${editMode ? "btn-primary" : "btn-ghost"}`}
                        onClick={() => setEditMode(v => !v)}
                        aria-pressed={editMode}
                        title={editMode ? "Done editing layout" : "Reorder widgets"}>
                        {editMode ? "Done" : "Edit layout"}
                    </button>
                    <div className="join">
                        {WINDOWS.map(w => (
                            <button
                                key={w.value}
                                type="button"
                                role="tab"
                                aria-selected={window === w.value}
                                className={`btn btn-sm join-item ${window === w.value ? "btn-primary" : "btn-ghost"}`}
                                onClick={() => setWindow(w.value)}>{w.label}</button>
                        ))}
                    </div>
                </div>
            </div>

            {((!editMode && liveStatsStale) || metricsError || droppedMetrics > 0) && (
                <div
                    role="alert"
                    className={`alert text-xs ${
                        metricsError ? "alert-error" : "alert-warning"
                    }`}>
                    {metricsError
                        ? `Metrics storage is unavailable: ${metricsError}`
                        : droppedMetrics > 0
                            ? `${droppedMetrics.toLocaleString()} metrics were dropped before they could be stored.`
                            : "Live updates are reconnecting. Values below may be stale."}
                </div>
            )}

            <div className="flex min-w-0 flex-col items-stretch gap-4 xl:flex-row">
                <div className="flex min-w-0 flex-1 flex-col gap-4">
                    <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
                        <SortableContext items={visibleOrder} strategy={verticalListSortingStrategy}>
                            {visibleOrder.map(id => {
                                const content = rowContent[id];
                                if (!content) return null;
                                return (
                                    <SortableRow key={id} id={id} editMode={editMode}>
                                        {content}
                                    </SortableRow>
                                );
                            })}
                        </SortableContext>
                    </DndContext>
                </div>
                <aside className="flex w-full shrink-0 xl:w-80 xl:self-stretch">
                    <LiveReadsPanel paused={editMode} />
                </aside>
            </div>
        </div>
    );
}
