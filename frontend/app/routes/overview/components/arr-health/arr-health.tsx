import type {
  ArrHealthResponse,
  ArrInstanceStatus,
  OverviewWindow,
} from "~/clients/backend-client.server";
import { formatDurationMs, formatNumber, formatTimeAgo } from "../../utils/format";
import { settingsPath } from "~/navigation/settings-tabs";
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
          <div className="flex flex-wrap items-center gap-3">
            <WidgetLink to="/queue">Queue</WidgetLink>
            <WidgetLink to={settingsPath("arrs")}>Arr settings</WidgetLink>
          </div>
        </div>

        <div className="flex flex-wrap gap-2">
          <Chip
            label="Instances online"
            value={`${summary.instancesOnline}/${summary.instancesTotal}`}
          />
          <Chip label="Imports" value={formatNumber(summary.importsCompleted)} />
          <Chip label="Median handoff" value={formatDurationMs(summary.medianHandoffMs)} />
          <Chip label="P95" value={formatDurationMs(summary.p95HandoffMs)} />
          <Chip label="Awaiting" value={formatNumber(summary.awaitingImport)} />
          {summary.degraded > 0 && (
            <Chip label="Degraded" value={formatNumber(summary.degraded)} warning />
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
                  <th>Queue</th>
                  <th>Awaiting</th>
                  <th>Last import</th>
                </tr>
              </thead>
              <tbody>
                {instances.map((instance) => (
                  <tr key={instance.key}>
                    <td className="max-w-[220px] font-medium">
                      <span
                        className="inline-block max-w-full truncate align-middle"
                        title={instance.host}
                      >
                        {instance.name}
                      </span>
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

        {awaiting.length > 0 && (
          <div>
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <h4 className="text-xs uppercase tracking-wide text-base-content/50">
                Awaiting import
              </h4>
              <WidgetLink to="/queue">Open queue</WidgetLink>
            </div>
            <ul className="space-y-1">
              {awaiting.map((item, index) => (
                <li
                  key={`${item.instanceKey}-${item.title ?? "item"}-${index}`}
                  className={`text-xs ${item.isUnusual ? "text-warning" : "text-base-content/80"}`}
                >
                  {item.title ?? "(untitled)"} — {item.instanceName} — waiting{" "}
                  {formatDurationMs(item.waitingMs)}
                  {item.isUnusual ? " — unusually long" : ""}
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </section>
  );
}

function Chip({
  label,
  value,
  warning = false,
}: {
  label: string;
  value: string;
  warning?: boolean;
}) {
  return (
    <span className={`badge badge-sm gap-1 ${warning ? "badge-warning" : "badge-ghost"}`}>
      <span className="text-base-content/60">{label}</span>
      <span className="font-mono tabular-nums">{value}</span>
    </span>
  );
}
