import { formatBytes } from "../../utils/format";

export type LiveTilesProps = {
  tiles: {
    activeReads: number;
    articlesPerMinute: number;
    errorsPerMinute: number;
    bytesServedPerMinute: number;
    inFlightArticleBytes?: number;
    inFlightArticleBudgetBytes?: number;
    inFlightArticleThrottleEvents?: number;
  };
};

export function LiveTiles({ tiles }: LiveTilesProps) {
  const bytesPerSec = tiles.bytesServedPerMinute / 60;
  const articlesPerSec = tiles.articlesPerMinute / 60;
  const leased = tiles.inFlightArticleBytes ?? 0;
  const cap = tiles.inFlightArticleBudgetBytes ?? 0;
  const throttles = tiles.inFlightArticleThrottleEvents ?? 0;
  const budgetPressure = cap > 0 && leased >= cap * 0.9;
  return (
    <div className="stats stats-vertical w-full border border-base-content/10 bg-base-200 shadow lg:stats-horizontal">
      <Tile
        label="Active reads"
        value={tiles.activeReads.toString()}
        accent={tiles.activeReads > 0 ? "live" : undefined}
      />
      <Tile
        label="Articles / s"
        value={articlesPerSec >= 10 ? articlesPerSec.toFixed(0) : articlesPerSec.toFixed(1)}
        sub={`${tiles.articlesPerMinute.toLocaleString()} / min`}
      />
      <Tile
        label="Read throughput"
        value={formatBytes(bytesPerSec) + "/s"}
        sub={`${formatBytes(tiles.bytesServedPerMinute)} / min`}
      />
      <Tile
        label="Article RAM"
        value={cap > 0 ? `${formatBytes(leased)}` : formatBytes(leased)}
        sub={
          cap > 0
            ? `${formatBytes(cap)} cap${throttles > 0 ? ` · ${throttles.toLocaleString()} waits` : ""}`
            : undefined
        }
        accent={budgetPressure ? "danger" : undefined}
      />
      <Tile
        label="Fetch errors"
        value={tiles.errorsPerMinute.toString()}
        sub="hard failures / min"
        accent={tiles.errorsPerMinute > 0 ? "danger" : undefined}
      />
    </div>
  );
}

function Tile({
  label,
  value,
  sub,
  accent,
}: {
  label: string;
  value: string;
  sub?: string | undefined;
  accent?: "live" | "danger" | undefined;
}) {
  const valueClass = accent === "live" ? "text-success" : accent === "danger" ? "text-error" : "";
  return (
    <div className="stat">
      <div className="stat-title">{label}</div>
      <div className={`stat-value font-mono text-2xl md:text-3xl ${valueClass}`}>{value}</div>
      {sub && <div className="stat-desc">{sub}</div>}
    </div>
  );
}
