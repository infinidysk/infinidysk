import { useEffect, useState } from "react";
import { formatBytes } from "../../utils/format";
import { mockReadsRequested } from "../live-reads-panel/live-reads-panel.mock";

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
  const [mockReads, setMockReads] = useState<number | null>(null);
  useEffect(() => {
    setMockReads(mockReadsRequested());
  }, []);
  const activeReads = mockReads ?? tiles.activeReads;
  const bytesPerSec = tiles.bytesServedPerMinute / 60;
  const articlesPerSec = tiles.articlesPerMinute / 60;
  const leased = tiles.inFlightArticleBytes ?? 0;
  const cap = tiles.inFlightArticleBudgetBytes ?? 0;
  const throttles = tiles.inFlightArticleThrottleEvents ?? 0;
  const budgetPressure = cap > 0 && leased >= cap * 0.9;
  return (
    <div
      role="region"
      aria-label="Live status"
      className="grid min-w-0 grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-3 lg:grid-cols-5"
    >
      <Tile
        label="Active reads"
        value={activeReads.toString()}
        accent={activeReads > 0 ? "live" : undefined}
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
    <div className="min-w-0">
      <div className="text-xs text-base-content/60">{label}</div>
      <div className={`break-words font-mono text-lg font-semibold tabular-nums ${valueClass}`}>
        {value}
      </div>
      {sub && <div className="text-[10px] leading-4 text-base-content/60">{sub}</div>}
    </div>
  );
}
