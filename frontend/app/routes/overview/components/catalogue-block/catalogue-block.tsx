import { formatBytes, formatNumber } from "../../utils/format";

export type CatalogueBlockProps = {
  catalogue: {
    fileCount: number;
    totalBytes: number;
    largestFileBytes: number;
    addedLast7Days: number;
  };
};

export function CatalogueBlock({ catalogue }: CatalogueBlockProps) {
  return (
    <section className="w-full min-w-0">
      <header className="mb-2">
        <h3 className="text-sm font-semibold text-base-content">Catalogue</h3>
        <p className="text-xs text-base-content/50">Your mounted library</p>
      </header>

      <div className="stats stats-vertical w-full border border-base-content/10 bg-base-100 sm:stats-horizontal">
        <Stat label="Files" value={formatNumber(catalogue.fileCount)} />
        <Stat label="Total size" value={formatBytes(catalogue.totalBytes)} />
        <Stat label="Largest file" value={formatBytes(catalogue.largestFileBytes)} />
        <Stat
          label="Added 7d"
          value={formatNumber(catalogue.addedLast7Days)}
          accent={catalogue.addedLast7Days > 0 ? "good" : undefined}
        />
      </div>
    </section>
  );
}

function Stat({
  label,
  value,
  accent,
}: {
  label: string;
  value: string;
  accent?: "good" | undefined;
}) {
  return (
    <div className="stat py-3">
      <div className="stat-title text-xs">{label}</div>
      <div
        className={`stat-value font-mono text-xl md:text-2xl ${accent === "good" ? "text-success" : ""}`}
      >
        {value}
      </div>
    </div>
  );
}
