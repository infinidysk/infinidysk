import { ManagedSetting, SettingsIntro, SettingsPage, Tooltip } from "~/components/ui";
import { InputGroup, Select, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import { PruneCompletedHistory } from "./prune-completed-history/prune-completed-history";
import { RemoveUnlinkedFiles } from "./remove-unlinked-files/remove-unlinked-files";
import { RenameWindowsInvalidDavPaths } from "./rename-windows-invalid-dav-paths/rename-windows-invalid-dav-paths";
import { ConvertStrmToSymlinks } from "./strm-to-symlinks/strm-to-symlinks";
import { RecreateStrmFiles } from "./recreate-strm-files/recreate-strm-files";
import { MigrateDatabaseFilesToBlobstore } from "./migrate-database-files-to-blobstore/migrate-database-files-to-blobstore";
import { ResetHealthCheckStats } from "./reset-health-check-stats/reset-health-check-stats";
import { ResetOverviewStats } from "./reset-overview-stats/reset-overview-stats";

type MaintenanceProps = {
  savedConfig: Record<string, string>;
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

export function Maintenance({ savedConfig, config, setNewConfig }: MaintenanceProps) {
  const orphanScheduleEnabled = isScheduledOrphanTaskEnabled(config);
  const scheduledTime = getScheduledTime(config);

  return (
    <SettingsPage>
      <SettingsIntro>
        Configure routine database housekeeping and schedule automatic orphan cleanup. Run one-off
        repair and migration tools from the maintenance task panels below.
      </SettingsIntro>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <ManagedSetting
          configKeys={[
            "db.is-startup-vacuum-enabled",
            "database.history-retention-days",
            "database.healthcheck-retention-days",
            "metrics.fetch-retention-hours",
          ]}
        >
          <section className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
            <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
              <span className="rounded-lg bg-primary/10 p-2 text-primary">
                <Icon name="database" className="!text-[20px]" />
              </span>
              <div>
                <h2 className="text-sm font-semibold text-base-content">Database upkeep</h2>
                <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">
                  Control startup optimization and automatic history pruning.
                </p>
              </div>
            </div>

            <div className="space-y-4 p-4">
              <Tooltip content="Reclaim unused SQLite space. Large databases may take longer to start.">
                <Toggle
                  id="db-startup-vacuum-enabled-checkbox"
                  className="cursor-pointer gap-2 rounded-lg bg-base-200/40 p-3"
                  checked={config["db.is-startup-vacuum-enabled"] === "true"}
                  onChange={(e) =>
                    setNewConfig({
                      ...config,
                      "db.is-startup-vacuum-enabled": String(e.target.checked),
                    })
                  }
                  label={
                    <span className="text-sm font-medium text-base-content">Vacuum on startup</span>
                  }
                />
              </Tooltip>

              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div className="space-y-2">
                  <label
                    className="block text-sm font-medium text-base-content"
                    htmlFor="history-retention-days"
                  >
                    SAB history retention
                  </label>
                  <InputGroup
                    className="w-full max-w-48"
                    id="history-retention-days"
                    type="number"
                    min={0}
                    suffix="days"
                    aria-describedby="history-retention-days-help"
                    value={config["database.history-retention-days"] ?? "90"}
                    onChange={(e) =>
                      setNewConfig({
                        ...config,
                        "database.history-retention-days": e.target.value,
                      })
                    }
                  />
                  <p
                    className="text-[11px] leading-relaxed text-base-content/45"
                    id="history-retention-days-help"
                  >
                    Prunes SAB history without deleting WebDAV files or making them eligible for
                    orphan cleanup. Set to 0 to keep everything. Environment:{" "}
                    <code className="break-all font-mono">DATABASE_HISTORY_RETENTION_DAYS</code>.
                  </p>
                </div>

                <div className="space-y-2">
                  <label
                    className="block text-sm font-medium text-base-content"
                    htmlFor="healthcheck-retention-days"
                  >
                    Health-check retention
                  </label>
                  <InputGroup
                    className="w-full max-w-48"
                    id="healthcheck-retention-days"
                    type="number"
                    min={0}
                    suffix="days"
                    aria-describedby="healthcheck-retention-days-help"
                    value={config["database.healthcheck-retention-days"] ?? "30"}
                    onChange={(e) =>
                      setNewConfig({
                        ...config,
                        "database.healthcheck-retention-days": e.target.value,
                      })
                    }
                  />
                  <p
                    className="text-[11px] leading-relaxed text-base-content/45"
                    id="healthcheck-retention-days-help"
                  >
                    Prunes old health-check results. Set to 0 to keep everything. Environment:{" "}
                    <code className="break-all font-mono">DATABASE_HEALTHCHECK_RETENTION_DAYS</code>
                    .
                  </p>
                </div>

                <div className="space-y-2 sm:col-span-2">
                  <label
                    className="block text-sm font-medium text-base-content"
                    htmlFor="metrics-fetch-retention-hours"
                  >
                    Raw fetch-event retention
                  </label>
                  <InputGroup
                    className="w-full max-w-48"
                    id="metrics-fetch-retention-hours"
                    type="number"
                    min={0}
                    max={8760}
                    suffix="hours"
                    aria-describedby="metrics-fetch-retention-hours-help"
                    value={config["metrics.fetch-retention-hours"] ?? "24"}
                    onChange={(e) =>
                      setNewConfig({
                        ...config,
                        "metrics.fetch-retention-hours": e.target.value,
                      })
                    }
                  />
                  <p
                    className="text-[11px] leading-relaxed text-base-content/45"
                    id="metrics-fetch-retention-hours-help"
                  >
                    Prunes raw per-fetch metrics rows. Minute and hourly rollups are kept
                    regardless. Set to 0 for rollup-only retention (one-hour floor). Environment:
                    <code className="break-all font-mono">METRICS_FETCH_RETENTION_HOURS</code>.
                  </p>
                </div>
              </div>
            </div>
          </section>
        </ManagedSetting>

        <ManagedSetting
          configKeys={[
            "maintenance.remove-orphaned-schedule-enabled",
            "maintenance.remove-orphaned-schedule-time",
          ]}
        >
          <section className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
            <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
              <span className="rounded-lg bg-primary/10 p-2 text-primary">
                <Icon name="event_repeat" className="!text-[20px]" />
              </span>
              <div>
                <h2 className="text-sm font-semibold text-base-content">Scheduled cleanup</h2>
                <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">
                  Run Remove Orphaned Files automatically once per day.
                </p>
              </div>
            </div>

            <div className="space-y-4 p-4">
              <Tooltip content="Runs the same protected Remove Orphaned Files cleanup available in the task panel below.">
                <Toggle
                  id="remove-orphaned-schedule-enabled-checkbox"
                  className="cursor-pointer gap-2 rounded-lg bg-base-200/40 p-3"
                  checked={orphanScheduleEnabled}
                  onChange={(e) =>
                    setNewConfig({
                      ...config,
                      "maintenance.remove-orphaned-schedule-enabled": String(e.target.checked),
                    })
                  }
                  label={
                    <span className="text-sm font-medium text-base-content">
                      Enable daily cleanup
                    </span>
                  }
                />
              </Tooltip>

              <fieldset className="space-y-2" disabled={!orphanScheduleEnabled}>
                <legend className="text-xs font-medium uppercase tracking-wide text-base-content/50">
                  Daily run time
                </legend>
                <div className="grid grid-cols-3 gap-2">
                  <label className="space-y-1">
                    <span className="block text-[11px] text-base-content/45">Hour</span>
                    <Select
                      className="w-full"
                      aria-label="Cleanup hour"
                      value={scheduledTime.hour}
                      onChange={(e) =>
                        setNewConfig({
                          ...config,
                          "maintenance.remove-orphaned-schedule-time": buildScheduledTime(
                            parseInt(e.target.value),
                            scheduledTime.minute,
                            scheduledTime.period,
                          ),
                        })
                      }
                    >
                      {Array.from({ length: 12 }, (_, i) => i + 1).map((h) => (
                        <option key={h} value={h}>
                          {h}
                        </option>
                      ))}
                    </Select>
                  </label>
                  <label className="space-y-1">
                    <span className="block text-[11px] text-base-content/45">Minute</span>
                    <Select
                      className="w-full"
                      aria-label="Cleanup minute"
                      value={scheduledTime.minute}
                      onChange={(e) =>
                        setNewConfig({
                          ...config,
                          "maintenance.remove-orphaned-schedule-time": buildScheduledTime(
                            scheduledTime.hour,
                            parseInt(e.target.value),
                            scheduledTime.period,
                          ),
                        })
                      }
                    >
                      <option value={0}>00</option>
                      <option value={15}>15</option>
                      <option value={30}>30</option>
                      <option value={45}>45</option>
                    </Select>
                  </label>
                  <label className="space-y-1">
                    <span className="block text-[11px] text-base-content/45">Period</span>
                    <Select
                      className="w-full"
                      aria-label="Cleanup period"
                      value={scheduledTime.period}
                      onChange={(e) =>
                        setNewConfig({
                          ...config,
                          "maintenance.remove-orphaned-schedule-time": buildScheduledTime(
                            scheduledTime.hour,
                            scheduledTime.minute,
                            e.target.value as "am" | "pm",
                          ),
                        })
                      }
                    >
                      <option value="am">AM</option>
                      <option value="pm">PM</option>
                    </Select>
                  </label>
                </div>
              </fieldset>

              <div className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55">
                <Icon
                  name="schedule"
                  className="mt-0.5 !text-[17px] shrink-0 text-base-content/45"
                />
                <p>
                  Schedule times use the server timezone. Set{" "}
                  <code className="font-mono text-base-content/70">TZ</code> in the container
                  environment if the displayed time does not match your location.
                </p>
              </div>
            </div>
          </section>
        </ManagedSetting>
      </div>

      <section className="space-y-3">
        <div className="flex items-end justify-between gap-4">
          <div>
            <h2 className="text-lg font-semibold text-base-content">Maintenance tasks</h2>
            <p className="mt-1 text-xs leading-relaxed text-base-content/50">
              Run repair, migration, and destructive cleanup tools on demand.
            </p>
          </div>
          <span className="badge badge-ghost badge-sm shrink-0">8 tools</span>
        </div>
        <div className="space-y-3">
          <MaintenanceTaskDetails title="Remove Orphaned Files">
            <RemoveUnlinkedFiles savedConfig={savedConfig} />
          </MaintenanceTaskDetails>
          <MaintenanceTaskDetails title="Prune Completed History">
            <PruneCompletedHistory savedConfig={savedConfig} />
          </MaintenanceTaskDetails>
          <MaintenanceTaskDetails title="Rename Windows-Invalid Paths">
            <RenameWindowsInvalidDavPaths savedConfig={savedConfig} />
          </MaintenanceTaskDetails>
          <MaintenanceTaskDetails title="Convert STRM Files to Symlinks">
            <ConvertStrmToSymlinks savedConfig={savedConfig} />
          </MaintenanceTaskDetails>
          <MaintenanceTaskDetails title="Recreate STRM Files">
            <RecreateStrmFiles savedConfig={savedConfig} />
          </MaintenanceTaskDetails>
          <MaintenanceTaskDetails title="Migrate Large Database Blobs to Blobstore">
            <MigrateDatabaseFilesToBlobstore savedConfig={savedConfig} />
          </MaintenanceTaskDetails>
          <MaintenanceTaskDetails title="Reset Health-Check Statistics">
            <ResetHealthCheckStats />
          </MaintenanceTaskDetails>
          <MaintenanceTaskDetails title="Reset Overview Statistics">
            <ResetOverviewStats />
          </MaintenanceTaskDetails>
        </div>
      </section>
    </SettingsPage>
  );
}

function MaintenanceTaskDetails({ title, children }: { title: string; children: ReactNode }) {
  return (
    <details className="collapse collapse-arrow overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
      <summary className="collapse-title min-h-0 cursor-pointer px-4 py-3 text-sm font-semibold text-base-content transition-colors hover:bg-base-content/5">
        {title}
      </summary>
      <div className="collapse-content border-t border-base-content/10 px-4 pb-4 pt-4">
        {children}
      </div>
    </details>
  );
}

export function isMaintenanceSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return (
    config["db.is-startup-vacuum-enabled"] !== newConfig["db.is-startup-vacuum-enabled"] ||
    config["database.history-retention-days"] !== newConfig["database.history-retention-days"] ||
    config["database.healthcheck-retention-days"] !==
      newConfig["database.healthcheck-retention-days"] ||
    config["metrics.fetch-retention-hours"] !== newConfig["metrics.fetch-retention-hours"] ||
    config["maintenance.remove-orphaned-schedule-enabled"] !==
      newConfig["maintenance.remove-orphaned-schedule-enabled"] ||
    config["maintenance.remove-orphaned-schedule-time"] !==
      newConfig["maintenance.remove-orphaned-schedule-time"]
  );
}

function isScheduledOrphanTaskEnabled(config: Record<string, string>) {
  return config["maintenance.remove-orphaned-schedule-enabled"] === "true";
}

function getScheduledTime(config: Record<string, string>): {
  hour: number;
  minute: number;
  period: "am" | "pm";
} {
  const totalMinutes = parseInt(config["maintenance.remove-orphaned-schedule-time"] || "0");
  const hour24 = Math.floor(totalMinutes / 60);
  return {
    hour: hour24 % 12 || 12, // 0→12 (midnight), 1→1, ..., 12→12 (noon), 13→1, ...
    minute: totalMinutes % 60,
    period: Math.floor(totalMinutes / 60) >= 12 ? "pm" : "am",
  };
}

function buildScheduledTime(hour: number, minute: number, period: "am" | "pm"): string {
  const hour24 = (hour % 12) + (period === "pm" ? 12 : 0);
  return "" + (hour24 * 60 + minute);
}
