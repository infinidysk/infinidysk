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
  const percentages = { ...counts };
  if (totalOutcomes > 0) {
    const outcomes = Object.keys(counts) as (keyof typeof counts)[];
    for (const outcome of outcomes) {
      percentages[outcome] = Math.floor((counts[outcome] * 100) / totalOutcomes);
    }
    const remaining = 100 - Object.values(percentages).reduce((sum, value) => sum + value, 0);
    const largestRemainders = outcomes.sort(
      (first, second) =>
        ((counts[second] * 100) % totalOutcomes) - ((counts[first] * 100) % totalOutcomes),
    );
    for (const outcome of largestRemainders.slice(0, remaining)) {
      percentages[outcome] += 1;
    }
  }

  return (
    <section className="card w-full border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-4 p-4 md:p-6">
        <div className="space-y-1.5">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <h2 className="card-title text-xl">Overview</h2>
            <Badge className="badge-ghost badge-sm">Last 30 days</Badge>
          </div>
          <p className="text-xs leading-relaxed text-base-content/55">
            These are health-check results recorded during this period. A file can appear more than
            once, and these totals do not verify the Library Directory setting. Total checked
            includes all results; percentages cover only healthy, repaired, deleted, and degraded
            results.
          </p>
        </div>

        <div className="stats stats-vertical w-full bg-base-200/40 xl:stats-horizontal">
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
            title={`Healthy (${percentages.healthy}%)`}
            value={counts.healthy}
            valueClassName="text-success"
          />
          <Stat
            icon="build_circle"
            iconClassName="text-info"
            iconFilled
            title={`Repaired (${percentages.repaired}%)`}
            value={counts.repaired}
            valueClassName="text-info"
          />
          <Stat
            icon="delete"
            iconClassName="text-error"
            iconFilled
            title={`Deleted (${percentages.deleted}%)`}
            value={counts.deleted}
            valueClassName="text-error"
          />
          <Stat
            icon="warning"
            iconClassName="text-warning"
            iconFilled
            title={`Degraded (${percentages.degraded}%)`}
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
    <div className="stat place-items-center py-4">
      <div className={`stat-figure ${iconClassName}`}>
        <Icon
          name={icon}
          {...(iconFilled !== undefined ? { filled: iconFilled } : {})}
          className="!text-[22px]"
        />
      </div>
      <div className="stat-title text-xs">{title}</div>
      <div className={`stat-value font-mono text-3xl tabular-nums md:text-4xl ${valueClassName}`}>
        {value}
      </div>
    </div>
  );
}
