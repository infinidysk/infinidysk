import { useMemo, useState } from "react";
import styles from "./latency-histogram.module.css";
import type { LatencyBucket } from "~/clients/backend-client.server";
import { formatNumber, formatPercent } from "../../utils/format";

export type LatencyHistogramProps = {
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
  samples: number;
  buckets: LatencyBucket[];
};

export function LatencyHistogram({ p50Ms, p95Ms, p99Ms, samples, buckets }: LatencyHistogramProps) {
  const [hover, setHover] = useState<LatencyBucket | null>(null);

  const maxCount = useMemo(() => buckets.reduce((m, b) => Math.max(m, b.count), 0), [buckets]);

  const empty = samples === 0;

  return (
    <section className="card w-full min-w-0 overflow-hidden border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h3 className="card-title text-base">Fetch time per article</h3>
            <p className="text-xs text-base-content/50">
              How long each successful article takes to retrieve from a Usenet provider
              {!empty && <> · {formatNumber(samples)} samples</>}
            </p>
          </div>
          <div className="flex gap-3">
            <Pctile label="p50" caption="median" ms={p50Ms} kind="ok" />
            <Pctile label="p95" caption="95th" ms={p95Ms} kind={p95Ms > 1500 ? "warn" : "ok"} />
            <Pctile
              label="p99"
              caption="99th"
              ms={p99Ms}
              kind={p99Ms > 5000 ? "danger" : p99Ms > 2000 ? "warn" : "ok"}
            />
          </div>
        </div>

        {empty ? (
          <div className="py-10 text-center text-xs text-base-content/50">
            No successful fetches in this window yet.
          </div>
        ) : (
          <>
            <div className={styles.bars}>
              {buckets.map((b, i) => {
                const h = maxCount > 0 ? (b.count / maxCount) * 100 : 0;
                const isHover = hover === b;
                return (
                  <button
                    type="button"
                    key={i}
                    className={`${styles.barCol} ${isHover ? styles.barHover : ""}`}
                    aria-label={`${fullBucketLabel(b)}: ${formatNumber(b.count)} ${b.count === 1 ? "fetch" : "fetches"}`}
                    onMouseEnter={() => setHover(b)}
                    onMouseLeave={() => setHover((h) => (h === b ? null : h))}
                    onFocus={() => setHover(b)}
                    onBlur={() => setHover((h) => (h === b ? null : h))}
                  >
                    <div className={styles.barWrap}>
                      <div className={styles.bar} style={{ height: `${h.toFixed(1)}%` }} />
                    </div>
                    <div className="max-w-full overflow-hidden text-center text-[10px] leading-none text-base-content/50 tabular-nums">
                      {bucketLabel(b)}
                    </div>
                  </button>
                );
              })}
            </div>
            <div className="mt-2.5 flex flex-wrap items-center justify-between gap-4">
              <div className="text-[10px] tracking-wide text-base-content/45 uppercase">
                ms ← faster &nbsp;·&nbsp; slower → seconds
              </div>
              <div className="min-h-3.5 text-[11px] text-base-content/50 tabular-nums">
                {hover ? (
                  <>
                    {fullBucketLabel(hover)} &mdash; {formatNumber(hover.count)}{" "}
                    {hover.count === 1 ? "fetch" : "fetches"}
                    {samples > 0 && <> · {formatPercent((hover.count / samples) * 100, 1)}</>}
                  </>
                ) : (
                  <>Select a bar for the exact count</>
                )}
              </div>
            </div>
          </>
        )}
      </div>
    </section>
  );
}

function Pctile({
  label,
  caption,
  ms,
  kind,
}: {
  label: string;
  caption: string;
  ms: number;
  kind: "ok" | "warn" | "danger";
}) {
  const valueClass = kind === "danger" ? "text-error" : kind === "warn" ? "text-warning" : "";
  return (
    <div
      className="min-w-[78px] rounded-box border border-base-content/10 bg-base-200 px-3 py-1.5 text-right"
      title={`${caption} — ${ms} ms`}
    >
      <div className="flex items-baseline justify-end gap-1.5">
        <span className="text-[10px] font-medium tracking-wide text-base-content/50 uppercase">
          {label}
        </span>
        <span className="text-[9px] tracking-wide text-base-content/50 lowercase">{caption}</span>
      </div>
      <div
        className={`mt-px text-[17px] leading-tight font-semibold tracking-tight tabular-nums ${valueClass}`}
      >
        {formatMs(ms)}
      </div>
    </div>
  );
}

function formatMs(ms: number): string {
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)} s`;
  return `${ms} ms`;
}

// Short axis-style label, just the upper bound of the bucket so 13 of them fit
// comfortably across narrow viewports. The full range lives in the tooltip.
function bucketLabel(b: LatencyBucket): string {
  if (b.hiMs >= 1_000_000_000) return "30s+";
  if (b.hiMs >= 1000) return `${b.hiMs / 1000}s`;
  return `${b.hiMs}`;
}

function fullBucketLabel(b: LatencyBucket): string {
  if (b.loMs === 0) return `Faster than ${formatBucketBound(b.hiMs)}`;
  if (b.hiMs >= 1_000_000_000) return `${formatBucketBound(b.loMs)} or slower`;
  return `${formatBucketBound(b.loMs)} – ${formatBucketBound(b.hiMs)}`;
}

function formatBucketBound(ms: number): string {
  if (ms >= 1000) return `${ms / 1000}s`;
  return `${ms}ms`;
}
