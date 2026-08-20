import type { IndexerRow } from "~/clients/backend-client.server";
import { formatBytes, formatNumber, formatPercent } from "../../utils/format";

export type IndexerScoreboardProps = {
  indexers: IndexerRow[];
};

export function IndexerScoreboard({ indexers }: IndexerScoreboardProps) {
  return (
    <section className="card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4">
        <div>
          <h3 className="card-title text-base">Indexers</h3>
          <p className="text-xs text-base-content/50">
            Completed vs failed downloads, last 30 days
          </p>
        </div>

        {indexers.length === 0 ? (
          <p className="py-6 text-center text-xs text-base-content/50">No imports recorded yet.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="table table-sm min-w-[560px]">
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
                    <td className="max-w-[220px] font-medium">
                      <span
                        className="inline-block max-w-full truncate align-middle"
                        title={i.name}
                      >
                        {i.name}
                      </span>
                    </td>
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
    <div className="relative h-4 w-20 overflow-hidden rounded bg-base-200">
      <div
        className="absolute inset-0 bg-success opacity-[0.28]"
        style={{ width: `${(rate * 100).toFixed(1)}%` }}
      />
      <span className="relative block px-1.5 text-center text-[11px] leading-4 text-base-content tabular-nums">
        {formatPercent(rate * 100, 0)}
      </span>
    </div>
  );
}

function formatSeconds(s: number): string {
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${(s / 60).toFixed(1)}m`;
  return `${(s / 3600).toFixed(1)}h`;
}
