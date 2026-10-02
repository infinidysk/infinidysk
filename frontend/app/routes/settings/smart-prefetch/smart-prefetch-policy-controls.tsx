import { useEffect, useRef } from "react";
import { Badge, Button, Input, InputGroup, Toggle } from "~/components/ui";
import {
  bytesToDecimalGb,
  decimalGbToBytes,
  hasCustomizedPrefetchPolicy,
  numericFields,
  resetPrefetchPolicyDefaults,
  type NumericKey,
  type PrefetchSettings,
} from "./smart-prefetch-model";

type BooleanKey = keyof Pick<
  PrefetchSettings,
  | "HistoryEnabled"
  | "RealtimeEnabled"
  | "ReadActivityEnabled"
  | "PredictionsEnabled"
  | "MinimumWarmEnabled"
  | "FullFileWarming"
  | "WarmLocalFiles"
  | "PauseDuringPlayback"
>;

const booleanGroups: { title: string; controls: { key: BooleanKey; label: string }[] }[] = [
  {
    title: "Signals",
    controls: [
      { key: "HistoryEnabled", label: "Use Plex watch history" },
      { key: "RealtimeEnabled", label: "Use verified Plex playback" },
      { key: "ReadActivityEnabled", label: "Use raw read-activity signals" },
      { key: "WarmLocalFiles", label: "Allow mapped local library files" },
    ],
  },
  {
    title: "Predictions",
    controls: [{ key: "PredictionsEnabled", label: "Predict upcoming episodes" }],
  },
  {
    title: "Warming",
    controls: [
      { key: "MinimumWarmEnabled", label: "Warm minimum head and tail ranges" },
      { key: "FullFileWarming", label: "Warm full files" },
      { key: "PauseDuringPlayback", label: "Pause background warming during playback" },
    ],
  },
];

const numericGroups: { title: string; keys: NumericKey[] }[] = [
  {
    title: "Predictions",
    keys: [
      "ConfidenceThreshold",
      "CooldownMinutes",
      "LookbackDays",
      "MoviePartialWatchLookbackDays",
      "MinEpisodesForPrediction",
      "MaxQueueAhead",
      "TvEpisodesPerShow",
    ],
  },
  {
    title: "Scheduling",
    keys: [
      "SyncIntervalMinutes",
      "RealtimeCheckIntervalSeconds",
      "MovieSyncIntervalMinutes",
      "TvSyncIntervalMinutes",
      "VerifiedSessionExpirySeconds",
      "IntentTtlHours",
    ],
  },
  {
    title: "Queue and resources",
    keys: [
      "QueueCapacity",
      "MaxRetries",
      "MaxConcurrentJobs",
      "ConnectionsPerJob",
      "MaxBytesPerItem",
    ],
  },
  {
    title: "Warming",
    keys: ["MinimumHeadMb", "MinimumTailMb"],
  },
];

export function SmartPrefetchPolicyControls({
  settings,
  error,
  onChange,
}: {
  settings: PrefetchSettings;
  error: string | null;
  onChange: (settings: PrefetchSettings) => void;
}) {
  const details = useRef<HTMLDetailsElement>(null);
  const customized = hasCustomizedPrefetchPolicy(settings);
  useEffect(() => {
    if (error && details.current) details.current.open = true;
  }, [error]);

  return (
    <div className="space-y-5">
      <div className="grid gap-3 md:grid-cols-2">
        <Toggle
          label="Enable Smart Prefetch"
          checked={settings.Enabled}
          onChange={(event) => onChange({ ...settings, Enabled: event.target.checked })}
        />
        <Toggle
          label="Movies"
          checked={settings.MovieEnabled}
          onChange={(event) => onChange({ ...settings, MovieEnabled: event.target.checked })}
        />
        <Toggle
          label="TV episodes"
          checked={settings.TvEnabled}
          onChange={(event) => onChange({ ...settings, TvEnabled: event.target.checked })}
        />
        <label>
          Daily download budget (GB/day)
          <InputGroup
            aria-label="Daily download budget (GB/day)"
            type="number"
            min={0}
            max={100_000}
            step={0.1}
            suffix="GB/day"
            value={bytesToDecimalGb(settings.DailyByteBudget)}
            onChange={(event) =>
              onChange({
                ...settings,
                DailyByteBudget: decimalGbToBytes(Number(event.target.value)),
              })
            }
          />
          <small className="block text-xs text-base-content/50">
            0 means unlimited. Provider attempts count; cache hits do not.
          </small>
        </label>
      </div>

      <div className="rounded-box border border-base-content/10 bg-base-200/40 p-4">
        <strong>{customized ? "Customized policy" : "Smart defaults are on"}</strong>
        <p className="mt-1 text-sm text-base-content/60">
          Verified playback, next episodes, whole files, pause during playback, and conservative
          concurrency.
        </p>
      </div>

      <details
        ref={details}
        className="collapse collapse-arrow border border-base-content/10 bg-base-200/40"
      >
        <summary className="collapse-title flex items-center gap-2 text-sm font-semibold">
          <span>Advanced settings</span>
          {customized && <Badge className="badge-warning badge-soft badge-sm">Customized</Badge>}
        </summary>
        <div className="collapse-content space-y-5">
          <div className="flex justify-end">
            <Button
              type="button"
              variant="ghost"
              onClick={() => onChange(resetPrefetchPolicyDefaults(settings))}
            >
              Reset to smart defaults
            </Button>
          </div>
          {booleanGroups.map((group) => (
            <section key={group.title} className="space-y-2">
              <h3 className="text-sm font-semibold">{group.title}</h3>
              <div className="grid gap-3 md:grid-cols-2">
                {group.controls.map((control) => (
                  <Toggle
                    key={control.key}
                    label={control.label}
                    checked={settings[control.key]}
                    onChange={(event) =>
                      onChange({ ...settings, [control.key]: event.target.checked })
                    }
                  />
                ))}
              </div>
            </section>
          ))}
          {numericGroups.map((group) => (
            <section key={group.title} className="space-y-2">
              <h3 className="text-sm font-semibold">{group.title}</h3>
              <div className="grid gap-3 md:grid-cols-2">
                {group.keys.map((key) => {
                  const field = numericFields.find((candidate) => candidate.key === key)!;
                  return (
                    <label key={field.key}>
                      {field.label}
                      <Input
                        aria-label={field.label}
                        type="number"
                        min={field.min}
                        max={field.max}
                        step={field.step ?? 1}
                        value={settings[field.key]}
                        onChange={(event) =>
                          onChange({ ...settings, [field.key]: Number(event.target.value) })
                        }
                      />
                      <small className="block text-xs text-base-content/50">
                        {field.min.toLocaleString()}–{field.max.toLocaleString()}
                      </small>
                    </label>
                  );
                })}
              </div>
            </section>
          ))}
        </div>
      </details>
    </div>
  );
}
