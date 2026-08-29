import { formatBytes } from "../../utils/format";

export type RecordsBlockProps = {
  records: {
    bestDayBytes: number;
    bestDayAt: number | null;
    bestHourBytes: number;
    bestHourAt: number | null;
  };
};

export function RecordsBlock({ records }: RecordsBlockProps) {
  const isEmpty = records.bestDayBytes === 0 && records.bestHourBytes === 0;

  return (
    <section className="w-full min-w-0">
      <header className="mb-2">
        <h3 className="text-sm font-semibold text-base-content">Records</h3>
        <p className="text-xs text-base-content/50">Personal bests since you started</p>
      </header>

      {isEmpty ? (
        <p className="text-sm text-base-content/50">Records appear after some activity.</p>
      ) : (
        <div className="stats w-full border border-base-content/10 bg-base-100">
          <Stat
            label="Busiest day"
            value={formatBytes(records.bestDayBytes)}
            desc={records.bestDayAt ? formatDay(records.bestDayAt) : undefined}
          />
          <Stat
            label="Busiest hour"
            value={formatBytes(records.bestHourBytes)}
            desc={records.bestHourAt ? formatHour(records.bestHourAt) : undefined}
          />
        </div>
      )}
    </section>
  );
}

function Stat({ label, value, desc }: { label: string; value: string; desc?: string | undefined }) {
  return (
    <div className="stat py-3">
      <div className="stat-title text-xs">{label}</div>
      <div className="stat-value font-mono text-xl md:text-2xl">{value}</div>
      {desc && <div className="stat-desc">{desc}</div>}
    </div>
  );
}

function formatDay(ms: number): string {
  const d = new Date(ms);
  return d.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

function formatHour(ms: number): string {
  const d = new Date(ms);
  const day = d.toLocaleDateString(undefined, { day: "numeric", month: "short" });
  const hh = String(d.getHours()).padStart(2, "0");
  return `${day} · ${hh}:00`;
}
