import { useMemo, useState } from "react";
import styles from "./failover-saves.module.css";
import type { FailoverBlock, OverviewWindow } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type FailoverSavesProps = {
    failover: FailoverBlock,
    window: OverviewWindow,
}

export function FailoverSaves({ failover, window }: FailoverSavesProps) {
    const {
        articlesRecovered, previousArticlesRecovered, segmentsCovered,
        readsSaved, readSessions, totalArticles,
        rescuedBy, rescuedFrom, reasons, buckets, bucketSizeMs,
    } = failover;

    const maxSaves = useMemo(() => Math.max(1, ...rescuedBy.map(p => p.saves)), [rescuedBy]);
    const maxMisses = useMemo(() => Math.max(1, ...rescuedFrom.map(p => p.misses)), [rescuedFrom]);

    const bucketTotals = useMemo(
        () => buckets.map(b => ({ bucket: b.bucket, total: b.counts.reduce((s, c) => s + c, 0) })),
        [buckets]
    );
    const peak = useMemo(() => {
        let best: { bucket: number, total: number } | null = null;
        for (const b of bucketTotals) if (!best || b.total > best.total) best = b;
        return best;
    }, [bucketTotals]);

    const reasonTotal = useMemo(() => reasons.reduce((s, r) => s + r.count, 0), [reasons]);
    const momentum = useMemo(
        () => computeMomentum(articlesRecovered, previousArticlesRecovered),
        [articlesRecovered, previousArticlesRecovered]
    );

    const hasData = articlesRecovered > 0 && rescuedBy.length > 0;
    const sinceLabel = window === "all" ? "all time" : `last ${window}`;
    const saveRate = totalArticles > 0 ? articlesRecovered / totalArticles : 0;
    const oneIn = readsSaved > 0 ? Math.max(1, Math.round(readSessions / readsSaved)) : 0;
    const topHero = rescuedBy[0];
    const topVillain = rescuedFrom[0];
    const hasContrast = Boolean(topHero && topVillain);
    const multiProvider = rescuedBy.length >= 2;

    return (
        <section className="card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
            <div className="card-body gap-3 p-4">
            <div>
                <h3 className="card-title text-base">Backup rescues</h3>
                <p className="text-xs text-base-content/50">
                    When your main provider misses a piece mid-session, a backup delivers it before anything fails ({sinceLabel})
                </p>
            </div>

            {hasData ? (
                <>
                    <div className="mb-3.5 flex items-center gap-4">
                        <span className="shrink-0 text-[40px] leading-[0.95] font-bold tracking-tight text-base-content tabular-nums">{formatNumber(articlesRecovered)}</span>
                        <div className="min-w-0 flex-1">
                            <div className="text-[13px] font-medium text-base-content/80">segments your backups rescued</div>
                            <div className="mt-0.5 text-xs text-base-content/50">
                                {totalArticles > 0 ? (
                                    <>that&rsquo;s <strong className="font-semibold text-base-content">{formatSmallPercent(saveRate)}</strong> of all fetches a backup had to cover</>
                                ) : (
                                    <>your stack self-healed every time it mattered</>
                                )}
                            </div>
                        </div>
                        {momentum && (
                            <div
                                className={`badge badge-lg h-auto flex-col gap-0.5 border border-base-content/10 bg-base-200 px-3 py-2 ${
                                    momentum.neutral
                                        ? ""
                                        : momentum.good
                                            ? "text-success"
                                            : "text-error"
                                }`}
                                title={`Failover load ${momentum.label} vs the previous ${window}`}>
                                <span className="text-sm font-bold leading-none">{momentum.arrow}</span>
                                <span className="font-mono text-sm font-bold tabular-nums leading-none">{momentum.text}</span>
                                <span className="text-[9px] font-normal tracking-wide text-base-content/50 uppercase">
                                    vs prev {window}
                                </span>
                            </div>
                        )}
                    </div>

                    {readsSaved > 0 && (
                        <div className="mb-4 text-xs text-base-content/80">
                            <strong className="font-bold text-base-content">1 in {oneIn}</strong> sessions needed a backup
                            <span className="text-base-content/50">
                                {" "}· {formatNumber(readsSaved)} of {formatNumber(readSessions)} rescued
                            </span>
                        </div>
                    )}

                    {hasContrast && (
                        <div className="mb-4 grid grid-cols-1 gap-2.5 sm:grid-cols-2">
                            {topHero && (
                                <div className="min-w-0 rounded-box border border-base-content/10 bg-base-200 p-3">
                                    <div className="text-[10px] font-medium tracking-wide text-base-content/50 uppercase">Most saves</div>
                                    <div className="truncate text-sm font-semibold text-base-content" title={topHero.provider}>
                                        {topHero.nickname?.trim() || topHero.provider}
                                    </div>
                                    <div className="mt-1 font-mono text-sm font-semibold tabular-nums text-success">
                                        {formatNumber(topHero.saves)} <span className="font-normal text-base-content/50">saves</span>
                                    </div>
                                </div>
                            )}
                            {topVillain && (
                                <div className="min-w-0 rounded-box border border-base-content/10 bg-base-200 p-3">
                                    <div className="text-[10px] font-medium tracking-wide text-base-content/50 uppercase">Most misses</div>
                                    <div className="truncate text-sm font-semibold text-base-content" title={topVillain.provider}>
                                        {topVillain.nickname?.trim() || topVillain.provider}
                                    </div>
                                    <div className="mt-1 font-mono text-sm font-semibold tabular-nums text-error">
                                        {formatNumber(topVillain.misses)} <span className="font-normal text-base-content/50">misses</span>
                                    </div>
                                </div>
                            )}
                        </div>
                    )}

                    {reasons.length > 0 && (
                        <div className="mb-4">
                            <div className="mb-1.5 text-[10px] tracking-wide text-base-content/50 uppercase">Why they missed</div>
                            <div className={styles.reasonBar}>
                                {reasons.map(r => {
                                    const meta = reasonMeta(r.status);
                                    const width = reasonTotal > 0 ? (r.count / reasonTotal) * 100 : 0;
                                    return (
                                        <span
                                            key={r.status}
                                            className={`${styles.reasonSeg} ${meta.cls}`}
                                            style={{ width: `${width.toFixed(1)}%` }}
                                            title={`${meta.label}: ${formatNumber(r.count)} (${width.toFixed(0)}%)`}
                                        />
                                    );
                                })}
                            </div>
                            <div className="mt-2 flex flex-wrap gap-1.5">
                                {reasons.map(r => {
                                    const meta = reasonMeta(r.status);
                                    return (
                                        <span key={r.status} className="badge badge-ghost badge-sm gap-1.5">
                                            <span className={`h-1.5 w-1.5 rounded-full ${meta.cls}`} />
                                            {meta.label} <strong className="font-mono">{formatNumber(r.count)}</strong>
                                        </span>
                                    );
                                })}
                            </div>
                        </div>
                    )}

                    {rescuedFrom.length > 0 && (
                        <>
                            <div className="mt-1 flex justify-between border-b border-base-content/10 pb-1.5 text-[10px] tracking-wide text-base-content/50 uppercase">
                                <span>Needed rescuing</span>
                                <span>misses</span>
                            </div>
                            <div className="mb-2 flex flex-col">
                                {rescuedFrom.map(p => {
                                    const width = (p.misses / maxMisses) * 100;
                                    const share = segmentsCovered > 0 ? (p.misses / segmentsCovered) * 100 : 0;
                                    return (
                                        <div key={p.provider} className="flex items-center gap-3 border-b border-base-content/10 py-2 last:border-b-0" title={p.nickname?.trim() || p.provider}>
                                            <span className="w-[130px] shrink-0 truncate text-[13px] font-medium text-base-content">{p.nickname?.trim() || p.provider}</span>
                                            <span className="h-2 min-w-[30px] flex-1 overflow-hidden rounded bg-base-200">
                                                <span className={`${styles.barFill} ${styles.barFillBad}`} style={{ width: `${width.toFixed(1)}%` }} />
                                            </span>
                                            <span className="w-[52px] shrink-0 text-right text-[13px] font-semibold text-base-content tabular-nums">{formatNumber(p.misses)}</span>
                                            <span className="w-9 shrink-0 text-right text-[11px] text-base-content/50 tabular-nums">{share.toFixed(0)}%</span>
                                        </div>
                                    );
                                })}
                            </div>
                        </>
                    )}

                    {multiProvider ? (
                        <>
                            <div className="mt-1 flex justify-between border-b border-base-content/10 pb-1.5 text-[10px] tracking-wide text-base-content/50 uppercase">
                                <span>Rescued by</span>
                                <span>saves</span>
                            </div>
                            <div className="mb-2 flex flex-col">
                                {rescuedBy.map(p => {
                                    const width = (p.saves / maxSaves) * 100;
                                    const share = articlesRecovered > 0 ? (p.saves / articlesRecovered) * 100 : 0;
                                    return (
                                        <div key={p.provider} className="flex items-center gap-3 border-b border-base-content/10 py-2 last:border-b-0" title={p.nickname?.trim() || p.provider}>
                                            <span className="w-[130px] shrink-0 truncate text-[13px] font-medium text-base-content">{p.nickname?.trim() || p.provider}</span>
                                            <span className="h-2 min-w-[30px] flex-1 overflow-hidden rounded bg-base-200">
                                                <span className={styles.barFill} style={{ width: `${width.toFixed(1)}%` }} />
                                            </span>
                                            <span className="w-[52px] shrink-0 text-right text-[13px] font-semibold text-base-content tabular-nums">{formatNumber(p.saves)}</span>
                                            <span className="w-9 shrink-0 text-right text-[11px] text-base-content/50 tabular-nums">{share.toFixed(0)}%</span>
                                        </div>
                                    );
                                })}
                            </div>
                        </>
                    ) : (!hasContrast && topHero) ? (
                        <div className="mt-1 flex flex-wrap items-baseline gap-1.5 rounded-box border border-base-content/10 bg-base-200 px-3.5 py-3 text-[13px] text-base-content/80">
                            Every rescue came through <strong className="font-semibold text-base-content">{topHero.nickname?.trim() || topHero.provider}</strong>
                        </div>
                    ) : null}

                    {bucketTotals.length >= 3 && (
                        <SavesTrend bucketTotals={bucketTotals} bucketSizeMs={bucketSizeMs} />
                    )}

                    {peak && peak.total > 0 && (
                        <div className="mt-3 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-[11px] text-base-content/50">
                            <span>Busiest {formatBucket(peak.bucket, bucketSizeMs)} ({formatNumber(peak.total)})</span>
                        </div>
                    )}
                </>
            ) : (
                <div className="py-6 text-center text-xs text-base-content/50">
                    No backup rescues in this window.
                    <div className="mt-1.5 text-base-content/40">
                        Every segment was served on the first try. When a provider misses, a backup steps in.
                        You&rsquo;ll see who failed, who covered, and why, right here.
                    </div>
                </div>
            )}
            </div>
        </section>
    );
}

const VB_W = 800;
const VB_H = 120;
const TOP_PAD = 8;
const BOT_PAD = 4;

function SavesTrend({ bucketTotals, bucketSizeMs }: { bucketTotals: { bucket: number, total: number }[], bucketSizeMs: number }) {
    const [hoverIdx, setHoverIdx] = useState<number | null>(null);

    const series = useMemo(() => {
        let run = 0;
        return bucketTotals.map(b => {
            run += b.total;
            return { bucket: b.bucket, delta: b.total, cum: run };
        });
    }, [bucketTotals]);

    const total = series.length ? series[series.length - 1].cum : 0;

    const { linePath, areaPath, xPercent, yPercent } = useMemo(() => {
        const n = series.length;
        if (n === 0) return { linePath: "", areaPath: "", xPercent: (_: number) => 0, yPercent: (_: number) => 0 };
        const max = Math.max(1, total);
        const xStep = n > 1 ? VB_W / (n - 1) : 0;
        const innerH = VB_H - TOP_PAD - BOT_PAD;
        const baseline = VB_H - BOT_PAD;
        const x = (i: number) => i * xStep;
        const y = (v: number) => baseline - (v / max) * innerH;
        const line = series.map((p, i) => `${i === 0 ? "M" : "L"}${x(i).toFixed(1)},${y(p.cum).toFixed(1)}`).join(" ");
        const area = `M0,${baseline.toFixed(1)} ${series.map((p, i) => `L${x(i).toFixed(1)},${y(p.cum).toFixed(1)}`).join(" ")} L${x(n - 1).toFixed(1)},${baseline.toFixed(1)} Z`;
        return {
            linePath: line,
            areaPath: area,
            xPercent: (i: number) => n > 1 ? (i / (n - 1)) * 100 : 50,
            yPercent: (v: number) => (y(v) / VB_H) * 100,
        };
    }, [series, total]);

    const xTicks = useMemo(() => {
        const n = series.length;
        if (n === 0) return [];
        const count = Math.min(4, n);
        return Array.from({ length: count }, (_, i) => {
            const idx = count < 2 ? 0 : Math.round((n - 1) * (i / (count - 1)));
            return { idx, label: formatBucket(series[idx].bucket, bucketSizeMs) };
        });
    }, [series, bucketSizeMs]);

    const onMove = (clientX: number, target: HTMLElement) => {
        const n = series.length;
        if (n === 0) return;
        const rect = target.getBoundingClientRect();
        const rel = (clientX - rect.left) / rect.width;
        const idx = Math.round(rel * (n - 1));
        setHoverIdx(Math.max(0, Math.min(n - 1, idx)));
    };

    if (total === 0) return null;
    const hover = hoverIdx !== null ? series[hoverIdx] : null;
    const midY = (TOP_PAD + (VB_H - BOT_PAD)) / 2;

    return (
        <div className="mt-3.5">
            <div className="mb-2 flex items-baseline justify-between gap-3">
                <span className="text-[10px] tracking-wide text-base-content/50 uppercase">Saves over time</span>
                <span className="min-w-0 text-right text-[11px] text-base-content/50 tabular-nums">
                    {hover ? (
                        <>
                            <strong className="font-semibold text-base-content">{formatBucket(hover.bucket, bucketSizeMs)}</strong>
                            &nbsp;·&nbsp;{formatNumber(hover.cum)} of {formatNumber(total)} saved
                            {hover.delta > 0 && <> · +{formatNumber(hover.delta)}</>}
                        </>
                    ) : (
                        <>cumulative · hover for detail</>
                    )}
                </span>
            </div>
            <div className={styles.trendPlot}>
                <div className="flex h-[120px] w-[30px] shrink-0 flex-col items-end justify-between text-[10px] text-base-content/50 tabular-nums select-none">
                    <span>{formatNumber(total)}</span>
                    <span>{formatNumber(Math.round(total / 2))}</span>
                    <span>0</span>
                </div>
                <div
                    className={styles.trendArea}
                    onMouseMove={e => onMove(e.clientX, e.currentTarget)}
                    onMouseLeave={() => setHoverIdx(null)}
                    onTouchStart={e => { const t = e.touches[0]; if (t) onMove(t.clientX, e.currentTarget); }}
                    onTouchMove={e => { const t = e.touches[0]; if (t) onMove(t.clientX, e.currentTarget); }}
                >
                    <svg viewBox={`0 0 ${VB_W} ${VB_H}`} preserveAspectRatio="none" className={styles.trendSvg}>
                        <line x1="0" y1={(VB_H - BOT_PAD).toFixed(1)} x2={VB_W} y2={(VB_H - BOT_PAD).toFixed(1)} className={styles.trendGrid} />
                        <line x1="0" y1={midY.toFixed(1)} x2={VB_W} y2={midY.toFixed(1)} className={styles.trendGrid} />
                        <line x1="0" y1={TOP_PAD.toFixed(1)} x2={VB_W} y2={TOP_PAD.toFixed(1)} className={styles.trendGrid} />
                        <path d={areaPath} className={styles.trendFill} />
                        <path d={linePath} className={styles.trendLine} />
                    </svg>
                    {hover && hoverIdx !== null && (
                        <>
                            <div className={styles.trendCrosshair} style={{ left: `${xPercent(hoverIdx)}%` }} />
                            <div className={styles.trendDot} style={{ left: `${xPercent(hoverIdx)}%`, top: `${yPercent(hover.cum)}%` }} />
                        </>
                    )}
                </div>
            </div>
            <div className="relative mt-1.5 ml-10 h-3.5 text-[10px] text-base-content/50 tabular-nums select-none">
                {xTicks.map(t => (
                    <span key={t.idx} className="absolute top-0 -translate-x-1/2 whitespace-nowrap" style={{ left: `${xPercent(t.idx)}%` }}>{t.label}</span>
                ))}
            </div>
        </div>
    );
}

function formatSmallPercent(fraction: number): string {
    const pct = fraction * 100;
    if (pct <= 0) return "0%";
    if (pct >= 1) return `${pct.toFixed(1)}%`;
    if (pct >= 0.01) return `${pct.toFixed(2)}%`;
    return "<0.01%";
}

type Momentum = { arrow: string, text: string, label: string, good: boolean, neutral?: boolean };

function computeMomentum(current: number, previous: number | null): Momentum | null {
    if (previous === null) return null;
    if (previous === 0 && current === 0) return null;
    if (previous === 0) return { arrow: "↑", text: "new", label: "up from none", good: false, neutral: true };
    const delta = ((current - previous) / previous) * 100;
    if (Math.abs(delta) < 3) return { arrow: "→", text: "flat", label: "holding flat", good: true };
    const up = delta > 0;
    const pct = `${Math.abs(delta).toFixed(0)}%`;
    return { arrow: up ? "↑" : "↓", text: pct, label: `${up ? "up" : "down"} ${pct}`, good: !up };
}

function reasonMeta(status: string): { label: string, cls: string } {
    switch (status) {
        case "Missing": return { label: "missing", cls: styles.rMissing };
        case "Timeout": return { label: "timed out", cls: styles.rTimeout };
        case "Auth": return { label: "auth", cls: styles.rAuth };
        case "Network": return { label: "network", cls: styles.rNetwork };
        case "Corrupt": return { label: "corrupt", cls: styles.rCorrupt };
        // Unclassified failures must never read as "missing" — that implies a clean
        // provider 430, which this isn't.
        case "Other": return { label: "other (unclassified)", cls: styles.rOther };
        default: return { label: status.toLowerCase(), cls: styles.rOther };
    }
}

const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

function formatBucket(ms: number, bucketSizeMs: number): string {
    const d = new Date(ms);
    const day = DAYS[d.getDay()];
    const dd = String(d.getDate()).padStart(2, "0");
    const mon = MONTHS[d.getMonth()];
    if (bucketSizeMs <= 3_600_000) {
        const hh = String(d.getHours()).padStart(2, "0");
        return `${day} ${hh}:00`;
    }
    if (bucketSizeMs >= 7 * 86_400_000) {
        return `week of ${dd} ${mon}`;
    }
    return `${day} ${dd} ${mon}`;
}
