import { type Dispatch, type SetStateAction } from "react";
import {
  Input,
  ManagedSetting,
  Select,
  SettingsCard,
  SettingsIntro,
  SettingsPage,
} from "~/components/ui";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../validation";

type QueueSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

export function QueueSettings({ config, setNewConfig }: QueueSettingsProps) {
  const queueMaxItems = parseNonNegativeInteger(config["queue.max-items"]);
  const queueResumeThreshold = parseNonNegativeInteger(config["queue.resume-threshold"]);
  const queueAdmissionValid = isValidQueueAdmission(config);

  return (
    <SettingsPage>
      <SettingsIntro>
        Control how many NZBs can wait and process at once, and how much of your provider connection
        capacity queue imports may use.
      </SettingsIntro>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <SettingsCard
          icon="speed"
          title="Processing capacity"
          description="Set queue concurrency without increasing the provider connection budget."
        >
          <ManagedSetting configKey="queue.worker-count">
            <div className="space-y-2">
              <label
                className="block text-sm font-medium text-base-content"
                htmlFor="queue-worker-count-select"
              >
                Concurrent Queue Downloads
              </label>
              <Select
                className="w-full max-w-md"
                id="queue-worker-count-select"
                aria-describedby="queue-worker-count-help"
                value={config["queue.worker-count"] || "1"}
                onChange={(e) => setNewConfig({ ...config, "queue.worker-count": e.target.value })}
              >
                <option value="1">1 — one at a time (default)</option>
                <option value="2">2</option>
                <option value="3">3</option>
                <option value="4">4</option>
                <option value="5">5</option>
                <option value="6">6</option>
                <option value="7">7</option>
                <option value="8">8</option>
                <option value="9">9</option>
                <option value="10">10</option>
              </Select>
              <p
                className="text-[11px] leading-relaxed text-base-content/45"
                id="queue-worker-count-help"
              >
                How many NZB queue items may process at once. The oldest active item gets preferred
                access; additional items use spare capacity. Raising this does not increase Queue
                Download Connections.
              </p>
            </div>
          </ManagedSetting>

          <ManagedSetting configKey="usenet.max-queue-connections">
            <div className="space-y-2">
              <label
                className="block text-sm font-medium text-base-content"
                htmlFor="max-queue-connections-input"
              >
                Queue Download Connections
              </label>
              <Input
                {...className([
                  "w-full max-w-xs",
                  !isValidMaxQueueConnections(config["usenet.max-queue-connections"]) &&
                    "input-error",
                ])}
                type="text"
                inputMode="numeric"
                id="max-queue-connections-input"
                aria-describedby="max-queue-connections-help"
                placeholder="Auto (all connections)"
                value={config["usenet.max-queue-connections"]}
                onChange={(e) =>
                  setNewConfig({ ...config, "usenet.max-queue-connections": e.target.value })
                }
              />
              <p
                className="text-[11px] leading-relaxed text-base-content/45"
                id="max-queue-connections-help"
              >
                Connections shared by queue workers and background health checks. Leave blank to use
                all Pool provider connections. Streaming has its own connection allocation and
                controls how capacity is shared when playback and imports overlap.
              </p>
            </div>
          </ManagedSetting>
        </SettingsCard>

        <SettingsCard
          icon="playlist_add_check"
          title="Queue admission"
          description="Limit pending SAB submissions and decide when new jobs may be accepted again."
        >
          <ManagedSetting configKeys={["queue.max-items", "queue.resume-threshold"]}>
            <div className="space-y-4">
              <div className="space-y-2">
                <label
                  className="block text-sm font-medium text-base-content"
                  htmlFor="queue-max-items-input"
                >
                  Maximum queued jobs
                </label>
                <Input
                  className={`w-full max-w-48 ${queueAdmissionValid ? "" : "input-error"}`}
                  type="number"
                  min={0}
                  step={1}
                  id="queue-max-items-input"
                  aria-describedby="queue-max-items-help"
                  aria-invalid={!queueAdmissionValid}
                  value={config["queue.max-items"] ?? "0"}
                  onChange={(e) => setNewConfig({ ...config, "queue.max-items": e.target.value })}
                />
                <p
                  className="text-[11px] leading-relaxed text-base-content/45"
                  id="queue-max-items-help"
                >
                  Reject new SAB submissions when this many jobs are queued. Radarr and Sonarr keep
                  rejected grabs pending and retry later. Use <code>0</code> for no limit.
                </p>
              </div>

              <div className="space-y-2">
                <label
                  className="block text-sm font-medium text-base-content"
                  htmlFor="queue-resume-threshold-input"
                >
                  Resume threshold
                </label>
                <Input
                  className={`w-full max-w-48 ${queueAdmissionValid ? "" : "input-error"}`}
                  type="number"
                  min={0}
                  max={queueMaxItems ?? undefined}
                  step={1}
                  id="queue-resume-threshold-input"
                  aria-describedby="queue-resume-threshold-help"
                  aria-invalid={!queueAdmissionValid}
                  value={config["queue.resume-threshold"] ?? "0"}
                  disabled={queueMaxItems === 0}
                  onChange={(e) =>
                    setNewConfig({ ...config, "queue.resume-threshold": e.target.value })
                  }
                />
                <p
                  className="text-[11px] leading-relaxed text-base-content/45"
                  id="queue-resume-threshold-help"
                >
                  After the limit is reached, accept submissions again at or below this queue depth.
                  Use <code>0</code> to resume immediately below the maximum.
                  {queueResumeThreshold !== null &&
                  queueMaxItems !== null &&
                  queueResumeThreshold > queueMaxItems
                    ? " The threshold cannot exceed the maximum."
                    : ""}
                </p>
              </div>
            </div>
          </ManagedSetting>
        </SettingsCard>
      </div>
    </SettingsPage>
  );
}

export function isQueueSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
): boolean {
  return (
    config["queue.worker-count"] !== newConfig["queue.worker-count"] ||
    config["usenet.max-queue-connections"] !== newConfig["usenet.max-queue-connections"] ||
    config["queue.max-items"] !== newConfig["queue.max-items"] ||
    config["queue.resume-threshold"] !== newConfig["queue.resume-threshold"]
  );
}

export function isQueueSettingsValid(config: Record<string, string>): boolean {
  return (
    isValidQueueWorkerCount(config["queue.worker-count"]) &&
    isValidMaxQueueConnections(config["usenet.max-queue-connections"]) &&
    isValidQueueAdmission(config)
  );
}

function isValidQueueWorkerCount(value: string | undefined): boolean {
  if (value == null || value.trim() === "") return true;
  const number = Number(value);
  return Number.isInteger(number) && number >= 1 && number <= 10;
}

function isValidMaxQueueConnections(value: string | undefined): boolean {
  return value == null || value.trim() === "" || isPositiveInteger(value);
}

function isValidQueueAdmission(config: Record<string, string>): boolean {
  const maxItems = parseNonNegativeInteger(config["queue.max-items"]);
  const resumeThreshold = parseNonNegativeInteger(config["queue.resume-threshold"]);
  if (maxItems === null || resumeThreshold === null) return false;
  return maxItems === 0 || resumeThreshold === 0 || resumeThreshold <= maxItems;
}

function parseNonNegativeInteger(value: string | undefined): number | null {
  if (value === undefined || value.trim() === "") return 0;
  if (!/^\d+$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) ? parsed : null;
}
