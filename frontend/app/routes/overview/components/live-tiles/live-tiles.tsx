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
      className="stats w-full border border-base-content/10 bg-base-200 shadow max-sm:grid-flow-row max-sm:grid-cols-3 max-sm:gap-px max-sm:bg-base-content/10"
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
        className="max-sm:col-span-2"
      />
    </div>
  );
}

function Tile({
  label,
  value,
  sub,
  accent,
  className,
}: {
  label: string;
  value: string;
  sub?: string | undefined;
  accent?: "live" | "danger" | undefined;
  className?: string | undefined;
}) {
  const valueClass = accent === "live" ? "text-success" : accent === "danger" ? "text-error" : "";
  return (
    <div
      className={`stat px-3 py-2.5 sm:px-4 sm:py-4 lg:px-6 max-sm:min-w-0 max-sm:border-e-0 max-sm:bg-base-200 ${className ?? ""}`}
    >
      {accent && (
        <div className="stat-figure">
          <span className={`status ${accent === "live" ? "status-success" : "status-error"}`} />
        </div>
      )}
      <div className="stat-title max-sm:whitespace-normal max-sm:break-words">{label}</div>
      <div
        className={`stat-value font-mono text-lg sm:text-xl md:text-2xl lg:text-3xl max-sm:whitespace-normal max-sm:break-words ${valueClass}`}
      >
        {value}
      </div>
      {sub && <div className="stat-desc max-sm:whitespace-normal max-sm:break-words">{sub}</div>}
    </div>
  );
}
