import { useCallback, useEffect, useMemo, useState } from "react";
import styles from "./provider-speed-chart.module.css";
import type { OverviewWindow, ProviderSpeedPoint } from "~/clients/backend-client.server";
import { formatBytes } from "../../utils/format";

export type ProviderSpeedChartProps = {
  providerLabel: string;
  points: ProviderSpeedPoint[];
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

  const { speedPath, maxSpeed, xPercent, yPercent } = useMemo(() => {
    if (points.length === 0) {
      return {
        speedPath: "",
        maxSpeed: 0,
        xPercent: (_: number) => 0,
        yPercent: (_: number) => 0,
      };
    }
    const peak = Math.max(0, ...points.map((p) => p.speedMbPerSec));
    const scaleMax = Math.max(0.1, peak);
    const xStep = points.length > 1 ? VB_W / (points.length - 1) : 0;
    const innerH = VB_H - TOP_PAD - BOT_PAD;
    const y = (v: number) => VB_H - BOT_PAD - (v / scaleMax) * innerH;
    const xPct = (i: number) => (points.length > 1 ? (i / (points.length - 1)) * 100 : 50);
    const yPct = (v: number) =>
      100 - ((v / scaleMax) * (1 - (TOP_PAD + BOT_PAD) / VB_H) * 100 + (BOT_PAD / VB_H) * 100);

    return {
      speedPath: buildSpeedPath(points, xStep, y),
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

  const hasData = points.length > 0;
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
    <section className="card w-full min-w-0 overflow-visible border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 overflow-visible p-4">
        <div>
          <h3 className="card-title text-base">{providerLabel}</h3>
          <p className="text-xs text-base-content/50">
            Effective MB/s
            {historyTruncated
              ? " · retained provider history (last 365 days)"
              : window === "all"
                ? " · all time"
                : ` · last ${window}`}
            {maxSpeed > 0 ? ` · peak ${maxSpeed.toFixed(2)} MB/s` : ""}
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
                </svg>

                {hover && cursorIdx !== null && (
                  <>
                    <div className={styles.crosshair} style={{ left: `${xPercent(cursorIdx)}%` }} />
                    <div
                      className={`tooltip tooltip-open ${tooltipPlacement} ${styles.hoverTooltip}`}
                      style={{
                        left: `${xPercent(cursorIdx)}%`,
                        top: `${yPercent(hover.speedMbPerSec)}%`,
                      }}
                    >
                      <div className="tooltip-content">
                        <div className="space-y-0.5 text-left font-mono text-xs">
                          <div className="font-semibold">{formatFullBucketTime(hover.bucket)}</div>
                          <div>{hover.speedMbPerSec.toFixed(2)} MB/s</div>
                          <div>{formatBytes(hover.bytesFetched)} fetched</div>
                        </div>
                      </div>
                      <span className={styles.hoverDotAnchor} />
                    </div>
                    <div
                      className={styles.hoverDot}
                      style={{
                        left: `${xPercent(cursorIdx)}%`,
                        top: `${yPercent(hover.speedMbPerSec)}%`,
                      }}
                    />
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
  points: ProviderSpeedPoint[],
  xStep: number,
  y: (v: number) => number,
): string {
  if (points.length === 0) return "";
  if (points.length === 1) {
    const yy = y(points[0]?.speedMbPerSec ?? 0).toFixed(1);
    const mid = VB_W / 2;
    const extension = 1;
    const x1 = Math.max(0, mid - extension).toFixed(1);
    const x2 = Math.min(VB_W, mid + extension).toFixed(1);
    return `M${x1},${yy} L${x2},${yy}`;
  }
  return points
    .map((p, i) => {
      const x = (i * xStep).toFixed(1);
      const yy = y(p?.speedMbPerSec ?? 0).toFixed(1);
      return `${i === 0 ? "M" : "L"}${x},${yy}`;
    })
    .join(" ");
}

function indexOfBucket(points: ProviderSpeedPoint[], bucket: number | null): number | null {
  if (bucket === null) return null;
  const idx = points.findIndex((p) => p.bucket === bucket);
  return idx >= 0 ? idx : null;
}

function describeSpeedBucket(point: ProviderSpeedPoint, window: OverviewWindow): string {
  return [
    formatBucketTime(point.bucket, window),
    `${point.speedMbPerSec.toFixed(2)} MB/s`,
    `${formatBytes(point.bytesFetched)} fetched`,
  ].join(", ");
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
