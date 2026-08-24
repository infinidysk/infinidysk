import { formatBytes, formatNumber } from "../../utils/format";

export type LifetimeBlockProps = {
  lifetime: {
    bytesFetched: number;
    bytesRead: number;
    articles: number;
    readSessions: number;
    readSeconds: number;
    firstSeenAt: number | null;
  };
};

export function LifetimeBlock({ lifetime }: LifetimeBlockProps) {
  const isEmpty = lifetime.bytesRead === 0 && lifetime.articles === 0;

  return (
    <section className="w-full min-w-0">
      <header className="mb-2">
        <h3 className="text-sm font-semibold text-base-content">All time</h3>
        <p className="text-xs text-base-content/50">{since(lifetime.firstSeenAt)}</p>
      </header>

      {isEmpty ? (
        <p className="text-sm text-base-content/50">
          Lifetime totals appear after your first reads.
        </p>
      ) : (
        <div className="stats stats-vertical w-full border border-base-content/10 bg-base-100 sm:stats-horizontal">
          <Stat label="Read" value={formatBytes(lifetime.bytesRead)} />
          <Stat label="Articles" value={formatNumber(lifetime.articles)} />
          <Stat label="Read sessions" value={formatNumber(lifetime.readSessions)} />
          <Stat label="Active-reads time" value={formatHours(lifetime.readSeconds)} />
        </div>
      )}
    </section>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat py-3">
      <div className="stat-title text-xs">{label}</div>
      <div className="stat-value font-mono text-xl md:text-2xl">{value}</div>
    </div>
  );
}

function since(firstSeenAt: number | null): string {
  if (!firstSeenAt) return "Since you started";
  const days = Math.max(1, Math.floor((Date.now() - firstSeenAt) / 86_400_000));
  if (days < 30) return `Last ${days} days of activity`;
  const months = Math.floor(days / 30);
  if (months < 24) return `Last ${months} months of activity`;
  return `Last ${Math.floor(months / 12)} years of activity`;
}

function formatHours(seconds: number): string {
  if (seconds < 60) return `${seconds} s`;
  const m = seconds / 60;
  if (m < 60) return `${m.toFixed(0)} min`;
  const h = m / 60;
  if (h < 48) return `${h.toFixed(1)} h`;
  const d = h / 24;
  return `${d.toFixed(1)} d`;
}
