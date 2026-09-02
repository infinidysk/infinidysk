import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Alert,
  Badge,
  Button,
  Check,
  Field,
  HelpText,
  Icon,
  Input,
  Label,
  ManagedSetting,
  RadioJoinFilter,
  Spinner,
  Toggle,
  Tooltip,
} from "~/components/ui";
import type {
  SetupWizardIngestionMethod,
  SetupWizardStrategy,
} from "~/clients/backend-client.server";
import { withUrlBase } from "~/utils/url-base";
import {
  applyStrategy,
  minutesFromTime,
  normalizeStrategy,
  parseArrConfig,
  serializeArrConfig,
  timeFromMinutes,
  type ArrConfig,
  type ArrInstance,
  type SetupDraft,
} from "./setup-model";
import type { ManagedEnvMap } from "~/components/ui";

export const SETUP_STEPS = [
  "Library type",
  "Playback",
  "Ingestion",
  "Backups",
  "Library",
  "Review",
] as const;

type UpdateDraft = (updater: (draft: SetupDraft) => SetupDraft) => void;

export function SetupProgress({ step }: { step: number }) {
  return (
    <>
      <ol className="steps steps-horizontal hidden w-full md:grid" aria-label="Setup progress">
        {SETUP_STEPS.map((label, index) => (
          <li
            key={label}
            className={`step text-xs ${index <= step ? "step-primary" : ""}`}
            data-content={String(index + 1)}
            aria-current={index === step ? "step" : undefined}
          >
            {label}
          </li>
        ))}
      </ol>
      <div className="space-y-2 md:hidden">
        <div className="flex items-center justify-between gap-3 text-sm">
          <span className="font-medium">{SETUP_STEPS[step]}</span>
          <span className="text-base-content/60">
            Step {step + 1} of {SETUP_STEPS.length}
          </span>
        </div>
        <progress
          className="progress progress-primary w-full"
          value={step + 1}
          max={SETUP_STEPS.length}
          aria-label={`Setup step ${step + 1} of ${SETUP_STEPS.length}`}
        />
      </div>
    </>
  );
}

export function LibraryTypeStep({
  draft,
  managedEnv,
  updateDraft,
}: {
  draft: SetupDraft;
  managedEnv: ManagedEnvMap;
  updateDraft: UpdateDraft;
}) {
  const strategy = normalizeStrategy(draft.config["api.import-strategy"]);
  return (
    <StepSection
      title="How will your media library consume InfiniDysk?"
      description="Choose the import shape used for future downloads. You can revisit this guide later."
    >
      <ManagedSetting configKey="api.import-strategy">
        <Field className="gap-3">
          <legend className="fieldset-legend">Library type</legend>
          <RadioJoinFilter
            name="setup-library-strategy"
            aria-label="Library type"
            value={strategy}
            prominent
            options={
              [
                {
                  id: "symlinks",
                  label: "Symlinks · Plex",
                  description: "Use an rclone mount and filesystem entries.",
                  icon: "link",
                },
                {
                  id: "strm",
                  label: "STRM · Emby/Jellyfin",
                  description: "Use small files that open direct playback URLs.",
                  icon: "play_circle",
                },
              ] as const
            }
            onChange={(value) =>
              updateDraft((current) => ({
                ...current,
                config: applyStrategy(current.config, value, managedEnv),
                vfsReadAheadConfirmed: value === "strm" ? false : current.vfsReadAheadConfirmed,
              }))
            }
          />
        </Field>
      </ManagedSetting>
      <Alert variant="info" className="alert-soft items-start text-sm">
        <Icon name={strategy === "symlinks" ? "link" : "play_circle"} className="!text-[20px]" />
        <div>
          <p className="font-semibold">
            {strategy === "symlinks" ? "Filesystem-shaped library" : "URL-shaped library"}
          </p>
          <p className="mt-1 text-xs leading-relaxed opacity-80">
            {strategy === "symlinks"
              ? "Plex follows symlinks through an rclone WebDAV mount. Rclone VFS caching handles read-ahead, so Segment Cache will be disabled."
              : "Emby or Jellyfin opens generated .strm URLs directly. Segment Cache will be enabled for repeated WebDAV reads and seeks."}
          </p>
        </div>
      </Alert>
    </StepSection>
  );
}

export function PlaybackStep({
  draft,
  updateDraft,
}: {
  draft: SetupDraft;
  updateDraft: UpdateDraft;
}) {
  return normalizeStrategy(draft.config["api.import-strategy"]) === "symlinks" ? (
    <SymlinkPlaybackStep draft={draft} updateDraft={updateDraft} />
  ) : (
    <StrmPlaybackStep draft={draft} updateDraft={updateDraft} />
  );
}

function SymlinkPlaybackStep({
  draft,
  updateDraft,
}: {
  draft: SetupDraft;
  updateDraft: UpdateDraft;
}) {
  const [testState, setTestState] = useState<"idle" | "testing" | "success" | "error">("idle");
  const [testMessage, setTestMessage] = useState("");
  const [testWarning, setTestWarning] = useState(false);
  const [copied, setCopied] = useState(false);
  const config = draft.config;
  const rcEnabled = config["rclone.rc-enabled"] === "true";
  const rcloneHost = config["rclone.host"];
  const rcloneUser = config["rclone.user"];
  const rclonePass = config["rclone.pass"];
  const sidecarFlags = `--allow-other
--poll-interval=0
--dir-cache-time=1w
--allow-non-empty
--vfs-cache-mode=full
--buffer-size=0
--vfs-read-chunk-streams=1
--vfs-read-chunk-size=1M
--vfs-read-chunk-size-limit=512M
--vfs-read-ahead=512M
--vfs-cache-max-size=50G
--vfs-cache-max-age=1w
--links
--use-cookies
--rc
--rc-addr=:5572
--rc-no-auth`;

  useEffect(() => {
    setTestState("idle");
    setTestMessage("");
    setTestWarning(false);
  }, [rcloneHost, rcloneUser, rclonePass]);

  const testConnection = useCallback(async () => {
    setTestState("testing");
    setTestMessage("");
    try {
      const form = new FormData();
      form.append("host", config["rclone.host"] ?? "");
      form.append("user", config["rclone.user"] ?? "");
      form.append("pass", config["rclone.pass"] ?? "");
      const response = await fetch(withUrlBase("/api/test-rclone-connection"), {
        method: "POST",
        body: form,
      });
      const result = (await response.json()) as {
        connected?: boolean;
        error?: string;
        readAheadBytes?: number | null;
        cacheMode?: string | null;
        vfsInspectionError?: string | null;
      };
      if (response.ok && result.connected) {
        setTestState("success");
        const details = [
          result.cacheMode ? `cache mode ${result.cacheMode}` : null,
          result.readAheadBytes != null ? `read-ahead ${formatBytes(result.readAheadBytes)}` : null,
        ].filter(Boolean);
        const readAheadConfirmed = (result.readAheadBytes ?? 0) > 0;
        setTestWarning(!readAheadConfirmed);
        setTestMessage(
          readAheadConfirmed
            ? `Connected · ${details.join(" · ")}`
            : result.vfsInspectionError
              ? `Connected, but VFS options could not be inspected: ${result.vfsInspectionError}`
              : "Connected, but VFS read-ahead is not enabled. Update the sidecar flags and restart rclone.",
        );
        if ((result.readAheadBytes ?? 0) > 0) {
          updateDraft((current) => ({ ...current, vfsReadAheadConfirmed: true }));
        }
      } else {
        setTestState("error");
        setTestWarning(false);
        setTestMessage(result.error || "Connection test failed.");
      }
    } catch (error) {
      setTestState("error");
      setTestWarning(false);
      setTestMessage(error instanceof Error ? error.message : "Connection test failed.");
    }
  }, [config, updateDraft]);

  return (
    <StepSection
      title="Prepare the rclone playback path"
      description="InfiniDysk will use rclone's bounded VFS cache for Plex playback and keep its own Segment Cache off."
    >
      <Alert variant="success" className="alert-soft items-start text-sm">
        <Icon name="check_circle" className="!text-[20px]" />
        <span>Segment Cache will be disabled when you apply this Symlinks setup.</span>
      </Alert>

      <ManagedSetting configKey="rclone.mount-dir">
        <Field>
          <Label htmlFor="setup-rclone-mount">Rclone mount directory</Label>
          <Input
            id="setup-rclone-mount"
            className="validator w-full"
            required
            placeholder="/mnt/nzbdav"
            value={config["rclone.mount-dir"] ?? ""}
            onChange={(event) => updateConfig(updateDraft, "rclone.mount-dir", event.target.value)}
          />
          <p className="validator-hint">The mounted WebDAV root is required for symlink imports.</p>
          <HelpText>Use the same absolute path inside InfiniDysk, Radarr, and Sonarr.</HelpText>
        </Field>
      </ManagedSetting>

      <details
        open
        className="collapse collapse-arrow border border-base-content/10 bg-base-200/40"
      >
        <summary className="collapse-title text-sm font-semibold">
          Rclone sidecar configuration
        </summary>
        <div className="collapse-content space-y-3">
          <p className="text-xs leading-relaxed text-base-content/65">
            Add these flags to the mount command. Adjust the 50 GiB limit to fit the storage
            available to the sidecar.
          </p>
          <div className="mockup-code text-xs">
            {sidecarFlags.split("\n").map((line) => (
              <pre key={line} data-prefix="">
                <code>{line}</code>
              </pre>
            ))}
          </div>
          <Tooltip content={copied ? "Copied" : "Copy rclone flags"}>
            <Button
              variant="ghost"
              size="small"
              onClick={() => {
                void navigator.clipboard.writeText(sidecarFlags).then(() => setCopied(true));
              }}
            >
              <Icon name="content_copy" className="!text-[18px]" />
              Copy flags
            </Button>
          </Tooltip>
          <Alert variant="warning" className="alert-soft items-start text-xs">
            <Icon name="security" className="!text-[18px]" />
            <span>
              <code>--rc-no-auth</code> is suitable only on an isolated trusted container network.
              For authentication, replace it with <code>--rc-user</code> and <code>--rc-pass</code>,
              then enter the same values below. A separate sidecar must bind <code>:5572</code>; use{" "}
              <code>127.0.0.1:5572</code> only when rclone and InfiniDysk share one network
              namespace.
            </span>
          </Alert>
        </div>
      </details>

      <ManagedSetting configKey="rclone.rc-enabled">
        <Toggle
          id="setup-rclone-rc-enabled"
          checked={rcEnabled}
          onChange={(event) =>
            updateConfig(updateDraft, "rclone.rc-enabled", String(event.target.checked))
          }
          label={<span>Enable rclone RC notifications</span>}
        />
      </ManagedSetting>

      <fieldset className="fieldset grid grid-cols-1 gap-4 lg:grid-cols-2" disabled={!rcEnabled}>
        <legend className="fieldset-legend lg:col-span-2">RC server connection</legend>
        <ManagedSetting configKey="rclone.host" className="lg:col-span-2">
          <Field>
            <Label htmlFor="setup-rclone-host">Rclone RC host</Label>
            <div className="join w-full">
              <Input
                id="setup-rclone-host"
                className="join-item validator min-w-0 flex-1"
                type="url"
                required={rcEnabled}
                placeholder="http://nzbdav_rclone:5572"
                value={config["rclone.host"] ?? ""}
                onChange={(event) => updateConfig(updateDraft, "rclone.host", event.target.value)}
              />
              <Button
                className="join-item shrink-0"
                onClick={() => void testConnection()}
                disabled={!rcEnabled || testState === "testing" || !config["rclone.host"]?.trim()}
              >
                {testState === "testing" ? (
                  <Spinner size="sm" />
                ) : (
                  <Icon name="cable" className="!text-[18px]" />
                )}
                Test
              </Button>
            </div>
            <p className="validator-hint">Enter an absolute URL for the sidecar RC endpoint.</p>
          </Field>
        </ManagedSetting>
        <ManagedSetting configKey="rclone.user">
          <Field>
            <Label htmlFor="setup-rclone-user">Username (optional)</Label>
            <Input
              id="setup-rclone-user"
              className="w-full"
              autoComplete="username"
              value={config["rclone.user"] ?? ""}
              onChange={(event) => updateConfig(updateDraft, "rclone.user", event.target.value)}
            />
          </Field>
        </ManagedSetting>
        <ManagedSetting configKey="rclone.pass">
          <Field>
            <Label htmlFor="setup-rclone-pass">Password (optional)</Label>
            <Input
              id="setup-rclone-pass"
              className="w-full"
              type="password"
              autoComplete="current-password"
              value={config["rclone.pass"] ?? ""}
              onChange={(event) => updateConfig(updateDraft, "rclone.pass", event.target.value)}
            />
          </Field>
        </ManagedSetting>
      </fieldset>

      {testState !== "idle" && testState !== "testing" && (
        <Alert
          variant={testState === "error" ? "danger" : testWarning ? "warning" : "success"}
          className="alert-soft text-sm"
        >
          <span
            className={`status ${testState === "error" ? "status-error" : testWarning ? "status-warning" : "status-success"}`}
            aria-hidden="true"
          />
          <span>{testMessage}</span>
        </Alert>
      )}

      <Check
        id="setup-vfs-confirmed"
        checked={draft.vfsReadAheadConfirmed}
        onChange={(event) =>
          updateDraft((current) => ({ ...current, vfsReadAheadConfirmed: event.target.checked }))
        }
        label="I verified that this mount has VFS read-ahead enabled."
      />
    </StepSection>
  );
}

function StrmPlaybackStep({ draft, updateDraft }: { draft: SetupDraft; updateDraft: UpdateDraft }) {
  return (
    <StepSection
      title="Configure direct STRM playback"
      description="Generated .strm files point Emby or Jellyfin back to InfiniDysk over HTTP."
    >
      <Alert variant="success" className="alert-soft items-start text-sm">
        <Icon name="cached" className="!text-[20px]" />
        <div>
          <p className="font-semibold">Segment Cache will be enabled</p>
          <p className="mt-1 text-xs opacity-80">
            It is the local WebDAV cache layer for repeated reads and seeks. A restart is required
            when this setting changes.
          </p>
        </div>
      </Alert>
      <ManagedSetting configKey="api.completed-downloads-dir">
        <Field>
          <Label htmlFor="setup-completed-dir">Completed downloads directory</Label>
          <Input
            id="setup-completed-dir"
            className="validator w-full"
            required
            placeholder="/data/completed-downloads"
            value={draft.config["api.completed-downloads-dir"] ?? ""}
            onChange={(event) =>
              updateConfig(updateDraft, "api.completed-downloads-dir", event.target.value)
            }
          />
          <p className="validator-hint">A shared directory is required for generated STRM files.</p>
          <HelpText>Map this exact path into Radarr or Sonarr.</HelpText>
        </Field>
      </ManagedSetting>
      <ManagedSetting configKey="general.base-url">
        <Field>
          <Label htmlFor="setup-base-url">Base URL</Label>
          <Input
            id="setup-base-url"
            className="validator w-full"
            type="url"
            required
            placeholder="http://infinidysk:3000"
            value={draft.config["general.base-url"] ?? ""}
            onChange={(event) => updateConfig(updateDraft, "general.base-url", event.target.value)}
          />
          <p className="validator-hint">Enter an absolute URL reachable by Emby or Jellyfin.</p>
          <HelpText>Generated STRM files use this address for playback.</HelpText>
        </Field>
      </ManagedSetting>
    </StepSection>
  );
}

export function IngestionStep({
  draft,
  updateDraft,
}: {
  draft: SetupDraft;
  updateDraft: UpdateDraft;
}) {
  const arrConfig = useMemo(() => parseArrConfig(draft.config["arr.instances"]), [draft.config]);
  const toggleMethod = (method: SetupWizardIngestionMethod, checked: boolean) => {
    updateDraft((current) => ({
      ...current,
      ingestionMethods: checked
        ? [...new Set([...current.ingestionMethods, method])]
        : current.ingestionMethods.filter((value) => value !== method),
    }));
  };

  const updateArrConfig = (next: ArrConfig) => {
    updateConfig(updateDraft, "arr.instances", serializeArrConfig(next));
  };

  return (
    <StepSection
      title="How will content reach InfiniDysk?"
      description="Select every workflow you plan to use. You can configure advanced search behavior later."
    >
      <fieldset className="fieldset">
        <legend className="fieldset-legend">Ingestion methods</legend>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
          <MethodChoice
            id="setup-ingestion-arrs"
            icon="sync_alt"
            title="Arr apps"
            detail="Radarr and Sonarr send NZBs automatically."
            checked={draft.ingestionMethods.includes("arrs")}
            onChange={(checked) => toggleMethod("arrs", checked)}
          />
          <MethodChoice
            id="setup-ingestion-search"
            icon="search"
            title="Built-in Search"
            detail="Search configured indexers from InfiniDysk."
            checked={draft.ingestionMethods.includes("search")}
            onChange={(checked) => toggleMethod("search", checked)}
          />
          <MethodChoice
            id="setup-ingestion-manual"
            icon="upload_file"
            title="Manual NZB"
            detail="Upload NZB files from the Queue page."
            checked={draft.ingestionMethods.includes("manual")}
            onChange={(checked) => toggleMethod("manual", checked)}
          />
        </div>
      </fieldset>

      {draft.ingestionMethods.includes("arrs") && (
        <ManagedSetting configKey="arr.instances">
          <ArrInstancesEditor config={arrConfig} onChange={updateArrConfig} />
        </ManagedSetting>
      )}

      {draft.ingestionMethods.includes("arrs") && (
        <ArrDownloadClientInstructions
          apiKey={draft.config["api.key"] ?? ""}
          categories={draft.config["api.categories"] ?? "movies,tv"}
          strategy={normalizeStrategy(draft.config["api.import-strategy"])}
          completedPath={
            normalizeStrategy(draft.config["api.import-strategy"]) === "symlinks"
              ? (draft.config["rclone.mount-dir"] ?? "/mnt/nzbdav")
              : (draft.config["api.completed-downloads-dir"] ?? "/data/completed-downloads")
          }
        />
      )}

      {draft.ingestionMethods.includes("search") && (
        <Alert variant="info" className="alert-soft text-sm">
          <Icon name="travel_explore" className="!text-[20px]" />
          <span>After setup, add Newznab indexers and Search Profiles under Settings.</span>
        </Alert>
      )}
      {draft.ingestionMethods.includes("manual") && (
        <Alert variant="info" className="alert-soft text-sm">
          <Icon name="list_alt" className="!text-[20px]" />
          <span>After setup, use Queue to upload a small NZB and verify playback.</span>
        </Alert>
      )}
    </StepSection>
  );
}

function MethodChoice({
  id,
  icon,
  title,
  detail,
  checked,
  onChange,
}: {
  id: string;
  icon: string;
  title: string;
  detail: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label
      htmlFor={id}
      className={`flex min-w-0 cursor-pointer items-start gap-3 rounded-box border p-4 ${
        checked ? "border-primary/60 bg-primary/10" : "border-base-content/10 bg-base-200/30"
      }`}
    >
      <input
        id={id}
        type="checkbox"
        className="checkbox checkbox-primary mt-0.5"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
      />
      <Icon name={icon} className="!text-[20px] shrink-0 text-base-content/70" />
      <span className="min-w-0">
        <span className="block text-sm font-semibold">{title}</span>
        <span className="mt-1 block text-xs leading-relaxed text-base-content/55">{detail}</span>
      </span>
    </label>
  );
}

function ArrInstancesEditor({
  config,
  onChange,
}: {
  config: ArrConfig;
  onChange: (config: ArrConfig) => void;
}) {
  return (
    <section className="space-y-4 border-t border-base-content/10 pt-5">
      <div>
        <h3 className="text-lg font-semibold">Register Radarr and Sonarr</h3>
        <p className="mt-1 text-sm text-base-content/60">
          These connections enable import monitoring, queue actions, and linked-library repairs.
        </p>
      </div>
      <div className="grid grid-cols-1 gap-5 xl:grid-cols-2">
        <ArrKindEditor
          kind="Radarr"
          instances={config.RadarrInstances}
          onChange={(instances) => onChange({ ...config, RadarrInstances: instances })}
        />
        <ArrKindEditor
          kind="Sonarr"
          instances={config.SonarrInstances}
          onChange={(instances) => onChange({ ...config, SonarrInstances: instances })}
        />
      </div>
    </section>
  );
}

function ArrKindEditor({
  kind,
  instances,
  onChange,
}: {
  kind: "Radarr" | "Sonarr";
  instances: ArrInstance[];
  onChange: (instances: ArrInstance[]) => void;
}) {
  return (
    <section className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <h4 className="font-semibold">{kind}</h4>
        <Button onClick={() => onChange([...instances, { Host: "", ApiKey: "", Enabled: true }])}>
          <Icon name="add" className="!text-[18px]" />
          Add
        </Button>
      </div>
      {instances.length === 0 ? (
        <p className="rounded-box border border-dashed border-base-content/20 p-4 text-sm text-base-content/55">
          No {kind} instances configured.
        </p>
      ) : (
        instances.map((instance, index) => (
          <ArrInstanceEditor
            key={`${kind}-${index}`}
            kind={kind}
            index={index}
            instance={instance}
            onChange={(next) =>
              onChange(instances.map((item, itemIndex) => (itemIndex === index ? next : item)))
            }
            onRemove={() => onChange(instances.filter((_, itemIndex) => itemIndex !== index))}
          />
        ))
      )}
    </section>
  );
}

function ArrInstanceEditor({
  kind,
  index,
  instance,
  onChange,
  onRemove,
}: {
  kind: "Radarr" | "Sonarr";
  index: number;
  instance: ArrInstance;
  onChange: (instance: ArrInstance) => void;
  onRemove: () => void;
}) {
  const [testState, setTestState] = useState<"idle" | "testing" | "success" | "error">("idle");
  const [testMessage, setTestMessage] = useState("");
  const prefix = `setup-${kind.toLowerCase()}-${index}`;

  useEffect(() => {
    setTestState("idle");
    setTestMessage("");
  }, [instance.Host, instance.ApiKey]);

  const testConnection = async () => {
    setTestState("testing");
    try {
      const form = new FormData();
      form.append("host", instance.Host);
      form.append("apiKey", instance.ApiKey);
      const response = await fetch(withUrlBase("/api/test-arr-connection"), {
        method: "POST",
        body: form,
      });
      const result = (await response.json()) as { connected?: boolean; error?: string };
      setTestState(response.ok && result.connected ? "success" : "error");
      setTestMessage(
        response.ok && result.connected
          ? "Connection successful"
          : result.error || "Connection test failed",
      );
    } catch (error) {
      setTestState("error");
      setTestMessage(error instanceof Error ? error.message : "Connection test failed");
    }
  };

  return (
    <fieldset className="fieldset rounded-box border border-base-content/10 bg-base-200/30 p-4">
      <legend className="fieldset-legend flex w-full items-center justify-between gap-3">
        <span>
          {kind} {index + 1}
        </span>
        <Tooltip content={`Remove ${kind} instance`}>
          <Button
            size="rounded"
            variant="ghost"
            aria-label={`Remove ${kind} instance`}
            onClick={onRemove}
          >
            <Icon name="close" className="!text-[18px]" />
          </Button>
        </Tooltip>
      </legend>
      <Toggle
        id={`${prefix}-enabled`}
        checked={instance.Enabled !== false}
        onChange={(event) => onChange({ ...instance, Enabled: event.target.checked })}
        label={<span>Enabled</span>}
      />
      <Label htmlFor={`${prefix}-name`}>Name (optional)</Label>
      <Input
        id={`${prefix}-name`}
        className="w-full"
        value={instance.Name ?? ""}
        onChange={(event) => onChange({ ...instance, Name: event.target.value })}
      />
      <Label htmlFor={`${prefix}-host`}>Host</Label>
      <div className="join w-full">
        <Input
          id={`${prefix}-host`}
          className="join-item min-w-0 flex-1"
          type="url"
          placeholder={kind === "Radarr" ? "http://radarr:7878" : "http://sonarr:8989"}
          value={instance.Host}
          onChange={(event) => onChange({ ...instance, Host: event.target.value })}
        />
        <Button
          className="join-item shrink-0"
          disabled={!instance.Host.trim() || !instance.ApiKey.trim() || testState === "testing"}
          onClick={() => void testConnection()}
        >
          {testState === "testing" ? (
            <Spinner size="sm" />
          ) : (
            <Icon name="cable" className="!text-[18px]" />
          )}
          Test
        </Button>
      </div>
      <Label htmlFor={`${prefix}-key`}>API key</Label>
      <Input
        id={`${prefix}-key`}
        className="w-full"
        type="password"
        value={instance.ApiKey}
        onChange={(event) => onChange({ ...instance, ApiKey: event.target.value })}
      />
      {testState !== "idle" && testState !== "testing" && (
        <p
          className={`flex items-center gap-2 text-xs ${testState === "success" ? "text-success" : "text-error"}`}
        >
          <span
            className={`status status-sm ${testState === "success" ? "status-success" : "status-error"}`}
            aria-hidden="true"
          />
          {testMessage}
        </p>
      )}
    </fieldset>
  );
}

function ArrDownloadClientInstructions({
  apiKey,
  categories,
  strategy,
  completedPath,
}: {
  apiKey: string;
  categories: string;
  strategy: SetupWizardStrategy;
  completedPath: string;
}) {
  const [copied, setCopied] = useState(false);
  return (
    <details className="collapse collapse-arrow border border-base-content/10 bg-base-200/40">
      <summary className="collapse-title text-sm font-semibold">
        Add InfiniDysk to each Arr app
      </summary>
      <div className="collapse-content space-y-4 text-sm">
        <ol className="list-decimal space-y-2 pl-5 text-base-content/70">
          <li>Open Settings → Download Clients → Add → SABnzbd.</li>
          <li>Use a hostname and port 3000 that the Arr container can reach.</li>
          <li>Paste the API key below and use matching categories.</li>
          <li>Test the client, then ensure the completed path is mounted identically.</li>
        </ol>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <div>
            <p className="text-xs font-medium text-base-content/55">Categories</p>
            <code className="mt-1 block break-all rounded-field bg-base-300 p-2 text-xs">
              {categories}
            </code>
          </div>
          <div>
            <p className="text-xs font-medium text-base-content/55">Completed path</p>
            <code className="mt-1 block break-all rounded-field bg-base-300 p-2 text-xs">
              {completedPath}
            </code>
          </div>
        </div>
        <div>
          <Label htmlFor="setup-sab-api-key">InfiniDysk API key</Label>
          <div className="join w-full">
            <Input
              id="setup-sab-api-key"
              className="join-item min-w-0 flex-1 font-mono"
              readOnly
              value={apiKey}
            />
            <Tooltip content={copied ? "Copied" : "Copy API key"}>
              <Button
                className="join-item"
                aria-label="Copy API key"
                onClick={() =>
                  void navigator.clipboard.writeText(apiKey).then(() => setCopied(true))
                }
              >
                <Icon name="content_copy" className="!text-[18px]" />
              </Button>
            </Tooltip>
          </div>
        </div>
        <Badge className="badge-soft badge-info">
          {strategy === "symlinks" ? "Symlink path" : "STRM path"}
        </Badge>
      </div>
    </details>
  );
}

export function BackupStep({
  draft,
  updateDraft,
  mainDatabaseProvider,
}: {
  draft: SetupDraft;
  updateDraft: UpdateDraft;
  mainDatabaseProvider: "sqlite" | "postgres";
}) {
  const enabled = draft.config["backup.schedule-enabled"] === "true";
  return (
    <StepSection
      title="Protect your configuration"
      description="InfiniDysk can create a logical backup once per day and retain a bounded number of snapshots."
    >
      {mainDatabaseProvider === "postgres" && (
        <Alert variant="warning" className="alert-soft items-start text-sm">
          <Icon name="info" className="!text-[20px]" />
          <span>
            This schedule protects local auxiliary SQLite stores only. Back up the main PostgreSQL
            database with your provider's tooling.
          </span>
        </Alert>
      )}
      <ManagedSetting configKey="backup.schedule-enabled">
        <Toggle
          id="setup-backup-enabled"
          checked={enabled}
          onChange={(event) =>
            updateConfig(updateDraft, "backup.schedule-enabled", String(event.target.checked))
          }
          label={<span>Enable daily scheduled backups</span>}
        />
      </ManagedSetting>
      <fieldset className="fieldset grid grid-cols-1 gap-4 sm:grid-cols-2" disabled={!enabled}>
        <legend className="fieldset-legend sm:col-span-2">Backup schedule</legend>
        <ManagedSetting configKey="backup.schedule-time">
          <Field>
            <Label htmlFor="setup-backup-time">Daily run time</Label>
            <Input
              id="setup-backup-time"
              className="w-full"
              type="time"
              value={timeFromMinutes(draft.config["backup.schedule-time"])}
              onChange={(event) =>
                updateConfig(
                  updateDraft,
                  "backup.schedule-time",
                  minutesFromTime(event.target.value),
                )
              }
            />
          </Field>
        </ManagedSetting>
        <ManagedSetting configKey="backup.retention-count">
          <Field>
            <Label htmlFor="setup-backup-retention">Backups to retain</Label>
            <Input
              id="setup-backup-retention"
              className="validator w-full"
              type="number"
              min={1}
              required={enabled}
              value={draft.config["backup.retention-count"] ?? "5"}
              onChange={(event) =>
                updateConfig(updateDraft, "backup.retention-count", event.target.value)
              }
            />
            <p className="validator-hint">Retain at least one backup.</p>
          </Field>
        </ManagedSetting>
      </fieldset>
    </StepSection>
  );
}

export function LibraryDirectoryStep({
  draft,
  updateDraft,
}: {
  draft: SetupDraft;
  updateDraft: UpdateDraft;
}) {
  return (
    <StepSection
      title="Where is your organized media library?"
      description="Health & Repairs uses this root to find imported symlinks or STRM files and coordinate replacements."
    >
      <ManagedSetting configKey="media.library-dir">
        <Field>
          <Label htmlFor="setup-library-dir">Library Directory</Label>
          <Input
            id="setup-library-dir"
            className="w-full"
            placeholder="/mnt/media"
            value={draft.config["media.library-dir"] ?? ""}
            onChange={(event) => updateConfig(updateDraft, "media.library-dir", event.target.value)}
          />
          <HelpText>
            Use the parent of your Radarr and Sonarr root folders, visible inside the InfiniDysk
            container. Do not use the rclone mount.
          </HelpText>
        </Field>
      </ManagedSetting>
      {!draft.config["media.library-dir"]?.trim() && (
        <Alert variant="warning" className="alert-soft text-sm">
          <Icon name="schedule" className="!text-[20px]" />
          <span>
            Set up later is allowed. Core health checks and PAR2 repair still work, but
            linked-library replacement will be limited.
          </span>
        </Alert>
      )}
    </StepSection>
  );
}

export function ReviewStep({
  baseline,
  draft,
  changes,
  managedEnv,
  strategyChangeConfirmed,
  setStrategyChangeConfirmed,
}: {
  baseline: Record<string, string>;
  draft: SetupDraft;
  changes: Record<string, string>;
  managedEnv: ManagedEnvMap;
  strategyChangeConfirmed: boolean;
  setStrategyChangeConfirmed: (confirmed: boolean) => void;
}) {
  const strategy = normalizeStrategy(draft.config["api.import-strategy"]);
  const baselineStrategy = normalizeStrategy(baseline["api.import-strategy"]);
  const rows = Object.entries(changes);
  return (
    <StepSection
      title="Review and apply"
      description="Configuration changes are applied together. Secrets remain hidden and existing imported files are not rewritten."
    >
      <div className="flex flex-wrap gap-2">
        <Badge className="badge-soft badge-primary">
          {strategy === "symlinks" ? "Symlinks · Plex" : "STRM · Emby/Jellyfin"}
        </Badge>
        {baseline["usenet.segment-cache.enabled"] !==
          draft.config["usenet.segment-cache.enabled"] && (
          <Badge className="badge-soft badge-warning">Restart required</Badge>
        )}
        {Object.keys(managedEnv).length > 0 && (
          <Badge className="badge-soft">Environment-managed settings preserved</Badge>
        )}
      </div>

      <section className="space-y-2">
        <h3 className="text-sm font-semibold text-base-content">Content ingestion</h3>
        <div className="flex flex-wrap gap-2">
          {draft.ingestionMethods.map((method) => (
            <Badge key={method} className="badge-outline">
              {method === "arrs"
                ? "Arr apps"
                : method === "search"
                  ? "Built-in Search"
                  : "Manual NZB"}
            </Badge>
          ))}
        </div>
      </section>

      {rows.length === 0 ? (
        <Alert variant="info" className="alert-soft text-sm">
          No persisted settings will change. Completing still records this setup guide as reviewed.
        </Alert>
      ) : (
        <div className="overflow-x-auto rounded-box border border-base-content/10">
          <table className="table table-sm">
            <thead>
              <tr>
                <th>Setting</th>
                <th>Current</th>
                <th>New</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(([key, value]) => (
                <tr key={key}>
                  <th className="whitespace-nowrap font-medium">{settingLabel(key)}</th>
                  <td>{displayConfigValue(key, baseline[key] ?? "")}</td>
                  <td>{displayConfigValue(key, value)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {baselineStrategy !== strategy && (
        <Alert variant="warning" className="alert-soft items-start text-sm">
          <Icon name="warning" className="!text-[20px]" />
          <div className="space-y-3">
            <p>
              Changing import strategy affects future imports only. Existing files require a
              Maintenance conversion or deliberate migration.
            </p>
            <Check
              id="setup-strategy-change-confirmed"
              checked={strategyChangeConfirmed}
              onChange={(event) => setStrategyChangeConfirmed(event.target.checked)}
              label="I understand existing imported files are not converted automatically."
            />
          </div>
        </Alert>
      )}

      {!draft.config["media.library-dir"]?.trim() && (
        <Alert variant="warning" className="alert-soft text-sm">
          Library Directory will remain unset.
        </Alert>
      )}
    </StepSection>
  );
}

export function StepSection({
  title,
  description,
  children,
}: {
  title: string;
  description: string;
  children: React.ReactNode;
}) {
  return (
    <section className="space-y-6">
      <div className="max-w-3xl">
        <h2 className="text-2xl font-bold text-base-content" tabIndex={-1} data-setup-heading>
          {title}
        </h2>
        <p className="mt-2 max-w-[70ch] text-sm leading-relaxed text-base-content/60">
          {description}
        </p>
      </div>
      <div className="space-y-5">{children}</div>
    </section>
  );
}

function updateConfig(updateDraft: UpdateDraft, key: string, value: string) {
  updateDraft((current) => ({
    ...current,
    config: { ...current.config, [key]: value },
  }));
}

function displayConfigValue(key: string, value: string): string {
  if (key === "rclone.pass") return value ? "Stored securely" : "Not set";
  if (key === "arr.instances") {
    const config = parseArrConfig(value);
    const count = config.RadarrInstances.length + config.SonarrInstances.length;
    return `${count} Arr instance${count === 1 ? "" : "s"}`;
  }
  if (value === "true") return "Enabled";
  if (value === "false") return "Disabled";
  return value || "Not set";
}

function settingLabel(key: string): string {
  const labels: Record<string, string> = {
    "api.import-strategy": "Import strategy",
    "usenet.segment-cache.enabled": "Segment Cache",
    "rclone.mount-dir": "Rclone mount directory",
    "rclone.rc-enabled": "RC notifications",
    "rclone.host": "Rclone RC host",
    "rclone.user": "Rclone RC user",
    "rclone.pass": "Rclone RC password",
    "api.completed-downloads-dir": "Completed downloads directory",
    "general.base-url": "Base URL",
    "arr.instances": "Arr connections",
    "backup.schedule-enabled": "Scheduled backups",
    "backup.schedule-time": "Backup time",
    "backup.retention-count": "Backup retention",
    "media.library-dir": "Library Directory",
  };
  return labels[key] ?? key;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KiB`;
  if (bytes < 1024 * 1024 * 1024) return `${Math.round(bytes / (1024 * 1024))} MiB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GiB`;
}
