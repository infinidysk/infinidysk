import { useCallback, useEffect, useMemo, useState } from "react";
import styles from "./provider-speed-chart.module.css";
import type { OverviewWindow, ProviderSampledSpeedPoint } from "~/clients/backend-client.server";

export type ProviderSpeedChartProps = {
  providerLabel: string;
  points: ProviderSampledSpeedPoint[];
  bucketSizeMs: number;
  historyTruncated: boolean;
  window: OverviewWindow;
};

const VB_W = 800;
const VB_H = 160;
const TOP_PAD = 6;
const BOT_PAD = 4;

export function ProviderSpeedChart({
  providerLabel,
  points,
  bucketSizeMs: _bucketSizeMs,
  historyTruncated,
  window,
}: ProviderSpeedChartProps) {
  const [hoverBucket, setHoverBucket] = useState<number | null>(null);
  const [keyboardBucket, setKeyboardBucket] = useState<number | null>(null);

  const hoverIdx = indexOfBucket(points, hoverBucket);
  const keyboardIdx = indexOfBucket(points, keyboardBucket);
  const cursorIdx = hoverIdx ?? keyboardIdx;

  useEffect(() => {
    setHoverBucket(null);
    setKeyboardBucket(null);
  }, [window, providerLabel]);

  const { speedPath, averagePath, maxSpeed, xPercent, yPercent } = useMemo(() => {
    if (points.length === 0) {
      return {
        speedPath: "",
        averagePath: "",
        maxSpeed: 0,
        xPercent: (_: number) => 0,
        yPercent: (_: number) => 0,
      };
    }
    const peak = Math.max(0, ...points.map((point) => point.peakMbPerSec ?? 0));
    const scaleMax = Math.max(0.1, peak);
    const xStep = points.length > 1 ? VB_W / (points.length - 1) : 0;
    const innerH = VB_H - TOP_PAD - BOT_PAD;
    const y = (v: number) => VB_H - BOT_PAD - (v / scaleMax) * innerH;
    const xPct = (i: number) => (points.length > 1 ? (i / (points.length - 1)) * 100 : 50);
    const yPct = (v: number) =>
      100 - ((v / scaleMax) * (1 - (TOP_PAD + BOT_PAD) / VB_H) * 100 + (BOT_PAD / VB_H) * 100);

    return {
      speedPath: buildSpeedPath(points, xStep, y, "peakMbPerSec"),
      averagePath: buildSpeedPath(points, xStep, y, "activeAverageMbPerSec"),
      maxSpeed: peak,
      xPercent: xPct,
      yPercent: yPct,
    };
  }, [points]);

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

  const hasData = points.some((point) => point.peakMbPerSec !== null);
  const hover = cursorIdx !== null ? (points[cursorIdx] ?? null) : null;
  const keyboardPoint = keyboardIdx !== null ? points[keyboardIdx] : undefined;
  const keyboardStatus = keyboardPoint ? describeSpeedBucket(keyboardPoint, window) : "";
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
    <section className="w-full min-w-0 overflow-visible border-t border-base-content/10">
      <div className="flex flex-col gap-3 overflow-visible pt-4">
        <div>
          <h3 className="card-title text-base">{providerLabel}</h3>
          <p className="text-xs text-base-content/50">
            Sampled MB/s
            {historyTruncated
              ? " · retained provider history (last 365 days)"
              : window === "all"
                ? " · all time"
                : ` · last ${window}`}
            {maxSpeed > 0 ? ` · chart peak ${maxSpeed.toFixed(2)} MB/s` : ""}
          </p>
        </div>

        {hasData ? (
          <>
            <div className={styles.plot}>
              <div className="flex h-40 w-9 shrink-0 flex-col items-end justify-between text-[10px] text-base-content/50 tabular-nums select-none">
                <span>{maxSpeed.toFixed(2)}</span>
                <span>{(maxSpeed / 2).toFixed(2)}</span>
                <span>0</span>
              </div>
              <div
                className={styles.chartArea}
                tabIndex={0}
                role="img"
                aria-label={`Speed history for ${providerLabel}. Use arrow keys for bucket details.`}
                aria-describedby="provider-speed-keyboard-status"
                onMouseMove={(e) => onMove(e.clientX, e.currentTarget)}
                onMouseLeave={() => setHoverBucket(null)}
                onTouchStart={(e) => {
                  const t = e.touches[0];
                  if (t) onMove(t.clientX, e.currentTarget);
                }}
                onTouchMove={(e) => {
                  const t = e.touches[0];
                  if (t) onMove(t.clientX, e.currentTarget);
                }}
                onKeyDown={handleKeyDown}
              >
                <svg
                  viewBox={`0 0 ${VB_W} ${VB_H}`}
                  preserveAspectRatio="none"
                  className={styles.svg}
                >
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
                  {speedPath && (
                    <path d={speedPath} className={styles.lineSpeed} data-series="speed" />
                  )}
                  {averagePath && (
                    <path
                      d={averagePath}
                      className={styles.lineAverage}
                      data-series="active-average"
                    />
                  )}
                </svg>

                {hover && cursorIdx !== null && (
                  <>
                    <div className={styles.crosshair} style={{ left: `${xPercent(cursorIdx)}%` }} />
                    <div
                      className={`tooltip tooltip-open ${tooltipPlacement} ${styles.hoverTooltip}`}
                      style={{
                        left: `${xPercent(cursorIdx)}%`,
                        top: `${yPercent(hover.peakMbPerSec ?? 0)}%`,
                      }}
                    >
                      <div className="tooltip-content">
                        <div className="space-y-0.5 text-left font-mono text-xs">
                          <div className="font-semibold">{formatFullBucketTime(hover.bucket)}</div>
                          <div>Peak {formatRate(hover.peakMbPerSec)}</div>
                          <div>Active avg {formatRate(hover.activeAverageMbPerSec)}</div>
                        </div>
                      </div>
                      <span className={styles.hoverDotAnchor} />
                    </div>
                    {hover.peakMbPerSec !== null && (
                      <div
                        className={styles.hoverDot}
                        style={{
                          left: `${xPercent(cursorIdx)}%`,
                          top: `${yPercent(hover.peakMbPerSec)}%`,
                        }}
                      />
                    )}
                  </>
                )}
              </div>
            </div>
            <div
              id="provider-speed-keyboard-status"
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
            <div className="flex flex-wrap gap-4 text-xs">
              <span className="text-secondary">Peak</span>
              <span className="text-info">Active avg</span>
            </div>
          </>
        ) : (
          <div className="py-12 text-center text-[13px] text-base-content/50">
            No speed samples in this window yet.
          </div>
        )}
      </div>
    </section>
  );
}

function buildSpeedPath(
  points: ProviderSampledSpeedPoint[],
  xStep: number,
  y: (v: number) => number,
  field: "peakMbPerSec" | "activeAverageMbPerSec",
): string {
  return points
    .map((point, index) => {
      const value = point[field] ?? 0;
      const position = points.length === 1 ? VB_W / 2 : index * xStep;
      const height = y(value).toFixed(1);
      if (points.length === 1) {
        return `M${Math.max(0, position - 1).toFixed(1)},${height} L${Math.min(VB_W, position + 1).toFixed(1)},${height}`;
      }
      return `${index === 0 ? "M" : "L"}${position.toFixed(1)},${height}`;
    })
    .join(" ");
}

function indexOfBucket(points: ProviderSampledSpeedPoint[], bucket: number | null): number | null {
  if (bucket === null) return null;
  const idx = points.findIndex((p) => p.bucket === bucket);
  return idx >= 0 ? idx : null;
}

function describeSpeedBucket(point: ProviderSampledSpeedPoint, window: OverviewWindow): string {
  return [
    formatBucketTime(point.bucket, window),
    `Peak ${formatRate(point.peakMbPerSec)}`,
    `Active avg ${formatRate(point.activeAverageMbPerSec)}`,
  ].join(", ");
}

function formatRate(value: number | null): string {
  return value === null ? "unavailable" : `${value.toFixed(2)} MB/s`;
}

function formatFullBucketTime(ms: number): string {
  return new Date(ms).toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
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
  const day = String(d.getDate()).padStart(2, "0");
  const mon = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"][
    d.getMonth()
  ];
  return `${day} ${mon}`;
}
