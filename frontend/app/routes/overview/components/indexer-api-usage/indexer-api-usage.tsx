import { useEffect, useState } from "react";
import type { IndexerApiUsageRow } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";
import { Tooltip } from "~/components/ui";

export type IndexerApiUsageProps = {
  rows: IndexerApiUsageRow[];
};

export function IndexerApiUsage({ rows }: IndexerApiUsageProps) {
  // Tick once a minute so the "resets in Xm" label stays roughly fresh without
  // forcing a backend roundtrip; the actual hit counts only refresh on the
  // overview's 30s poll, which is plenty for a daily/24h cap.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 60_000);
    return () => clearInterval(id);
  }, []);

  return (
    <section className="card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4">
        <div>
          <h3 className="card-title text-base">Indexer API usage</h3>
          <p className="text-xs text-base-content/50">
            Hits in the current reset window per indexer
          </p>
        </div>

        {rows.length === 0 ? (
          <p className="py-6 text-center text-xs text-base-content/50">
            No enabled indexers configured.
          </p>
        ) : (
          <div className="overflow-x-auto max-sm:overflow-visible">
            <table className="table table-sm w-full max-sm:table-fixed sm:min-w-[560px]">
              <thead>
                <tr>
                  <th className="max-sm:w-[30%]">Indexer</th>
                  <th className="min-w-[180px] max-sm:min-w-0">API hits</th>
                  <th className="min-w-[180px] max-sm:min-w-0">Downloads</th>
                  <th className="max-sm:hidden">Next reset</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.name}>
                    <td className="max-w-[220px] font-medium">
                      <Tooltip content={r.name}>
                        <span className="inline-block max-w-full truncate align-middle">
                          {r.name}
                        </span>
                      </Tooltip>
                    </td>
                    <td className="min-w-0">
                      <UsageBar
                        used={r.apiHits}
                        limit={r.apiHitLimit}
                        label={`${r.name} API hits`}
                      />
                    </td>
                    <td className="min-w-0">
                      <UsageBar
                        used={r.downloadHits}
                        limit={r.downloadHitLimit}
                        label={`${r.name} downloads`}
                      />
                    </td>
                    <td className="hidden whitespace-nowrap font-mono text-xs tabular-nums text-base-content/50 max-sm:hidden sm:table-cell">
                      {formatReset(r.resetAtMs, r.resetHourUtc, now)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}

function UsageBar({
  used,
  limit,
  label,
}: {
  used: number;
  limit: number | null | undefined;
  label: string;
}) {
  if (!limit || limit <= 0) {
    return (
      <div className="flex min-w-0 items-center gap-2.5">
        <Tooltip className="min-w-0 flex-1" content="No limit configured">
          <progress
            aria-label={`${label}: ${formatNumber(used)}, unlimited`}
            className="progress progress-success h-2 w-full min-w-0"
            value={0}
            max={100}
          />
        </Tooltip>
        <span className="min-w-0 shrink-0 text-xs text-base-content tabular-nums whitespace-nowrap max-sm:shrink max-sm:whitespace-normal">
          {formatNumber(used)}
          <span className="text-base-content/50"> · unlimited</span>
        </span>
      </div>
    );
  }
  const pct = Math.min(100, (used / limit) * 100);
  const near = pct >= 80 && pct < 100;
  const over = pct >= 100;
  const tone = over ? "progress-error" : near ? "progress-warning" : "progress-success";
  return (
    <div className="flex min-w-0 items-center gap-2.5 overflow-hidden">
      <progress
        aria-label={`${label}: ${formatNumber(used)} of ${formatNumber(limit)}`}
        className={`progress h-2 min-w-0 flex-1 ${tone}`}
        value={pct}
        max={100}
      />
      <span className="min-w-0 shrink-0 text-xs text-base-content tabular-nums whitespace-nowrap max-sm:shrink max-sm:whitespace-normal">
        {formatNumber(used)}
        <span className="text-base-content/50"> / {formatNumber(limit)}</span>
      </span>
    </div>
  );
}

function formatReset(
  resetAtMs: number,
  resetHourUtc: number | null | undefined,
  nowMs: number,
): string {
  const remaining = resetAtMs - nowMs;
  if (remaining <= 0) return "now";
  const totalMinutes = Math.floor(remaining / 60_000);
  const days = Math.floor(totalMinutes / (24 * 60));
  const hours = Math.floor((totalMinutes % (24 * 60)) / 60);
  const mins = totalMinutes % 60;

  let countdown: string;
  if (days > 0) countdown = `${days}d ${hours}h`;
  else if (hours > 0) countdown = `${hours}h ${mins}m`;
  else countdown = `${Math.max(1, mins)}m`;

  const suffix = typeof resetHourUtc === "number" ? ` (${pad2(resetHourUtc)}:00 UTC)` : "";
  return `in ${countdown}${suffix}`;
}

function pad2(n: number): string {
  return n < 10 ? `0${n}` : `${n}`;
}
