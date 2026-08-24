import type { IndexerRow } from "~/clients/backend-client.server";
import { formatBytes, formatNumber, formatPercent } from "../../utils/format";
import { settingsPath } from "~/navigation/settings-tabs";
import { Tooltip } from "~/components/ui";
import { WidgetLink } from "../widget-link/widget-link";

export type IndexerScoreboardProps = {
  indexers: IndexerRow[];
};

export function IndexerScoreboard({ indexers }: IndexerScoreboardProps) {
  const failedTotal = indexers.reduce((s, i) => s + i.failed, 0);
  return (
    <section className="card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 className="card-title text-base">Indexers</h3>
            <p className="text-xs text-base-content/50">
              Completed vs failed downloads, last 30 days
            </p>
          </div>
          <div className="card-actions m-0">
            <WidgetLink to={settingsPath("indexers")}>Indexer settings</WidgetLink>
          </div>
        </div>
        {failedTotal > 0 && (
          <p className="text-xs text-error">
            {failedTotal} failed download{failedTotal === 1 ? "" : "s"}.{" "}
            <WidgetLink to={settingsPath("indexers")}>Review indexer settings</WidgetLink>
          </p>
        )}

        {indexers.length === 0 ? (
          <p className="py-6 text-center text-xs text-base-content/50">
            No downloads recorded yet.
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="table table-pin-cols table-sm min-w-[560px]">
              <thead>
                <tr>
                  <th>Indexer</th>
                  <th>Completed</th>
                  <th>Failed</th>
                  <th>Success</th>
                  <th>Bytes</th>
                  <th>Avg time</th>
                </tr>
              </thead>
              <tbody>
                {indexers.map((i) => (
                  <tr key={i.name}>
                    <th scope="row" className="bg-base-100 max-w-[220px] font-medium">
                      <Tooltip content={i.name}>
                        <span className="inline-block max-w-full truncate align-middle">
                          {i.name}
                        </span>
                      </Tooltip>
                    </th>
                    <td className="font-mono tabular-nums">{formatNumber(i.completed)}</td>
                    <td className={`font-mono tabular-nums ${i.failed > 0 ? "text-error" : ""}`}>
                      {formatNumber(i.failed)}
                    </td>
                    <td>
                      <SuccessBar rate={i.successRate} />
                    </td>
                    <td className="font-mono tabular-nums">{formatBytes(i.bytesCompleted)}</td>
                    <td className="font-mono tabular-nums">{formatSeconds(i.avgSeconds)}</td>
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

function SuccessBar({ rate }: { rate: number }) {
  return (
    <div className="flex items-center gap-2">
      <progress className="progress progress-success w-20" value={rate * 100} max={100} />
      <span className="font-mono text-[11px] tabular-nums">{formatPercent(rate * 100, 0)}</span>
    </div>
  );
}

function formatSeconds(s: number): string {
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${(s / 60).toFixed(1)}m`;
  return `${(s / 3600).toFixed(1)}h`;
}
