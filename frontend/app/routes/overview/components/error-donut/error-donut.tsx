import { useMemo, useState } from "react";
import styles from "./error-donut.module.css";
import type { ErrorSlice } from "~/clients/backend-client.server";
import { formatNumber, formatPercent } from "../../utils/format";

export type ErrorDonutProps = {
  errors: ErrorSlice[];
};

// Severity-aware palette mapped to daisyUI semantic tokens where possible.
const COLORS: Record<string, string> = {
  Missing: "color-mix(in srgb, var(--color-base-content) 45%, transparent)",
  Other: "color-mix(in srgb, var(--color-base-content) 35%, transparent)",
  Timeout: "var(--color-warning)",
  Corrupt: "var(--color-accent)",
  Network: "var(--color-error)",
  Auth: "var(--color-info)",
  Protocol: "var(--color-secondary)",
};
const DEFAULT_COLOR = "color-mix(in srgb, var(--color-base-content) 35%, transparent)";

// "Other" means the fetch failed for a reason we haven't classified yet — never
// imply it's a clean provider miss (that's "Missing").
const LABELS: Record<string, string> = {
  Other: "Other (unclassified)",
};

export function statusLabel(status: string): string {
  return LABELS[status] ?? status;
}

const HARD_FAILURE_STATUSES = new Set([
  "Timeout",
  "Corrupt",
  "Auth",
  "Network",
  "Protocol",
  "Other",
]);

export function ErrorBreakdown({ errors }: ErrorDonutProps) {
  const [hover, setHover] = useState<string | null>(null);

  const { hardFailures, missTotal, hardTotal } = useMemo(() => {
    const hardFailures = errors
      .filter((e) => e.status !== "Missing")
      .map((e) => ({
        ...e,
        color: COLORS[e.status] ?? DEFAULT_COLOR,
      }));
    const missTotal = errors.filter((e) => e.status === "Missing").reduce((s, e) => s + e.count, 0);
    const hardTotal = hardFailures.reduce((s, e) => s + e.count, 0);
    return { hardFailures, missTotal, hardTotal };
  }, [errors]);

  const hardSegments = useMemo(() => {
    return hardFailures.map((e) => ({
      ...e,
      fraction: hardTotal > 0 ? e.count / hardTotal : 0,
    }));
  }, [hardFailures, hardTotal]);

  const allClear = missTotal === 0 && hardTotal === 0;

  return (
    <section className="card w-full min-w-0 overflow-hidden border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4">
        <div>
          <h3 className="card-title text-base">Error breakdown</h3>
          <p className="text-xs text-base-content/50">Hard failures vs expected provider misses</p>
        </div>

        {allClear ? (
          <div className="flex items-center gap-3 px-3 pt-4 pb-2">
            <div className="h-3 w-3 shrink-0 rounded-full bg-success" />
            <div>
              <div className="text-sm font-semibold text-base-content">All clear</div>
              <div className="mt-0.5 text-xs text-base-content/50">
                No fetch errors in this window.
              </div>
            </div>
          </div>
        ) : (
          <>
            <div className="mb-3 flex flex-wrap items-baseline gap-x-6 gap-y-2">
              <div className="flex items-baseline gap-2">
                <span className="text-[28px] leading-none font-semibold tracking-tight text-base-content tabular-nums">
                  {formatNumber(hardTotal)}
                </span>
                <span className="text-[11px] font-medium tracking-wide text-base-content/50 uppercase">
                  {hardTotal === 1 ? "error" : "errors"}
                </span>
              </div>
              {missTotal > 0 && (
                <div className="flex items-baseline gap-2">
                  <span className="text-xl leading-none font-semibold tracking-tight text-base-content/50 tabular-nums">
                    {formatNumber(missTotal)}
                  </span>
                  <span className="text-[11px] font-medium tracking-wide text-base-content/50 uppercase">
                    provider misses
                  </span>
                </div>
              )}
            </div>

            {hardTotal > 0 ? (
              <>
                <div
                  className={`${styles.stack} mb-3.5`}
                  onMouseLeave={() => setHover(null)}
                  role="img"
                  aria-label={`${hardTotal} hard fetch errors broken down by type`}
                >
                  {hardSegments.map((s) => (
                    <div
                      key={s.status}
                      className={`${styles.stackSeg} ${hover && hover !== s.status ? styles.stackSegDim : ""}`}
                      style={{
                        flex: s.count,
                        background: s.color,
                      }}
                      onMouseEnter={() => setHover(s.status)}
                      title={`${statusLabel(s.status)}: ${formatNumber(s.count)} (${formatPercent(s.fraction * 100, 1)})`}
                    />
                  ))}
                </div>

                <ul className="m-0 grid list-none grid-cols-[repeat(auto-fill,minmax(160px,1fr))] gap-x-3.5 gap-y-1.5 p-0">
                  {hardSegments.map((s) => (
                    <li
                      key={s.status}
                      className={`grid cursor-default grid-cols-[10px_1fr_auto_auto] items-center gap-2.5 rounded-md px-1.5 py-1 text-xs transition-colors ${
                        hover === s.status ? "bg-base-content/[0.04]" : ""
                      }`}
                      onMouseEnter={() => setHover(s.status)}
                      onMouseLeave={() => setHover(null)}
                    >
                      <span className="h-2.5 w-2.5 rounded-sm" style={{ background: s.color }} />
                      <span className="text-base-content">{statusLabel(s.status)}</span>
                      <span className="font-medium text-base-content tabular-nums">
                        {formatNumber(s.count)}
                      </span>
                      <span className="text-[11px] text-base-content/50 tabular-nums">
                        {formatPercent(s.fraction * 100, 0)}
                      </span>
                    </li>
                  ))}
                </ul>
              </>
            ) : (
              <div className="mb-3 text-xs leading-snug text-base-content/50">
                No hard fetch failures — misses above are expected failover.
              </div>
            )}

            {missTotal > 0 && (
              <div className="mt-3.5 flex items-start gap-2.5 border-t border-base-content/10 pt-3 text-[11px] leading-snug text-base-content/50">
                <span
                  className="mt-0.5 h-2.5 w-2.5 shrink-0 rounded-sm"
                  style={{ background: COLORS["Missing"] }}
                />
                <span>
                  Provider misses are articles not found on the first provider tried; failover
                  usually recovers them.
                </span>
              </div>
            )}
          </>
        )}
      </div>
    </section>
  );
}

// Backwards-compatible export so the existing import name keeps working.
export { ErrorBreakdown as ErrorDonut };

// Exported for tests — hard failures exclude Missing.
export function isHardFailureStatus(status: string): boolean {
  return HARD_FAILURE_STATUSES.has(status);
}
