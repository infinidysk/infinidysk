import type { HealthCheckStats, HealthResult, RepairAction } from "~/clients/backend-client.server";
import { Badge, Icon } from "~/components/ui";

export type HealthStatsProps = {
    stats: HealthCheckStats[];
}

// Numeric values mirror the backend HealthResult / RepairAction enums declared in
// ~/clients/backend-client.server (a .server module, so its enums cannot be value-imported here).
const HealthResultHealthy: HealthResult = 0;
const RepairActionRepaired: RepairAction = 1;
const RepairActionDeleted: RepairAction = 2;

export function HealthStats({ stats }: HealthStatsProps) {
    const totalChecked = stats
        .reduce((sum, stat) => sum + stat.count, 0);
    const healthy = stats
        .filter(stat => stat.result === HealthResultHealthy)
        .reduce((sum, stat) => sum + stat.count, 0);
    const repaired = stats
        .filter(stat => stat.repairStatus === RepairActionRepaired)
        .reduce((sum, stat) => sum + stat.count, 0);
    const deleted = stats
        .filter(stat => stat.repairStatus === RepairActionDeleted)
        .reduce((sum, stat) => sum + stat.count, 0);

    const getPercentage = (count: number) => {
        return totalChecked > 0 ? Math.round((count / totalChecked) * 100) : 0;
    };

    return (
        <section className="card w-full border border-base-content/10 bg-base-100 shadow-sm">
            <div className="card-body gap-4 p-4 md:p-6">
                <div className="flex flex-wrap items-center justify-between gap-3">
                    <h2 className="card-title text-xl">Overview</h2>
                    <Badge className="badge-ghost badge-sm">Last 30 days</Badge>
                </div>

                <div className="stats stats-vertical w-full bg-base-200/40 sm:stats-horizontal">
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
                        title={`Healthy (${getPercentage(healthy)}%)`}
                        value={healthy}
                        valueClassName="text-success"
                    />
                    <Stat
                        icon="build_circle"
                        iconClassName="text-info"
                        iconFilled
                        title={`Repaired (${getPercentage(repaired)}%)`}
                        value={repaired}
                        valueClassName="text-info"
                    />
                    <Stat
                        icon="delete"
                        iconClassName="text-error"
                        iconFilled
                        title={`Deleted (${getPercentage(deleted)}%)`}
                        value={deleted}
                        valueClassName="text-error"
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
                <Icon name={icon} filled={iconFilled} className="!text-[22px]" />
            </div>
            <div className="stat-title text-xs">{title}</div>
            <div className={`stat-value font-mono text-3xl tabular-nums md:text-4xl ${valueClassName}`}>
                {value}
            </div>
        </div>
    );
}
