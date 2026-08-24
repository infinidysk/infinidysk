import type {
  ArrHealthResponse,
  ArrInstanceStatus,
  OverviewWindow,
} from "~/clients/backend-client.server";
import { formatDurationMs, formatNumber, formatTimeAgo } from "../../utils/format";
import { settingsPath } from "~/navigation/settings-tabs";
import { Tooltip } from "~/components/ui";
import { WidgetLink } from "../widget-link/widget-link";

export type ArrHealthProps = {
  data: ArrHealthResponse;
  window: OverviewWindow;
};

const STATUS_BADGE: Record<ArrInstanceStatus, string> = {
  healthy: "badge-success",
  degraded: "badge-warning",
  offline: "badge-error",
  pending: "badge-ghost",
};

export function ArrHealth({ data, window }: ArrHealthProps) {
  const { summary, instances, awaiting } = data;
  const sinceLabel = window === "all" ? "all time (~90 days of stored events)" : `last ${window}`;
  const groupedAwaiting = Array.from(
    awaiting.reduce((groups, item, index) => {
      const key = item.downloadId
        ? `${item.instanceKey}:${item.downloadId}`
        : `${item.instanceKey}:unidentified:${index}`;
      const group = groups.get(key);
      if (group) {
        group.items.push(item);
      } else {
        groups.set(key, { ...item, items: [item] });
      }
      return groups;
    }, new Map<string, (typeof awaiting)[number] & { items: (typeof awaiting)[number][] }>()),
  ).map(([, group]) => group);

  return (
    <section className="card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 className="card-title text-base">Arr Health</h3>
            <p className="text-xs text-base-content/50">
              InfiniDysk completion → Sonarr/Radarr import, {sinceLabel}
            </p>
          </div>
          <div className="card-actions m-0">
            <WidgetLink to="/queue">Queue</WidgetLink>
            <WidgetLink to={settingsPath("arrs")}>Arr settings</WidgetLink>
          </div>
        </div>

        <div className="stats stats-vertical w-full border border-base-content/10 sm:stats-horizontal">
          <MiniStat label="Online" value={`${summary.instancesOnline}/${summary.instancesTotal}`} />
          <MiniStat label="Imports" value={formatNumber(summary.importsCompleted)} />
          <MiniStat label="Median handoff" value={formatDurationMs(summary.medianHandoffMs)} />
          <MiniStat label="P95" value={formatDurationMs(summary.p95HandoffMs)} />
          <MiniStat label="Awaiting" value={formatNumber(summary.awaitingImport)} />
          {summary.degraded > 0 && (
            <MiniStat label="Degraded" value={formatNumber(summary.degraded)} warning />
          )}
        </div>

        {instances.length === 0 ? (
          <p className="py-6 text-center text-xs text-base-content/50">No imports recorded yet.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="table table-sm min-w-[720px]">
              <thead>
                <tr>
                  <th>Instance</th>
                  <th>Status</th>
                  <th>Imports</th>
                  <th>Median</th>
                  <th>P95</th>
                  <th>
                    <Tooltip content="All items currently in this Arr instance's queue.">
                      <span className="cursor-help">Queue</span>
                    </Tooltip>
                  </th>
                  <th>
                    <Tooltip content="Completed downloads that Arr is still importing or has marked import pending.">
                      <span className="cursor-help">Awaiting</span>
                    </Tooltip>
                  </th>
                  <th>Last import</th>
                </tr>
              </thead>
              <tbody>
                {instances.map((instance) => (
                  <tr key={instance.key}>
                    <td className="max-w-[220px] font-medium">
                      <Tooltip content={instance.host}>
                        <span className="inline-block max-w-full truncate align-middle">
                          {instance.name}
                        </span>
                      </Tooltip>
                      <span className="mt-0.5 block text-[11px] font-normal capitalize text-base-content/45">
                        {instance.appType}
                      </span>
                    </td>
                    <td>
                      <span className={`badge badge-sm ${STATUS_BADGE[instance.status]}`}>
                        {instance.status}
                      </span>
                    </td>
                    <td className="font-mono tabular-nums">{formatNumber(instance.imports)}</td>
                    <td className="font-mono tabular-nums">
                      {formatDurationMs(instance.medianHandoffMs)}
                    </td>
                    <td className="font-mono tabular-nums">
                      {formatDurationMs(instance.p95HandoffMs)}
                    </td>
                    <td className="font-mono tabular-nums">{formatNumber(instance.queueCount)}</td>
                    <td className="font-mono tabular-nums">
                      {formatNumber(instance.awaitingCount)}
                    </td>
                    <td className="font-mono tabular-nums text-base-content/80">
                      {instance.status === "offline" && !instance.lastImportAtMs
                        ? (instance.lastError ?? "Unreachable")
                        : formatTimeAgo(instance.lastImportAtMs)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {summary.importsCompleted === 0 && instances.length > 0 && (
          <p className="text-xs text-base-content/50">No imports recorded yet.</p>
        )}

        {groupedAwaiting.length > 0 && (
          <div>
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <h4 className="text-xs uppercase tracking-wide text-base-content/50">
                Awaiting import — {summary.awaitingShown} of {summary.awaitingImport} longest waits
              </h4>
              <WidgetLink to="/queue">Open queue</WidgetLink>
            </div>
            <ul className="list bg-base-100 p-0">
              {groupedAwaiting.map((item, index) => (
                <li
                  key={`${item.instanceKey}-${item.title ?? "item"}-${index}`}
                  className={`list-row py-2 text-xs ${item.isUnusual ? "text-warning" : "text-base-content/80"}`}
                >
                  <div className="list-col-grow">
                    {item.title ?? "(untitled)"} — {item.instanceName} — waiting{" "}
                    {formatDurationMs(item.waitingMs)}
                    {item.isUnusual ? " — unusually long" : ""}
                    {item.statusReason ? ` — ${item.statusReason}` : ""}
                    {!item.statusReason && item.trackedDownloadState
                      ? ` — ${item.trackedDownloadState}`
                      : ""}
                    {item.items.length > 1 ? ` — ${item.items.length} affected items` : ""}
                  </div>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </section>
  );
}

function MiniStat({
  label,
  value,
  warning = false,
}: {
  label: string;
  value: string;
  warning?: boolean;
}) {
  return (
    <div className="stat py-2">
      <div className="stat-title text-xs">{label}</div>
      <div className={`stat-value font-mono text-lg ${warning ? "text-warning" : ""}`}>{value}</div>
    </div>
  );
}
