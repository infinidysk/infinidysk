import type { HealthCheckStats, HealthResult, RepairAction } from "~/clients/backend-client.server";
import { Badge, Icon } from "~/components/ui";

export type HealthStatsProps = {
  stats: HealthCheckStats[];
};

// Numeric values mirror the backend HealthResult / RepairAction enums declared in
// ~/clients/backend-client.server (a .server module, so its enums cannot be value-imported here).
const HealthResultHealthy: HealthResult = 0;
const HealthResultDegraded: HealthResult = 2;
const RepairActionRepaired: RepairAction = 1;
const RepairActionDeleted: RepairAction = 2;
const RepairActionNeeded: RepairAction = 3;
const RepairActionRepairedViaPar2: RepairAction = 4;

export function HealthStats({ stats }: HealthStatsProps) {
  const totalChecked = stats.reduce((sum, stat) => sum + stat.count, 0);
  const counts = { healthy: 0, repaired: 0, deleted: 0, degraded: 0 };
  for (const stat of stats) {
    if (
      stat.repairStatus === RepairActionRepaired ||
      stat.repairStatus === RepairActionRepairedViaPar2
    ) {
      counts.repaired += stat.count;
    } else if (stat.repairStatus === RepairActionDeleted) {
      counts.deleted += stat.count;
    } else if (stat.repairStatus === RepairActionNeeded) {
      continue;
    } else if (stat.result === HealthResultHealthy) {
      counts.healthy += stat.count;
    } else if (stat.result === HealthResultDegraded) {
      counts.degraded += stat.count;
    }
  }

  const totalOutcomes = Object.values(counts).reduce((sum, count) => sum + count, 0);
  const percentage = (count: number) => {
    const value = totalOutcomes > 0 ? (count * 100) / totalOutcomes : 0;
    if (value > 0 && value < 0.1) return "<0.1%";
    if (value > 99.9 && value < 100) return ">99.9%";
    return `${value.toLocaleString(undefined, { maximumFractionDigits: 1 })}%`;
  };

  return (
    <section className="card w-full border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-4 p-4 md:p-6">
        <div className="space-y-1.5">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <h2 className="card-title text-xl">Overview</h2>
            <Badge className="badge-ghost badge-sm">Last 30 days</Badge>
          </div>
          <details className="text-xs leading-relaxed text-base-content/70">
            <summary className="cursor-pointer">About these results</summary>
            <p className="mt-2 max-w-prose">
              These are health-check results recorded during this period. A file can appear more
              than once, and these totals do not verify the Library Directory setting. Total checked
              includes all results; percentages cover only healthy, repaired, deleted, and degraded
              results.
            </p>
          </details>
        </div>

        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-5">
          <Stat
            icon="fact_check"
            iconClassName="text-base-content/50"
            title="Total checked"
            value={totalChecked}
          />
          <Stat
            icon="check_circle"
            iconClassName="text-success"
            iconFilled
            title={`Healthy (${percentage(counts.healthy)})`}
            value={counts.healthy}
            valueClassName="text-success"
          />
          <Stat
            icon="build_circle"
            iconClassName="text-info"
            iconFilled
            title={`Repaired (${percentage(counts.repaired)})`}
            value={counts.repaired}
            valueClassName="text-info"
          />
          <Stat
            icon="delete"
            iconClassName="text-error"
            iconFilled
            title={`Deleted (${percentage(counts.deleted)})`}
            value={counts.deleted}
            valueClassName="text-error"
          />
          <Stat
            icon="warning"
            iconClassName="text-warning"
            iconFilled
            title={`Degraded (${percentage(counts.degraded)})`}
            value={counts.degraded}
            valueClassName="text-warning"
          />
        </div>
      </div>
    </section>
  );
}

function Stat({
  icon,
  iconClassName,
  iconFilled,
  title,
  value,
  valueClassName = "",
}: {
  icon: string;
  iconClassName: string;
  iconFilled?: boolean;
  title: string;
  value: number;
  valueClassName?: string;
}) {
  return (
    <div className="min-w-0 py-2">
      <div className={`mb-1 ${iconClassName}`}>
        <Icon
          name={icon}
          {...(iconFilled !== undefined ? { filled: iconFilled } : {})}
          className="!text-[22px]"
        />
      </div>
      <div className="text-xs text-base-content/70">{title}</div>
      <div className={`font-mono text-2xl font-semibold tabular-nums ${valueClassName}`}>
        {value.toLocaleString()}
      </div>
    </div>
  );
}
