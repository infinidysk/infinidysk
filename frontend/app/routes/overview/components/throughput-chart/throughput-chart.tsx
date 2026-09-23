import { useCallback, useEffect, useMemo, useState, type CSSProperties } from "react";
import styles from "./throughput-chart.module.css";
import type { OverviewWindow, ThroughputPoint } from "~/clients/backend-client.server";
import { formatBytes, formatNumber } from "../../utils/format";
import { Tooltip } from "~/components/ui";

export type ThroughputChartProps = {
  points: ThroughputPoint[];
  totalArticles: number;
  totalClientArticles: number;
  totalQueueArticles: number;
  totalMisses: number;
  totalErrors: number;
  totalBytesServed: number;
  totalBytesFetched: number;
  bucketSizeMs: number;
  peakFetchBytesPerSec?: number;
  window: OverviewWindow;
};

const VB_W = 800;
const VB_H = 160;
const TOP_PAD = 6;
const BOT_PAD = 4;

const seriesValues = {
  "client-articles": clientArticles,
  "queue-articles": queueArticles,
  "app-articles": appArticles,
  errors: (point: ThroughputPoint) => point.errors,
};
type SeriesId = keyof typeof seriesValues;

export function ThroughputChart({
  points,
  totalArticles,
  totalClientArticles,
  totalQueueArticles,
  totalMisses,
  totalErrors,
  totalBytesServed,
  totalBytesFetched,
  bucketSizeMs,
  peakFetchBytesPerSec = 0,
  window,
}: ThroughputChartProps) {
  const [hoverBucket, setHoverBucket] = useState<number | null>(null);
  const [keyboardBucket, setKeyboardBucket] = useState<number | null>(null);
  const [isolatedSeries, setIsolatedSeries] = useState<SeriesId | null>(null);
  const [previewSeries, setPreviewSeries] = useState<SeriesId | null>(null);
  const emphasizedSeries = isolatedSeries ?? previewSeries;

  const bucketSeconds = Math.max(1, (bucketSizeMs || 60_000) / 1000);
  const hoverIdx = indexOfBucket(points, hoverBucket);
  const keyboardIdx = indexOfBucket(points, keyboardBucket);
  const cursorIdx = hoverIdx ?? keyboardIdx;

  useEffect(() => {
    setHoverBucket(null);
    setKeyboardBucket(null);
    setIsolatedSeries(null);
    setPreviewSeries(null);
  }, [window]);

  const {
    clientArticlesPath,
    queueArticlesPath,
    appArticlesPath,
    errorsPath,
    areaPath,
    maxArticles,
    scaleMax,
    maxClientArticles,
    maxQueueArticles,
    maxAppArticles,
    maxNetworkRate,
    xPercent,
    yPercent,
  } = useMemo(() => {
    if (points.length === 0) {
      return {
        clientArticlesPath: "",
        queueArticlesPath: "",
        appArticlesPath: "",
        errorsPath: "",
        areaPath: "",
        maxArticles: 0,
        scaleMax: 0,
        maxClientArticles: 0,
        maxQueueArticles: 0,
        maxAppArticles: 0,
        maxNetworkRate: 0,
        xPercent: (_: number) => 0,
        yPercent: (_: number) => 0,
      };
    }
    const peakClientArticles = Math.max(0, ...points.map(clientArticles));
    const peakQueueArticles = Math.max(0, ...points.map(queueArticles));
    const peakAppArticles = Math.max(0, ...points.map(appArticles));
    const peakArticles = Math.max(peakClientArticles, peakQueueArticles, peakAppArticles);
    const scaleMax = isolatedSeries
      ? Math.max(1, ...points.map(seriesValues[isolatedSeries]))
      : Math.max(1, peakArticles, ...points.map((p) => p.errors));
    // Bucket averages only floor the peak for history recorded before 1-second sampling existed.
    const maxRate = Math.max(
      peakFetchBytesPerSec,
      ...points.map((p) => (p.bytesFetched ?? 0) / bucketSeconds),
    );
    const xStep = points.length > 1 ? VB_W / (points.length - 1) : 0;
    const innerH = VB_H - TOP_PAD - BOT_PAD;
    const y = (v: number) => VB_H - BOT_PAD - (v / scaleMax) * innerH;

    const xPct = (i: number) => (points.length > 1 ? (i / (points.length - 1)) * 100 : 50);
    const yPct = (v: number) =>
      100 - ((v / scaleMax) * (1 - (TOP_PAD + BOT_PAD) / VB_H) * 100 + (BOT_PAD / VB_H) * 100);

    return {
      clientArticlesPath: buildArticlesSeriesPath(points, clientArticles, xStep, y),
      queueArticlesPath: buildArticlesSeriesPath(points, queueArticles, xStep, y),
      appArticlesPath: buildArticlesSeriesPath(points, appArticles, xStep, y),
      errorsPath: buildSparseSeriesPath(points, (p) => p.errors, xStep, y),
      areaPath: emphasizedSeries
        ? buildArticlesSeriesPath(points, seriesValues[emphasizedSeries], xStep, y, true)
        : "",
      maxArticles: peakArticles,
      scaleMax,
      maxClientArticles: peakClientArticles,
      maxQueueArticles: peakQueueArticles,
      maxAppArticles: peakAppArticles,
      maxNetworkRate: maxRate,
      xPercent: xPct,
      yPercent: yPct,
    };
  }, [points, bucketSeconds, peakFetchBytesPerSec, isolatedSeries, emphasizedSeries]);

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

  const bucketAt = useCallback(
    (clientX: number, target: HTMLElement) => {
      if (points.length === 0) return null;
      const rect = target.getBoundingClientRect();
      if (rect.width <= 0) return points.at(-1)?.bucket ?? null;
      const rel = (clientX - rect.left) / rect.width;
      const idx = Math.round(rel * (points.length - 1));
      const clamped = Math.max(0, Math.min(points.length - 1, idx));
      return points[clamped]?.bucket ?? null;
    },
    [points],
  );

  const handleMouseMove = (e: React.MouseEvent<HTMLDivElement>) =>
    setHoverBucket(bucketAt(e.clientX, e.currentTarget));
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
    if (t) setHoverBucket(bucketAt(t.clientX, e.currentTarget));
  };
  const handleTouchStart = (e: React.TouchEvent<HTMLDivElement>) => {
    const t = e.touches[0];
    if (t) setHoverBucket(bucketAt(t.clientX, e.currentTarget));
  };

  const hasData = points.length > 0;
  const safeTotalClientArticles = Math.min(totalArticles, Math.max(0, totalClientArticles ?? 0));
  const safeTotalQueueArticles = Math.min(
    totalArticles - safeTotalClientArticles,
    Math.max(0, totalQueueArticles ?? 0),
  );
  const totalAppArticles = totalArticles - safeTotalClientArticles - safeTotalQueueArticles;
  const series = [
    {
      id: "client-articles",
      label: "Client attempts",
      total: safeTotalClientArticles,
      peak: maxClientArticles,
      path: clientArticlesPath,
      className: styles.lineClient,
      color: "var(--color-success)",
      swatch: "bg-success",
    },
    {
      id: "queue-articles",
      label: "Import attempts",
      total: safeTotalQueueArticles,
      peak: maxQueueArticles,
      path: queueArticlesPath,
      className: styles.lineQueue,
      color: "var(--color-secondary)",
      swatch: "bg-secondary",
    },
    {
      id: "app-articles",
      label: "Maintenance attempts",
      total: totalAppArticles,
      peak: maxAppArticles,
      path: appArticlesPath,
      className: styles.lineApp,
      color: "var(--color-info)",
      swatch: "border-t-2 border-info",
    },
    {
      id: "errors",
      label: "Errors",
      total: totalErrors,
      peak: totalErrors,
      path: errorsPath,
      className: styles.lineErrors,
      color: "var(--color-error)",
      swatch: "bg-error",
    },
  ] satisfies {
    id: SeriesId;
    label: string;
    total: number;
    peak: number;
    path: string;
    className: string;
    color: string;
    swatch: string;
  }[];
  const visibleSeries = series
    .filter((item) => !isolatedSeries || item.id === isolatedSeries)
    .sort(
      (left, right) => Number(left.id === emphasizedSeries) - Number(right.id === emphasizedSeries),
    );
  const selectedSeries = series.find((item) => item.id === isolatedSeries);
  const emphasizedItem = series.find((item) => item.id === emphasizedSeries);
  const successfulReads = Math.max(0, totalArticles - totalMisses - totalErrors);
  const bucketLabel =
    window === "1h" || window === "24h" ? "min" : window === "all" ? "day" : "hour";
  const hover = cursorIdx !== null ? (points[cursorIdx] ?? null) : null;
  const keyboardPoint = keyboardIdx !== null ? points[keyboardIdx] : undefined;
  const keyboardStatus = keyboardPoint
    ? describeThroughputBucket(keyboardPoint, window, bucketSeconds)
    : "";
  const hoverNetworkRate = hover ? (hover.bytesFetched ?? 0) / bucketSeconds : 0;
  const cursorValue = hover
    ? Math.max(0, ...visibleSeries.map((item) => seriesValues[item.id](hover)))
    : 0;

  return (
    <section className="card w-full min-w-0 overflow-visible border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 overflow-visible p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 className="card-title text-base">Activity</h3>
            <p className="text-xs text-base-content/70">
              Article attempts per {bucketLabel}, {window === "all" ? "all time" : `last ${window}`}
            </p>
          </div>
          <div className="grid w-full grid-cols-2 gap-x-[18px] gap-y-3 sm:flex sm:w-auto sm:flex-wrap">
            <Total
              label="Successful reads"
              alignStart
              value={formatNumber(successfulReads)}
              description="Article retrievals reported successful in this window, including cache hits. Excludes recorded misses and errors; not unique articles or completed playback."
            />
            <Total
              label="Peak download"
              value={hasData ? `${formatBytes(maxNetworkRate)}/s` : "N/A"}
              description="Highest 1-second Usenet download rate sampled in this window. It does not shrink when you widen the time range. History recorded before peak sampling falls back to the highest bucket average."
            />
            <Total
              label="Errors"
              alignStart
              value={formatNumber(totalErrors)}
              description="Recorded attempt errors in this window, excluding provider misses. Retries may recover them; this is not a count of failed client reads."
              accent={totalErrors > 0 ? "danger" : undefined}
            />
            <Total
              label="Served"
              value={formatBytes(totalBytesServed)}
              description="Bytes served by client read sessions ending in this window."
            />
            <Total
              label="Fetched"
              value={formatBytes(totalBytesFetched)}
              description="Bytes downloaded from Usenet providers in this window, including streaming, health checks, and queue processing. Counted when fetched, so it will not reconcile exactly with Served."
            />
          </div>
        </div>

        {hasData ? (
          <>
            <div className={styles.plot}>
              <div className="flex h-40 w-9 shrink-0 flex-col items-end justify-between text-[10px] text-base-content/70 tabular-nums select-none">
                <span>{formatNumber(scaleMax)}</span>
                {scaleMax > 1 && <span>{formatNumber(Math.round(scaleMax / 2))}</span>}
                <span>0</span>
              </div>
              <div
                className={styles.chartArea}
                tabIndex={0}
                role="img"
                aria-label={`${formatNumber(safeTotalClientArticles)} client attempts, ${formatNumber(safeTotalQueueArticles)} import attempts, ${formatNumber(totalAppArticles)} maintenance attempts, ${formatNumber(totalArticles)} attempts total, ${formatNumber(successfulReads)} successful reads, ${formatNumber(totalErrors)} errors, ${formatBytes(totalBytesServed)} served, ${formatBytes(totalBytesFetched)} fetched. ${selectedSeries ? `Showing only ${selectedSeries.label.toLowerCase()}. ` : ""}Use Left and Right arrow keys for bucket details, Home and End for the first and last bucket, and Escape to dismiss.`}
                aria-describedby="overview-throughput-keyboard-status"
                onMouseMove={handleMouseMove}
                onMouseLeave={handleMouseLeave}
                onTouchStart={handleTouchStart}
                onTouchMove={handleTouchMove}
                onKeyDown={handleKeyDown}
                onFocus={() => {
                  setKeyboardBucket((current) =>
                    indexOfBucket(points, current) !== null
                      ? current
                      : (hoverBucket ?? points.at(-1)?.bucket ?? null),
                  );
                }}
                onBlur={() => {
                  setKeyboardBucket(null);
                  setHoverBucket(null);
                }}
                onClick={(event) => {
                  event.currentTarget.focus();
                  setKeyboardBucket(bucketAt(event.clientX, event.currentTarget));
                  setHoverBucket(null);
                }}
              >
                <svg
                  aria-hidden="true"
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
                  {emphasizedItem && areaPath && (
                    <path
                      d={areaPath}
                      fill={emphasizedItem.color}
                      fillOpacity={0.12}
                      stroke="none"
                      data-area-series={emphasizedItem.id}
                    />
                  )}
                  {visibleSeries
                    .filter((item) => item.peak > 0 && item.path)
                    .map((item) => (
                      <path
                        key={item.id}
                        d={item.path}
                        className={item.className}
                        data-series={item.id}
                        style={{
                          opacity: emphasizedSeries && item.id !== emphasizedSeries ? 0.3 : 1,
                          strokeWidth: item.id === emphasizedSeries ? 2.5 : 1.5,
                        }}
                      />
                    ))}
                </svg>

                {hover && cursorIdx !== null && (
                  <>
                    <div className={styles.crosshair} style={{ left: `${xPercent(cursorIdx)}%` }} />
                    <div
                      className={`tooltip tooltip-open tooltip-top ${styles.hoverTooltip}`}
                      style={
                        {
                          "--cursor-x": `${xPercent(cursorIdx)}%`,
                          "--cursor-y": `${yPercent(cursorValue)}%`,
                        } as CSSProperties
                      }
                    >
                      <div className="tooltip-content">
                        <div className="space-y-0.5 text-left font-mono text-xs">
                          <div className="font-semibold">
                            {formatBucketTime(hover.bucket, window)}
                          </div>
                          {series
                            .filter(
                              (item) =>
                                item.id !== "errors" ||
                                hover.errors > 0 ||
                                isolatedSeries === "errors",
                            )
                            .map((item) => (
                              <div
                                key={item.id}
                                className={`flex items-center gap-1.5 ${item.id === emphasizedSeries ? "font-semibold" : ""}`}
                              >
                                <span
                                  aria-hidden="true"
                                  className="inline-block h-2 w-2 shrink-0 rounded-full"
                                  style={{ background: item.color }}
                                />
                                {formatNumber(seriesValues[item.id](hover))}{" "}
                                {item.label.toLowerCase()}
                              </div>
                            ))}
                          <div>{formatNumber(hover.articles)} attempts total</div>
                          {hoverNetworkRate > 0 && (
                            <div>{formatBytes(hoverNetworkRate)}/s downloaded</div>
                          )}
                          {(hover.misses ?? 0) > 0 && (
                            <div>{formatNumber(hover.misses)} provider miss attempts</div>
                          )}
                          {hover.bytesServed > 0 && (
                            <div>{formatBytes(hover.bytesServed)} served</div>
                          )}
                        </div>
                      </div>
                      <span />
                    </div>
                    {visibleSeries
                      .filter((item) => seriesValues[item.id](hover) > 0)
                      .map((item) => (
                        <div
                          key={item.id}
                          className={styles.hoverDot}
                          data-marker-series={item.id}
                          style={{
                            left: `${xPercent(cursorIdx)}%`,
                            top: `${yPercent(seriesValues[item.id](hover))}%`,
                            background: item.color,
                          }}
                        />
                      ))}
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

            <div className="relative mt-1.5 ml-[46px] h-4 text-[10px] text-base-content/70 tabular-nums select-none">
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

            <div className="mt-2 flex flex-wrap items-center gap-3.5 text-[11px] text-base-content/70">
              {series
                .filter((item) => item.id !== "errors" || totalErrors > 0)
                .map((item) => (
                  <button
                    key={item.id}
                    type="button"
                    aria-pressed={isolatedSeries === item.id}
                    title={
                      isolatedSeries === item.id
                        ? "Show all series"
                        : `Isolate ${item.label.toLowerCase()}`
                    }
                    className={`inline-flex min-h-9 cursor-pointer items-center gap-1.5 rounded-sm px-1 text-left focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary ${isolatedSeries === item.id ? "text-base-content underline underline-offset-4" : "hover:text-base-content"}`}
                    onMouseEnter={() => setPreviewSeries(item.id)}
                    onMouseLeave={() => setPreviewSeries(null)}
                    onFocus={() => setPreviewSeries(item.id)}
                    onBlur={() => setPreviewSeries(null)}
                    onClick={() => {
                      setIsolatedSeries((current) => (current === item.id ? null : item.id));
                      setPreviewSeries(null);
                    }}
                  >
                    <span
                      aria-hidden="true"
                      className={`inline-block h-0.5 w-2.5 shrink-0 ${item.swatch}`}
                    />
                    {item.label}
                    {item.id !== "errors" ? ` · ${formatNumber(item.total)}` : ""}
                  </button>
                ))}
              <span className="ml-auto tabular-nums">
                Peak {selectedSeries ? selectedSeries.label.toLowerCase() : "attempts"}{" "}
                {formatNumber(
                  selectedSeries
                    ? Math.max(0, ...points.map(seriesValues[selectedSeries.id]))
                    : maxArticles,
                )}{" "}
                / {bucketLabel}
              </span>
            </div>
          </>
        ) : (
          <div className="py-12 text-center text-[13px] text-base-content/70">
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
  filled = false,
): string {
  const parts: string[] = [];
  const xOffset = points.length === 1 ? VB_W / 2 : 0;
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

    if (filled) parts.push(`M${(xOffset + from * xStep).toFixed(1)},${y(0).toFixed(1)}`);

    for (let j = from; j <= to; j++) {
      const p = points[j];
      if (!p) continue;
      const x = (xOffset + j * xStep).toFixed(1);
      const yy = y(getValue(p)).toFixed(1);
      parts.push(`${j === from && !filled ? "M" : "L"}${x},${yy}`);
    }
    // Edge-of-window isolated spike with no adjacent zero needs a tiny stroke.
    if (from === to) {
      const p = points[from];
      if (p) {
        const x2 = (xOffset + from * xStep + Math.max(xStep * 0.15, 1)).toFixed(1);
        const yy = y(getValue(p)).toFixed(1);
        parts.push(`L${x2},${yy}`);
      }
    }
    if (filled) {
      const endX = xOffset + to * xStep + (from === to ? Math.max(xStep * 0.15, 1) : 0);
      parts.push(`L${endX.toFixed(1)},${y(0).toFixed(1)} Z`);
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
  const xOffset = points.length === 1 ? VB_W / 2 : 0;
  for (let i = 0; i < points.length; i++) {
    const p = points[i];
    if (!p) continue;
    const value = getValue(p);
    const x = (xOffset + i * xStep).toFixed(1);
    const yy = y(value).toFixed(1);
    if (value > 0) {
      if (!inSegment) {
        parts.push(`M${x},${yy}`);
        inSegment = true;
        // Isolated spikes need a tiny stroke segment to be visible.
        const next = points[i + 1];
        const nextZero = i === points.length - 1 || !next || getValue(next) === 0;
        if (nextZero) {
          const x2 = (xOffset + i * xStep + Math.max(xStep * 0.15, 1)).toFixed(1);
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
  description,
  alignStart = false,
}: {
  label: string;
  value: string;
  accent?: "danger" | undefined;
  description: string;
  alignStart?: boolean;
}) {
  return (
    <Tooltip
      content={description}
      placement="bottom"
      className={`min-w-0 ${alignStart ? "tooltip-start sm:tooltip-end" : "tooltip-end"}`}
      contentClassName="!max-w-[min(16rem,80vw)] whitespace-normal text-left"
    >
      <div
        tabIndex={0}
        className="rounded-sm text-right focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
      >
        <div className="text-[10px] font-medium text-base-content/70 uppercase">{label}</div>
        <div
          className={`break-words text-lg font-semibold tabular-nums ${accent === "danger" ? "text-error" : "text-base-content"}`}
        >
          {value}
        </div>
      </div>
    </Tooltip>
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
    `${formatNumber(clientArticles(point))} client attempts`,
    `${formatNumber(queueArticles(point))} import attempts`,
    `${formatNumber(appArticles(point))} maintenance attempts`,
    `${formatNumber(point.articles)} attempts total`,
  ];
  const rate = (point.bytesFetched ?? 0) / bucketSeconds;
  if (rate > 0) parts.push(`${formatBytes(rate)}/s downloaded`);
  if ((point.misses ?? 0) > 0) parts.push(`${formatNumber(point.misses)} provider miss attempts`);
  if (point.errors > 0) parts.push(`${formatNumber(point.errors)} errors`);
  if (point.bytesServed > 0) parts.push(`${formatBytes(point.bytesServed)} served`);
  return parts.join(", ");
}

function clientArticles(point: ThroughputPoint): number {
  return Math.min(point.articles, Math.max(0, point.clientArticles ?? 0));
}

/** Queue-import attempts, clamped so client + import never exceed the bucket total. */
function queueArticles(point: ThroughputPoint): number {
  return Math.min(point.articles - clientArticles(point), Math.max(0, point.queueArticles ?? 0));
}

/** Everything that is neither client nor import: health checks, repairs, other background work. */
function appArticles(point: ThroughputPoint): number {
  return Math.max(0, point.articles - clientArticles(point) - queueArticles(point));
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
