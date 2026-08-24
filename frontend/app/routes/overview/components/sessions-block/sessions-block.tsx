import type { OverviewWindow } from "~/clients/backend-client.server";
import { formatBytes, formatNumber } from "../../utils/format";

export type SessionsBlockProps = {
  sessions: {
    count: number;
    totalBytesServed: number;
    avgDurationMs: number;
    longestDurationMs: number;
    biggestReadBytes: number;
  };
  window: OverviewWindow;
};

export function SessionsBlock({ sessions, window }: SessionsBlockProps) {
  return (
    <section className="w-full min-w-0">
      <header className="mb-2">
        <h3 className="text-sm font-semibold text-base-content">Read sessions</h3>
        <p className="text-xs text-base-content/50">
          {window === "all" ? "All time" : `Last ${window}`}
        </p>
      </header>

      {sessions.count === 0 ? (
        <p className="text-sm text-base-content/50">No completed read sessions yet.</p>
      ) : (
        <div className="stats stats-vertical w-full border border-base-content/10 bg-base-100 sm:stats-horizontal">
          <Stat label="Sessions" value={formatNumber(sessions.count)} />
          <Stat label="Bytes served" value={formatBytes(sessions.totalBytesServed)} />
          <Stat label="Avg duration" value={formatDuration(sessions.avgDurationMs)} />
          <Stat label="Longest read" value={formatDuration(sessions.longestDurationMs)} />
          <Stat label="Biggest single read" value={formatBytes(sessions.biggestReadBytes)} />
        </div>
      )}
    </section>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat px-3 py-3">
      <div className="stat-title text-xs">{label}</div>
      <div className="stat-value font-mono text-xl md:text-2xl">{value}</div>
    </div>
  );
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(1)} s`;
  const m = s / 60;
  if (m < 60) return `${m.toFixed(1)} m`;
  return `${(m / 60).toFixed(1)} h`;
}
