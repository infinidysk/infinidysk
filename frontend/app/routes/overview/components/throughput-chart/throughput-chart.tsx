import { useCallback, useEffect, useMemo, useState } from "react";
import styles from "./throughput-chart.module.css";
import type { OverviewWindow, ThroughputPoint } from "~/clients/backend-client.server";
import { formatBytes, formatNumber } from "../../utils/format";

export type ThroughputChartProps = {
  points: ThroughputPoint[];
  totalArticles: number;
  totalClientArticles: number;
  totalMisses: number;
  totalErrors: number;
  totalBytesServed: number;
  bucketSizeMs: number;
  window: OverviewWindow;
};

const VB_W = 800;
const VB_H = 160;
const TOP_PAD = 6;
const BOT_PAD = 4;

export function ThroughputChart({
  points,
  totalArticles,
  totalClientArticles,
  totalMisses,
  totalErrors,
  totalBytesServed,
  bucketSizeMs,
  window,
}: ThroughputChartProps) {
  const [hoverBucket, setHoverBucket] = useState<number | null>(null);
  const [keyboardBucket, setKeyboardBucket] = useState<number | null>(null);

  const bucketSeconds = Math.max(1, (bucketSizeMs || 60_000) / 1000);
  const hoverIdx = indexOfBucket(points, hoverBucket);
  const keyboardIdx = indexOfBucket(points, keyboardBucket);
  const cursorIdx = hoverIdx ?? keyboardIdx;

  useEffect(() => {
    setHoverBucket(null);
    setKeyboardBucket(null);
  }, [window]);

  const { clientArticlesPath, appArticlesPath, errorsPath, maxArticles, maxClientArticles, maxAppArticles, maxNetworkRate, xPercent, yPercent } =
    useMemo(() => {
      if (points.length === 0) {
        return {
          clientArticlesPath: "",
          appArticlesPath: "",
          errorsPath: "",
          maxArticles: 0,
          maxClientArticles: 0,
          maxAppArticles: 0,
          maxNetworkRate: 0,
          xPercent: (_: number) => 0,
          yPercent: (_: number) => 0,
        };
      }
      const peakClientArticles = Math.max(0, ...points.map(clientArticles));
      const peakAppArticles = Math.max(0, ...points.map(appArticles));
      const peakArticles = Math.max(peakClientArticles, peakAppArticles);
      const scaleMax = Math.max(1, peakArticles, ...points.map((p) => p.errors));
      const maxRate = Math.max(0, ...points.map((p) => (p.bytesFetched ?? 0) / bucketSeconds));
      const xStep = points.length > 1 ? VB_W / (points.length - 1) : 0;
      const innerH = VB_H - TOP_PAD - BOT_PAD;
      const y = (v: number) => VB_H - BOT_PAD - (v / scaleMax) * innerH;

      const xPct = (i: number) => (points.length > 1 ? (i / (points.length - 1)) * 100 : 50);
      const yPct = (v: number) =>
        100 - ((v / scaleMax) * (1 - (TOP_PAD + BOT_PAD) / VB_H) * 100 + (BOT_PAD / VB_H) * 100);

      return {
        clientArticlesPath: buildArticlesSeriesPath(points, clientArticles, xStep, y),
        appArticlesPath: buildArticlesSeriesPath(points, appArticles, xStep, y),
        errorsPath: buildSparseSeriesPath(points, (p) => p.errors, xStep, y),
        maxArticles: peakArticles,
        maxClientArticles: peakClientArticles,
        maxAppArticles: peakAppArticles,
        maxNetworkRate: maxRate,
        xPercent: xPct,
        yPercent: yPct,
      };
    }, [points, bucketSeconds]);

  const xTicks = useMemo(() => {
    if (points.length === 0) return [];
    const count = Math.min(5, points.length);
    if (count < 2) {
      const first = points[0];
      return first ? [{ idx: 0, label: formatBucketTime(first.bucket, window) }] : [];
    }
    return Array.from({ length: count }, (_, i) => {
      const idx = Math.round((points.length - 1) * (i / (count - 1)));
      const point = points[idx];
      return { idx, label: point ? formatBucketTime(point.bucket, window) : "" };
    });
  }, [points, window]);

  const onMove = useCallback(
    (clientX: number, target: HTMLElement) => {
      if (points.length === 0) return;
      const rect = target.getBoundingClientRect();
      const rel = (clientX - rect.left) / rect.width;
      const idx = Math.round(rel * (points.length - 1));
      const clamped = Math.max(0, Math.min(points.length - 1, idx));
      setHoverBucket(points[clamped]?.bucket ?? null);
    },
    [points],
  );

  const handleMouseMove = (e: React.MouseEvent<HTMLDivElement>) =>
    onMove(e.clientX, e.currentTarget);
  const handleMouseLeave = () => setHoverBucket(null);
  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (points.length === 0) return;
    const from = keyboardIdx ?? hoverIdx;
    let next: number | null;
    if (e.key === "ArrowRight") {
      e.preventDefault();
      next = Math.min(points.length - 1, (from ?? -1) + 1);
    } else if (e.key === "ArrowLeft") {
      e.preventDefault();
      next = Math.max(0, (from ?? points.length) - 1);
    } else if (e.key === "Home") {
      e.preventDefault();
      next = 0;
    } else if (e.key === "End") {
      e.preventDefault();
      next = points.length - 1;
    } else if (e.key === "Escape") {
      setHoverBucket(null);
      setKeyboardBucket(null);
      return;
    } else {
      return;
    }
    setHoverBucket(null);
    setKeyboardBucket(points[next]?.bucket ?? null);
  };
  const handleTouchMove = (e: React.TouchEvent<HTMLDivElement>) => {
    const t = e.touches[0];
    if (t) onMove(t.clientX, e.currentTarget);
  };
  const handleTouchStart = (e: React.TouchEvent<HTMLDivElement>) => {
    const t = e.touches[0];
    if (t) onMove(t.clientX, e.currentTarget);
  };

  const hasData = points.length > 0;
  const safeTotalClientArticles = Math.min(totalArticles, Math.max(0, totalClientArticles ?? 0));
  const totalAppArticles = totalArticles - safeTotalClientArticles;
  const bucketLabel =
    window === "1h" || window === "24h" ? "min" : window === "all" ? "day" : "hour";
  const hover = cursorIdx !== null ? (points[cursorIdx] ?? null) : null;
  const keyboardPoint = keyboardIdx !== null ? points[keyboardIdx] : undefined;
  const keyboardStatus = keyboardPoint
    ? describeThroughputBucket(keyboardPoint, window, bucketSeconds)
    : "";
  const hoverNetworkRate = hover ? (hover.bytesFetched ?? 0) / bucketSeconds : 0;
  const hoverClientArticles = hover ? clientArticles(hover) : 0;
  const hoverAppArticles = hover ? appArticles(hover) : 0;
  const tooltipPlacement =
    cursorIdx === null || points.length < 2
      ? "tooltip-top"
      : (() => {
          const rel = cursorIdx / (points.length - 1);
          if (rel < 0.2) return "tooltip-right";
          if (rel > 0.8) return "tooltip-left";
          return "tooltip-top";
        })();

  return (
    <section className="card w-full min-w-0 overflow-visible border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 overflow-visible p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 className="card-title text-base">Activity</h3>
            <p className="text-xs text-base-content/50">
              Article reads per {bucketLabel}, last {window}
            </p>
          </div>
          <div className="flex gap-[18px]">
            <Total label="Articles" value={formatNumber(totalArticles)} />
            <Total label="Misses" value={formatNumber(totalMisses)} />
            <Total
              label="Errors"
              value={formatNumber(totalErrors)}
              accent={totalErrors > 0 ? "danger" : undefined}
            />
            <Total label="Served" value={formatBytes(totalBytesServed)} />
          </div>
        </div>

        {hasData ? (
          <>
            <div className={styles.plot}>
              <div className="flex h-40 w-9 shrink-0 flex-col items-end justify-between text-[10px] text-base-content/50 tabular-nums select-none">
                <span>{formatNumber(maxArticles)}</span>
                <span>{formatNumber(Math.round(maxArticles / 2))}</span>
                <span>0</span>
              </div>
              <div
                className={styles.chartArea}
                tabIndex={0}
                role="img"
                aria-label={`${formatNumber(safeTotalClientArticles)} client reads, ${formatNumber(totalAppArticles)} app reads, ${formatNumber(totalArticles)} articles total, ${formatNumber(totalErrors)} errors, ${formatBytes(totalBytesServed)} served. Use arrow keys for bucket details.`}
                aria-describedby="overview-throughput-keyboard-status"
                onMouseMove={handleMouseMove}
                onMouseLeave={handleMouseLeave}
                onTouchStart={handleTouchStart}
                onTouchMove={handleTouchMove}
                onKeyDown={handleKeyDown}
              >
                <svg
                  viewBox={`0 0 ${VB_W} ${VB_H}`}
                  preserveAspectRatio="none"
                  className={styles.svg}
                >
                  {/* faint gridlines */}
                  <line
                    x1="0"
                    y1={(VB_H - BOT_PAD).toFixed(1)}
                    x2={VB_W}
                    y2={(VB_H - BOT_PAD).toFixed(1)}
                    className={styles.gridline}
                  />
                  <line
                    x1="0"
                    y1={(VB_H / 2).toFixed(1)}
                    x2={VB_W}
                    y2={(VB_H / 2).toFixed(1)}
                    className={styles.gridline}
                  />
                  <line
                    x1="0"
                    y1={TOP_PAD.toFixed(1)}
                    x2={VB_W}
                    y2={TOP_PAD.toFixed(1)}
                    className={styles.gridline}
                  />
                  {maxClientArticles > 0 && (
                    <path d={clientArticlesPath} className={styles.lineClient} data-series="client-articles" />
                  )}
                  {maxAppArticles > 0 && (
                    <path d={appArticlesPath} className={styles.lineApp} data-series="app-articles" />
                  )}
                  {totalErrors > 0 && errorsPath && (
                    <path d={errorsPath} className={styles.lineErrors} data-series="errors" />
                  )}
                </svg>

                {hover && cursorIdx !== null && (
                  <>
                    <div className={styles.crosshair} style={{ left: `${xPercent(cursorIdx)}%` }} />
                    <div
                      className={`tooltip tooltip-open ${tooltipPlacement} ${styles.hoverTooltip}`}
                      style={{
                        left: `${xPercent(cursorIdx)}%`,
                        top: `${yPercent(Math.max(hoverClientArticles, hoverAppArticles))}%`,
                      }}
                    >
                      <div className="tooltip-content">
                        <div className="space-y-0.5 text-left font-mono text-xs">
                          <div className="font-semibold">
                            {formatBucketTime(hover.bucket, window)}
                          </div>
                          <div>{formatNumber(hoverClientArticles)} client reads</div>
                          <div>{formatNumber(hoverAppArticles)} app reads</div>
                          <div>{formatNumber(hover.articles)} articles total</div>
                          {hoverNetworkRate > 0 && (
                            <div>{formatBytes(hoverNetworkRate)}/s downloaded</div>
                          )}
                          {(hover.misses ?? 0) > 0 && (
                            <div>{formatNumber(hover.misses)} misses</div>
                          )}
                          {hover.errors > 0 && (
                            <div className="text-error">{formatNumber(hover.errors)} errors</div>
                          )}
                          {hover.bytesServed > 0 && (
                            <div>{formatBytes(hover.bytesServed)} served</div>
                          )}
                        </div>
                      </div>
                      <span className={styles.hoverDotAnchor} />
                    </div>
                    {totalErrors > 0 && hover.errors > 0 && (
                      <div
                        className={`${styles.hoverDot} ${styles.hoverDotErr}`}
                        style={{
                          left: `${xPercent(cursorIdx)}%`,
                          top: `${yPercent(hover.errors)}%`,
                        }}
                      />
                    )}
                  </>
                )}
              </div>
            </div>
            <div
              id="overview-throughput-keyboard-status"
              className="sr-only"
              role="status"
              aria-live="polite"
              aria-atomic="true"
            >
              {keyboardStatus}
            </div>

            <div className="relative mt-1.5 ml-[46px] h-4 text-[10px] text-base-content/50 tabular-nums select-none">
              {xTicks.map((t) => (
                <span
                  key={t.idx}
                  className="absolute top-0 -translate-x-1/2 whitespace-nowrap"
                  style={{ left: `${xPercent(t.idx)}%` }}
                >
                  {t.label}
                </span>
              ))}
            </div>

            <div className="mt-2 flex flex-wrap items-center gap-3.5 text-[11px] text-base-content/50">
              <span className="inline-flex items-center gap-1.5">
                <span className="inline-block h-0.5 w-2.5 bg-success" />
                Client reads · {formatNumber(safeTotalClientArticles)}
              </span>
              <span className="inline-flex items-center gap-1.5">
                <span className="inline-block w-2.5 border-t-2 border-dashed border-info" />
                App reads · {formatNumber(totalAppArticles)}
                {maxNetworkRate > 0 && <> · peak {formatBytes(maxNetworkRate)}/s</>}
              </span>
              {totalErrors > 0 && (
                <span className="inline-flex items-center gap-1.5">
                  <span className="inline-block h-0.5 w-2.5 bg-error" />
                  Errors
                </span>
              )}
              <span className="ml-auto tabular-nums">
                Peak {formatNumber(maxArticles)} / {bucketLabel} · hover or use arrow keys
              </span>
            </div>
          </>
        ) : (
          <div className="py-12 text-center text-[13px] text-base-content/50">
            No activity in this window yet.
            <div className="mt-1.5 text-[11px] text-base-content/40">
              Articles you fetch will appear here.
            </div>
          </div>
        )}
      </div>
    </section>
  );
}

/**
 * Articles path: draw each positive run, including one leading and one trailing
 * zero when present, so segments rise from / fall to the baseline. Idle stretches
 * between runs stay undrawn (no continuous zero baseline).
 */
function buildArticlesSeriesPath(
  points: ThroughputPoint[],
  getValue: (p: ThroughputPoint) => number,
  xStep: number,
  y: (v: number) => number,
): string {
  const parts: string[] = [];
  let i = 0;
  while (i < points.length) {
    const current = points[i];
    if (!current || getValue(current) <= 0) {
      i++;
      continue;
    }
    const runStart = i;
    while (i < points.length) {
      const p = points[i];
      if (!p || getValue(p) <= 0) break;
      i++;
    }
    const runEnd = i - 1;
    const from = runStart > 0 ? runStart - 1 : runStart;
    const to = runEnd < points.length - 1 ? runEnd + 1 : runEnd;

    for (let j = from; j <= to; j++) {
      const p = points[j];
      if (!p) continue;
      const x = (j * xStep).toFixed(1);
      const yy = y(getValue(p)).toFixed(1);
      parts.push(`${j === from ? "M" : "L"}${x},${yy}`);
    }
    // Edge-of-window isolated spike with no adjacent zero needs a tiny stroke.
    if (from === to) {
      const p = points[from];
      if (p) {
        const x2 = (from * xStep + Math.max(xStep * 0.15, 1)).toFixed(1);
        const yy = y(getValue(p)).toFixed(1);
        parts.push(`L${x2},${yy}`);
      }
    }
  }
  return parts.join(" ");
}

/** Sparse errors path: skip y=0 so red does not cover the green baseline. */
function buildSparseSeriesPath(
  points: ThroughputPoint[],
  getValue: (p: ThroughputPoint) => number,
  xStep: number,
  y: (v: number) => number,
): string {
  const parts: string[] = [];
  let inSegment = false;
  for (let i = 0; i < points.length; i++) {
    const p = points[i];
    if (!p) continue;
    const value = getValue(p);
    const x = (i * xStep).toFixed(1);
    const yy = y(value).toFixed(1);
    if (value > 0) {
      if (!inSegment) {
        parts.push(`M${x},${yy}`);
        inSegment = true;
        // Isolated spikes need a tiny stroke segment to be visible.
        const next = points[i + 1];
        const nextZero = i === points.length - 1 || !next || getValue(next) === 0;
        if (nextZero) {
          const x2 = (i * xStep + Math.max(xStep * 0.15, 1)).toFixed(1);
          parts.push(`L${x2},${yy}`);
        }
      } else {
        parts.push(`L${x},${yy}`);
      }
    } else {
      inSegment = false;
    }
  }
  return parts.join(" ");
}

function Total({
  label,
  value,
  accent,
}: {
  label: string;
  value: string;
  accent?: "danger" | undefined;
}) {
  return (
    <div className="text-right">
      <div className="text-[10px] font-medium tracking-wide text-base-content/50 uppercase">
        {label}
      </div>
      <div
        className={`text-lg font-semibold tracking-tight tabular-nums ${accent === "danger" ? "text-error" : "text-base-content"}`}
      >
        {value}
      </div>
    </div>
  );
}

function indexOfBucket(points: ThroughputPoint[], bucket: number | null): number | null {
  if (bucket === null) return null;
  const idx = points.findIndex((p) => p.bucket === bucket);
  return idx >= 0 ? idx : null;
}

function describeThroughputBucket(
  point: ThroughputPoint,
  window: OverviewWindow,
  bucketSeconds: number,
): string {
  const parts = [
    formatBucketTime(point.bucket, window),
    `${formatNumber(clientArticles(point))} client reads`,
    `${formatNumber(appArticles(point))} app reads`,
    `${formatNumber(point.articles)} articles total`,
  ];
  const rate = (point.bytesFetched ?? 0) / bucketSeconds;
  if (rate > 0) parts.push(`${formatBytes(rate)}/s downloaded`);
  if ((point.misses ?? 0) > 0) parts.push(`${formatNumber(point.misses)} misses`);
  if (point.errors > 0) parts.push(`${formatNumber(point.errors)} errors`);
  if (point.bytesServed > 0) parts.push(`${formatBytes(point.bytesServed)} served`);
  return parts.join(", ");
}

function clientArticles(point: ThroughputPoint): number {
  return Math.min(point.articles, Math.max(0, point.clientArticles ?? 0));
}

function appArticles(point: ThroughputPoint): number {
  return Math.max(0, point.articles - clientArticles(point));
}

function formatBucketTime(ms: number, window: OverviewWindow): string {
  const d = new Date(ms);
  if (window === "1h" || window === "24h") {
    const hh = String(d.getHours()).padStart(2, "0");
    const mm = String(d.getMinutes()).padStart(2, "0");
    return `${hh}:${mm}`;
  }
  if (window === "7d") {
    const day = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][d.getDay()];
    const hh = String(d.getHours()).padStart(2, "0");
    return `${day} ${hh}:00`;
  }
  // 30d and all-time: show day-month so the x-axis spans many days clearly.
  const day = String(d.getDate()).padStart(2, "0");
  const mon = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"][
    d.getMonth()
  ];
  return `${day} ${mon}`;
}
