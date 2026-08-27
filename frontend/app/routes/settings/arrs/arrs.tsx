import { Button } from "~/components/ui/button";
import { Alert, Spinner, Tooltip } from "~/components/ui/feedback";
import { SettingsCard, SettingsIntro, SettingsPage, ManagedSetting } from "~/components/ui";
import { Input, Select, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect } from "react";
import { withUrlBase } from "~/utils/url-base";

type ArrsSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

interface ConnectionDetails {
  Name?: string;
  Host: string;
  ApiKey: string;
  Enabled?: boolean;
}

interface QueueRule {
  Message: string;
  Action: number;
}

// Mirrors backend TestArrConnectionResponse (BaseApiResponse + Connected), camelCase JSON.
interface TestConnectionResult {
  status?: boolean;
  connected?: boolean;
  error?: string | null;
}

interface ArrConfig {
  RadarrInstances: ConnectionDetails[];
  SonarrInstances: ConnectionDetails[];
  QueueRules: QueueRule[];
  QueueReplacementSearchLimit?: number;
  QueueReplacementSearchWindowMinutes?: number;
}

function parseArrConfig(value: string): ArrConfig {
  try {
    // Config key "arr.instances" holds the backend ArrConfig JSON.
    const parsed = JSON.parse(value) as ArrConfig | null;
    if (
      parsed &&
      Array.isArray(parsed.RadarrInstances) &&
      Array.isArray(parsed.SonarrInstances) &&
      Array.isArray(parsed.QueueRules)
    ) {
      return parsed;
    }
  } catch {
    // invalid stored JSON: fall through to the default config below
  }

  return {
    RadarrInstances: [],
    SonarrInstances: [],
    QueueRules: [],
  };
}

const queueStatusMessages = [
  {
    display:
      "Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible.",
    searchTerm:
      "Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible.",
  },
  {
    display:
      "Found matching movie via grab history, but release was matched to movie by ID. Manual Import required.",
    searchTerm:
      "Found matching movie via grab history, but release was matched to movie by ID. Manual Import required.",
  },
  {
    display: "Episode was not found in the grabbed release",
    searchTerm: "was not found in the grabbed release",
  },
  {
    display: "Episode(s) was/were unexpected considering the folder name",
    searchTerm: "unexpected considering the",
  },
  {
    display: "Not an upgrade for existing episode file(s)",
    searchTerm: "Not an upgrade for existing episode file(s)",
  },
  {
    display: "Not an upgrade for existing movie file",
    searchTerm: "Not an upgrade for existing movie file",
  },
  {
    display: "Not a Custom Format upgrade",
    searchTerm: "Not a Custom Format upgrade",
  },
  {
    display: "No files found are eligible for import",
    searchTerm: "No files found are eligible for import",
  },
  {
    display: "Episode file already imported",
    searchTerm: "Episode file already imported",
  },
  {
    display: "No audio tracks detected",
    searchTerm: "No audio tracks detected",
  },
  {
    display: "Invalid season or episode",
    searchTerm: "Invalid season or episode",
  },
  {
    display: "Single episode file contains all episodes in seasons",
    searchTerm: "Single episode file contains all episodes in seasons",
  },
  {
    display: "Unable to determine if file is a sample",
    searchTerm: "Unable to determine if file is a sample",
  },
  {
    display: "Sample",
    searchTerm: "Sample",
  },
  {
    display: "Found archive file, might need to be extracted",
    searchTerm: "Found archive file, might need to be extracted",
  },
];

export function ArrsSettings({ config, setNewConfig }: ArrsSettingsProps) {
  const arrConfig = parseArrConfig(config["arr.instances"]!);

  const updateConfig = useCallback(
    (newArrConfig: ArrConfig) => {
      setNewConfig({ ...config, "arr.instances": JSON.stringify(newArrConfig) });
    },
    [config, setNewConfig],
  );

  const addRadarrInstance = useCallback(() => {
    updateConfig({
      ...arrConfig,
      RadarrInstances: [...arrConfig.RadarrInstances, { Host: "", ApiKey: "", Enabled: true }],
    });
  }, [arrConfig, updateConfig]);

  const removeRadarrInstance = useCallback(
    (index: number) => {
      updateConfig({
        ...arrConfig,
        RadarrInstances: arrConfig.RadarrInstances.filter((_, i) => i !== index),
      });
    },
    [arrConfig, updateConfig],
  );

  const updateRadarrInstance = useCallback(
    (index: number, field: keyof ConnectionDetails, value: string | boolean) => {
      updateConfig({
        ...arrConfig,
        RadarrInstances: arrConfig.RadarrInstances.map((instance, i) =>
          i === index ? { ...instance, [field]: value } : instance,
        ),
      });
    },
    [arrConfig, updateConfig],
  );

  const addSonarrInstance = useCallback(() => {
    updateConfig({
      ...arrConfig,
      SonarrInstances: [...arrConfig.SonarrInstances, { Host: "", ApiKey: "", Enabled: true }],
    });
  }, [arrConfig, updateConfig]);

  const removeSonarrInstance = useCallback(
    (index: number) => {
      updateConfig({
        ...arrConfig,
        SonarrInstances: arrConfig.SonarrInstances.filter((_, i) => i !== index),
      });
    },
    [arrConfig, updateConfig],
  );

  const updateSonarrInstance = useCallback(
    (index: number, field: keyof ConnectionDetails, value: string | boolean) => {
      updateConfig({
        ...arrConfig,
        SonarrInstances: arrConfig.SonarrInstances.map((instance, i) =>
          i === index ? { ...instance, [field]: value } : instance,
        ),
      });
    },
    [arrConfig, updateConfig],
  );

  const updateQueueAction = useCallback(
    (searchTerm: string, action: number) => {
      // update the queue rule if it already exists
      const newQueueRules = (arrConfig.QueueRules || [])
        .filter((queueRule: QueueRule) =>
          queueStatusMessages.map((x) => x.searchTerm).includes(queueRule.Message),
        )
        .map((queueRule: QueueRule) =>
          queueRule.Message == searchTerm ? { Message: searchTerm, Action: action } : queueRule,
        );

      // add the new queue rule if it doesn't already exist
      if (!newQueueRules.find((queueRule: QueueRule) => queueRule.Message == searchTerm))
        newQueueRules.push({ Message: searchTerm, Action: action });

      // update the config
      updateConfig({
        ...arrConfig,
        QueueRules: newQueueRules,
      });
    },
    [arrConfig, updateConfig],
  );

  const updateReplacementSearchBudget = useCallback(
    (
      field: "QueueReplacementSearchLimit" | "QueueReplacementSearchWindowMinutes",
      value: string,
    ) => {
      const limit = field === "QueueReplacementSearchLimit" ? 10 : 1440;
      const storedValue =
        field === "QueueReplacementSearchLimit"
          ? (arrConfig.QueueReplacementSearchLimit ?? 3)
          : (arrConfig.QueueReplacementSearchWindowMinutes ?? 30);
      const fallback = Math.max(1, Math.min(limit, storedValue));
      const parsed = Number.parseInt(value, 10);
      updateConfig({
        ...arrConfig,
        [field]: Number.isFinite(parsed) ? Math.max(1, Math.min(limit, parsed)) : fallback,
      });
    },
    [arrConfig, updateConfig],
  );

  return (
    <SettingsPage>
      <SettingsIntro>
        Connect supported Arr apps for automated replacement searches, then choose how InfiniDysk
        handles downloads stuck in their queues. Radarr and Sonarr are supported today, with room
        for additional Arr apps in the future.
      </SettingsIntro>

      <ManagedSetting configKey="arr.instances">
        <div className="flex flex-col gap-4">
          <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
            <SettingsCard
              icon="movie"
              title="Radarr instances"
              description="Movie managers that can search for replacement releases."
              action={
                <Button size="small" onClick={addRadarrInstance}>
                  <Icon name="add" className="!text-[18px]" />
                  Add
                </Button>
              }
            >
              {arrConfig.RadarrInstances.length === 0 ? (
                <div role="alert" className="alert alert-info alert-soft text-sm">
                  No Radarr instances configured. Click on the "Add" button to get started.
                </div>
              ) : (
                arrConfig.RadarrInstances.map((instance, index) => (
                  <InstanceForm
                    key={index}
                    instance={instance}
                    index={index}
                    type="radarr"
                    onUpdate={updateRadarrInstance}
                    onRemove={removeRadarrInstance}
                  />
                ))
              )}
            </SettingsCard>

            <SettingsCard
              icon="live_tv"
              title="Sonarr instances"
              description="Series managers that can search for replacement releases."
              action={
                <Button size="small" onClick={addSonarrInstance}>
                  <Icon name="add" className="!text-[18px]" />
                  Add
                </Button>
              }
            >
              {arrConfig.SonarrInstances.length === 0 ? (
                <div role="alert" className="alert alert-info alert-soft text-sm">
                  No Sonarr instances configured. Click on the "Add" button to get started.
                </div>
              ) : (
                arrConfig.SonarrInstances.map((instance, index) => (
                  <InstanceForm
                    key={index}
                    instance={instance}
                    index={index}
                    type="sonarr"
                    onUpdate={updateSonarrInstance}
                    onRemove={removeSonarrInstance}
                  />
                ))
              )}
            </SettingsCard>
          </div>

          <ManagedSetting configKey="arr.health-enabled">
            <SettingsCard
              icon="monitor_heart"
              title="Arr Health"
              description="Optional Overview metrics for import handoff from InfiniDysk to Sonarr and Radarr."
            >
              <Toggle
                id="arr-health-enabled"
                className="cursor-pointer gap-2 p-0"
                checked={config["arr.health-enabled"] !== "false"}
                onChange={(e) =>
                  setNewConfig({ ...config, "arr.health-enabled": "" + e.target.checked })
                }
                label={
                  <span className="text-sm text-base-content">Show Arr Health on Overview</span>
                }
              />
              <p className="text-[11px] leading-relaxed text-base-content/45">
                When this is off, enabled instances still handle queue rules and repairs, but
                InfiniDysk does not poll Arr APIs for import health and hides the Overview widget.
                The widget is also hidden when no instance is enabled.
              </p>
            </SettingsCard>
          </ManagedSetting>

          <SettingsCard
            icon="rule"
            title="Automatic queue management"
            description="Choose how matching Radarr and Sonarr queue warnings should be handled."
          >
            <div role="alert" className="alert alert-info alert-soft text-sm">
              Configure what to do for items stuck in Radarr / Sonarr queues. Different actions can
              be configured for different status messages. Only usenet queue items will be acted
              upon.
            </div>
            <div className="flex flex-wrap items-end gap-4 rounded-box border border-base-content/10 bg-base-200 p-3">
              <label className="flex flex-col gap-1 text-sm text-base-content/80">
                Automatic replacement searches
                <Input
                  type="number"
                  min={1}
                  max={10}
                  className="w-24"
                  value={arrConfig.QueueReplacementSearchLimit ?? 3}
                  onChange={(e) =>
                    updateReplacementSearchBudget("QueueReplacementSearchLimit", e.target.value)
                  }
                />
              </label>
              <label className="flex flex-col gap-1 text-sm text-base-content/80">
                Per window (minutes)
                <Input
                  type="number"
                  min={1}
                  max={1440}
                  className="w-28"
                  value={arrConfig.QueueReplacementSearchWindowMinutes ?? 30}
                  onChange={(e) =>
                    updateReplacementSearchBudget(
                      "QueueReplacementSearchWindowMinutes",
                      e.target.value,
                    )
                  }
                />
              </label>
              <p className="max-w-xl text-[11px] leading-relaxed text-base-content/55">
                “Remove, Blocklist, and Search” is limited per movie or episode. When the limit is
                reached, InfiniDysk still removes and blocklists the rejected release but does not
                trigger another automatic search.
              </p>
            </div>
            <ul className="divide-y divide-base-content/10 rounded-box border border-base-content/10 bg-base-200">
              {queueStatusMessages.map((queueStatusMessage, index) => {
                const selectId = `queue-rule-${index}`;
                return (
                  <li
                    key={index}
                    className="grid grid-cols-1 gap-2 p-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center sm:gap-4"
                  >
                    <label htmlFor={selectId} className="min-w-0 text-sm text-base-content/80">
                      {queueStatusMessage.display}
                    </label>
                    <Select
                      id={selectId}
                      className="select-sm w-full sm:w-auto sm:min-w-48 sm:justify-self-end"
                      value={
                        arrConfig.QueueRules.find(
                          (x: QueueRule) => x.Message == queueStatusMessage.searchTerm,
                        )?.Action ?? "0"
                      }
                      onChange={(e) =>
                        updateQueueAction(queueStatusMessage.searchTerm, Number(e.target.value))
                      }
                    >
                      <option value="0">Do Nothing</option>
                      <option value="1">Remove</option>
                      <option value="2">Remove and Blocklist</option>
                      <option value="3">Remove, Blocklist, and Search</option>
                    </Select>
                  </li>
                );
              })}
            </ul>
          </SettingsCard>
        </div>
      </ManagedSetting>
    </SettingsPage>
  );
}

interface InstanceFormProps {
  instance: ConnectionDetails;
  index: number;
  type: "radarr" | "sonarr";
  onUpdate: (index: number, field: keyof ConnectionDetails, value: string | boolean) => void;
  onRemove: (index: number) => void;
}

function InstanceForm({ instance, index, type, onUpdate, onRemove }: InstanceFormProps) {
  const [connectionState, setConnectionState] = useState<"idle" | "testing" | "success" | "error">(
    "idle",
  );
  const [testError, setTestError] = useState<string | null>(null);

  useEffect(() => {
    setConnectionState("idle");
    setTestError(null);
  }, [instance.Host, instance.ApiKey]);

  const testConnection = useCallback(async (host: string, apiKey: string) => {
    if (!host.trim() || !apiKey.trim()) {
      return;
    }

    setConnectionState("testing");
    setTestError(null);

    try {
      const formData = new FormData();
      formData.append("host", host);
      formData.append("apiKey", apiKey);

      const response = await fetch(withUrlBase("/api/test-arr-connection"), {
        method: "POST",
        body: formData,
      });

      // Response of POST /api/test-arr-connection (backend TestArrConnectionResponse).
      const result = (await response.json()) as TestConnectionResult;

      if (result.status && result.connected) {
        setConnectionState("success");
        setTestError(null);
      } else {
        setConnectionState("error");
        setTestError(result.error || "Connection test failed");
      }
    } catch (error) {
      setConnectionState("error");
      setTestError(error instanceof Error ? error.message : "Connection test failed");
    }
  }, []);

  return (
    <div className={"relative rounded-lg border border-base-content/10 bg-base-100 p-4 shadow-md"}>
      <button
        className={
          "absolute right-2 top-2 rounded p-1 text-base-content/60 hover:bg-base-content/10 hover:text-error"
        }
        onClick={() => onRemove(index)}
        aria-label="Remove instance"
      >
        <Icon name="close" className="!text-[18px]" />
      </button>
      <div className="space-y-4">
        <Toggle
          id={`${type}-${index}-enabled`}
          className="cursor-pointer gap-2 p-0"
          checked={instance.Enabled !== false}
          onChange={(e) => onUpdate(index, "Enabled", e.target.checked)}
          label={<span className="text-sm text-base-content">Enabled</span>}
        />
        <div className="space-y-2">
          <label
            className="block text-sm font-medium text-base-content"
            htmlFor={`${type}-${index}-name`}
          >
            Name
          </label>
          <Input
            id={`${type}-${index}-name`}
            type="text"
            placeholder={type === "radarr" ? "Radarr" : "Sonarr"}
            value={instance.Name ?? ""}
            onChange={(e) => onUpdate(index, "Name", e.target.value)}
          />
          <p className="text-[11px] leading-relaxed text-base-content/45">
            Optional display name on Overview. Defaults to the host URL.
          </p>
        </div>
        <div className="space-y-2">
          <label
            className="block text-sm font-medium text-base-content"
            htmlFor={`${type}-${index}-host`}
          >
            Host
          </label>
          <div className="join w-full">
            <Input
              id={`${type}-${index}-host`}
              type="text"
              className="join-item min-w-0 flex-1"
              placeholder={type === "radarr" ? "http://localhost:7878" : "http://localhost:8989"}
              value={instance.Host}
              onChange={(e) => onUpdate(index, "Host", e.target.value)}
            />
            {instance.Host.trim() && instance.ApiKey.trim() && (
              <Tooltip content="Tests host, credentials, and API response">
                <Button
                  variant={
                    connectionState === "success"
                      ? "success"
                      : connectionState === "error"
                        ? "danger"
                        : "secondary"
                  }
                  onClick={() => void testConnection(instance.Host, instance.ApiKey)}
                  disabled={connectionState === "testing"}
                  className="join-item shrink-0"
                >
                  {connectionState === "testing" ? (
                    <Spinner />
                  ) : connectionState === "success" ? (
                    <Icon name="check" className="!text-[18px]" />
                  ) : connectionState === "error" ? (
                    <Icon name="close" className="!text-[18px]" />
                  ) : (
                    "Test Conn"
                  )}
                </Button>
              </Tooltip>
            )}
          </div>
          {connectionState === "error" && testError && (
            <Alert variant="danger" className="text-xs py-2">
              {testError}
            </Alert>
          )}
          {connectionState === "success" && (
            <Alert variant="success" className="text-xs py-2">
              Connection test successful
            </Alert>
          )}
        </div>
        <div className="space-y-2">
          <label className="block text-sm font-medium text-base-content">API Key</label>
          <Input
            type="password"
            className={"w-full"}
            value={instance.ApiKey}
            onChange={(e) => onUpdate(index, "ApiKey", e.target.value)}
          />
        </div>
      </div>
    </div>
  );
}

export function isArrsSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return (
    config["arr.instances"] !== newConfig["arr.instances"] ||
    config["arr.health-enabled"] !== newConfig["arr.health-enabled"]
  );
}

export function isArrsSettingsValid(newConfig: Record<string, string>) {
  try {
    // Config key "arr.instances" holds the backend ArrConfig JSON.
    const arrConfig = JSON.parse(newConfig["arr.instances"] || "{}") as ArrConfig;

    // Validate all Radarr instances
    for (const instance of arrConfig.RadarrInstances || []) {
      if (!isValidHost(instance.Host) || !isValidApiKey(instance.ApiKey)) {
        return false;
      }
    }

    // Validate all Sonarr instances
    for (const instance of arrConfig.SonarrInstances || []) {
      if (!isValidHost(instance.Host) || !isValidApiKey(instance.ApiKey)) {
        return false;
      }
    }

    return true;
  } catch {
    return false;
  }
}

function isValidHost(host: string): boolean {
  if (host.trim().length === 0) return false;
  try {
    new URL(host);
    return true;
  } catch {
    return false;
  }
}

function isValidApiKey(apiKey: string): boolean {
  return apiKey.trim().length > 0;
}
