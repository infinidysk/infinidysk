import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { RotateCcw } from "lucide-react";
import { api, body, type Schemas } from "../api/client";
import { bytes } from "../format";
import { AdvancedSettings, Failure, Field, InfoTip, Loading, Locked, OptionCards, SettingsPanel, Toggle } from "../ui";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Slider } from "@/components/ui/slider";

type Perf = Schemas["Performance"];
type Tuning = Schemas["Tuning"];
type Key = keyof Tuning;

const presets: Record<Schemas["Preset"], { title: string; blurb: string }> = {
  balanced: { title: "Balanced", blurb: "Smooth 4K playback on most machines." },
  max_playback: { title: "Max playback", blurb: "Deeper read-ahead and cache for fast links and many viewers." },
  low_resources: { title: "Low resources", blurb: "Less memory and shallower read-ahead for small machines." },
};

const keys: Key[] = [
  "read_ahead_mb",
  "memory_cache_enabled",
  "memory_cache_mb",
  "disk_cache_enabled",
  "disk_cache_dir",
  "disk_cache_gb",
  "disk_cache_max_age_hours",
  "read_ahead_memory_mb",
  "fuse_max_readahead_kb",
  "fuse_max_background",
];
const cleared = Object.fromEntries(keys.map((k) => [k, null])) as Omit<Perf, "preset">;

/** Seconds of video `mb` holds at a typical 4K bitrate of 5 MB/s. */
const seconds = (mb: number) => Math.max(1, Math.round(mb / 5));

/** A slider value in its unit, moved up one unit from 1000 (1250 MB reads 1.25 GB). */
const size = (value: number, unit?: string) =>
  value >= 1000 && unit ? `${+(value / 1000).toFixed(2)} ${unit === "MB" ? "GB" : "MB"}` : `${value}${unit ? ` ${unit}` : ""}`;

export function PerformanceSettings({
  value,
  locked,
  invalid,
  onChange,
  limit,
  onLimit,
}: {
  value: Perf;
  locked: (path: string) => boolean;
  /** The save error about the setting at a dotted path. */
  invalid: (path: string) => string | undefined;
  onChange: (value: Perf) => void;
  /** `network.download_limit_mb_per_s`; null is unlimited. */
  limit: number | null;
  onLimit: (value: number | null) => void;
}) {
  const dir = value.disk_cache_dir;
  const view = useQuery({
    queryKey: ["performance", dir],
    queryFn: () => body(api.GET("/api/v1/performance", { params: { query: dir?.startsWith("/") ? { dir } : {} } })),
    placeholderData: keepPreviousData,
    refetchInterval: 5000,
  });
  if (view.isPending) return <Loading />;
  if (view.isError) return <Failure error={view.error} />;

  const base = view.data.presets.find((p) => p.preset === value.preset)?.tuning ?? view.data.effective;
  const get = <K extends Key>(k: K) => (value[k] ?? base[k]) as Tuning[K];
  // An override equal to the preset's value is stored as none, so the preset stays in charge of it.
  const set = <K extends Key>(k: K, v: Tuning[K]) => onChange({ ...value, [k]: v === base[k] ? null : v });
  const off = (k: Key) => locked(`performance.${k}`);
  const custom = keys.some((k) => value[k] != null);
  const free = view.data.free_bytes;
  const cache = view.data.cache;
  const used = (n: number, of: number) => ` ${bytes(n, true)} of ${bytes(of, true)} in use.`;

  return (
    <>
      <SinceStart view={view.data} />
      <div className="space-y-2">
        <div className="flex h-6 items-center justify-between">
          <Label>
            Preset
            <Locked locked={locked("performance.preset")} />
          </Label>
          {custom && (
            <Button
              type="button"
              variant="link"
              size="sm"
              className="h-auto p-0"
              disabled={locked("performance")}
              onClick={() => onChange({ ...value, ...cleared })}
            >
              <RotateCcw />
              Reset to {presets[value.preset].title}
            </Button>
          )}
        </div>
        <OptionCards
          label="Preset"
          value={value.preset}
          disabled={locked("performance.preset")}
          onChange={(preset) => onChange({ ...value, ...cleared, preset })}
          options={view.data.presets.map(({ preset, tuning }) => ({
            value: preset,
            title: (
              <>
                {presets[preset].title}
                {preset === value.preset && custom && <Badge variant="secondary">Custom</Badge>}
              </>
            ),
            description: presets[preset].blurb,
            meta: `${size(tuning.read_ahead_mb, "MB")} ahead · ${size(tuning.memory_cache_mb, "MB")} cache`,
          }))}
        />
      </div>

      <SettingsPanel title="Read-ahead and cache">
        <PowerSlider
          id="read-ahead"
          label="Read-ahead per file"
          value={get("read_ahead_mb")}
          min={1}
          max={1250}
          unit="MB"
          disabled={off("read_ahead_mb")}
          hint={`About ${seconds(get("read_ahead_mb"))} s of 4K video.`}
          tip="Higher keeps playback smooth through slow providers; lower makes seeking respond faster."
          onChange={(v) => set("read_ahead_mb", v)}
        />
        <Toggle
          id="memory-cache"
          label={
            <>
              Memory cache
              <Locked locked={off("memory_cache_enabled")} />
            </>
          }
          hint="Keeps recent segments in memory, so rewinds and a second viewer of the same file need no download."
          checked={get("memory_cache_enabled")}
          disabled={off("memory_cache_enabled")}
          onChange={(v) => set("memory_cache_enabled", v)}
        />
        {get("memory_cache_enabled") && (
          <PowerSlider
            id="memory-cache-size"
            label="Memory cache size"
            value={get("memory_cache_mb")}
            min={16}
            max={65536}
            unit="MB"
            disabled={off("memory_cache_mb")}
            hint={`About ${seconds(get("memory_cache_mb"))} s of 4K video.${cache.memory_enabled ? used(cache.memory_bytes, cache.memory_capacity) : ""}`}
            onChange={(v) => set("memory_cache_mb", v)}
          />
        )}
        <Toggle
          id="disk-cache"
          label={
            <>
              Disk cache
              <Locked locked={off("disk_cache_enabled")} />
            </>
          }
          hint="Keeps segments on disk across restarts. Use a fast local disk, not the mount."
          checked={get("disk_cache_enabled")}
          disabled={off("disk_cache_enabled")}
          onChange={(v) => set("disk_cache_enabled", v)}
        />
        {get("disk_cache_enabled") && (
          <>
            <Field
              id="disk-cache-dir"
              label={
                <>
                  Directory
                  <Locked locked={off("disk_cache_dir")} />
                </>
              }
              hint={free != null ? `${bytes(free)} free on this volume.` : "An absolute path."}
              error={invalid("performance.disk_cache_dir")}
            >
              <Input
                id="disk-cache-dir"
                className="font-mono"
                required
                pattern="/.*"
                disabled={off("disk_cache_dir")}
                value={get("disk_cache_dir")}
                onChange={(e) => set("disk_cache_dir", e.target.value)}
              />
            </Field>
            <NumberField
              id="disk-cache-size"
              label="Size limit"
              unit="GB"
              value={get("disk_cache_gb")}
              min={1}
              max={100000}
              disabled={off("disk_cache_gb")}
              hint={`Shrinks to leave 1 GB free.${cache.disk_enabled ? used(cache.disk_bytes, cache.disk_capacity) : ""}`}
              error={invalid("performance.disk_cache_gb")}
              onChange={(v) => set("disk_cache_gb", v)}
            />
            <NumberField
              id="disk-cache-age"
              label="Keep for"
              unit="hours"
              value={get("disk_cache_max_age_hours")}
              min={1}
              max={8760}
              disabled={off("disk_cache_max_age_hours")}
              hint={`${+(get("disk_cache_max_age_hours") / 24).toFixed(1)} days since last read.`}
              error={invalid("performance.disk_cache_max_age_hours")}
              onChange={(v) => set("disk_cache_max_age_hours", v)}
            />
          </>
        )}
      </SettingsPanel>

      <SettingsPanel title="Download speed">
        <Toggle
          id="download-limit"
          label={
            <>
              Limit download speed
              <Locked locked={locked("network.download_limit_mb_per_s")} />
            </>
          }
          hint="Caps the combined download speed from all your Usenet providers, so infinidysk leaves room on a shared connection. Playback and imports share the cap; serving files to players is not limited."
          disabled={locked("network.download_limit_mb_per_s")}
          checked={limit != null}
          onChange={(on) => onLimit(on ? DEFAULT_DOWNLOAD_LIMIT : null)}
        />
        {limit != null && (
          <DownloadLimit value={limit} disabled={locked("network.download_limit_mb_per_s")} onChange={onLimit} />
        )}
      </SettingsPanel>

      <AdvancedSettings>
        <SettingsPanel>
          <PowerSlider
            id="read-ahead-memory"
            label="Read-ahead memory, all files"
            value={get("read_ahead_memory_mb")}
            min={64}
            max={65536}
            unit="MB"
            disabled={off("read_ahead_memory_mb")}
            hint="Upper bound on data fetched ahead across every open file. Takes effect after a restart."
            onChange={(v) => set("read_ahead_memory_mb", v)}
          />
          <PowerSlider
            id="fuse-readahead"
            label="Mount read-ahead"
            value={get("fuse_max_readahead_kb")}
            min={128}
            max={65536}
            unit="KB"
            disabled={off("fuse_max_readahead_kb")}
            hint="How far the kernel reads ahead on the mount. Takes effect after a restart."
            onChange={(v) => set("fuse_max_readahead_kb", v)}
          />
          <PowerSlider
            id="fuse-background"
            label="Mount background requests"
            value={get("fuse_max_background")}
            min={1}
            max={1024}
            disabled={off("fuse_max_background")}
            hint="Kernel requests in flight on the mount. Takes effect after a restart."
            onChange={(v) => set("fuse_max_background", v)}
          />
        </SettingsPanel>
      </AdvancedSettings>
    </>
  );
}

/** 25, 50, 75, 100 and 200% of a gigabit connection, in MB/s. */
const DOWNLOAD_LIMIT_STOPS = [31, 63, 94, 125, 250];
const DEFAULT_DOWNLOAD_LIMIT = 63;

function DownloadLimit({ value, disabled, onChange }: { value: number; disabled: boolean; onChange: (value: number) => void }) {
  const at = DOWNLOAD_LIMIT_STOPS.findIndex((v) => v >= value);
  return (
    <Field
      id="download-limit-speed"
      layout="row"
      label={
        <span className="flex w-full justify-between">
          Speed
          <span className="font-mono tabular-nums">{value} MB/s</span>
        </span>
      }
      hint={`${Math.round(value * 0.8)}% of a gigabit connection. Protocol overhead adds a few percent on the wire.`}
    >
      <Slider
        id="download-limit-speed"
        aria-label="Download speed limit"
        min={0}
        max={DOWNLOAD_LIMIT_STOPS.length - 1}
        step={1}
        disabled={disabled}
        value={[at === -1 ? DOWNLOAD_LIMIT_STOPS.length - 1 : at]}
        onValueChange={([i]) => onChange(DOWNLOAD_LIMIT_STOPS[i!]!)}
      />
    </Field>
  );
}

/** Round sizes per decade, so slider stops read as plain decimal numbers. */
const STEPS = [1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 7, 8];

function ladder(min: number, max: number): number[] {
  const out = new Set<number>();
  for (let decade = 1; decade <= max; decade *= 10)
    for (const step of STEPS) {
      const v = Math.round(step * decade);
      if (v >= min && v <= max) out.add(v);
    }
  return [...out];
}

/** A slider over round decimal sizes, for sizes that span several orders of magnitude. */
function PowerSlider({
  id,
  label,
  value,
  min,
  max,
  unit,
  hint,
  tip,
  disabled,
  onChange,
}: {
  id: string;
  label: string;
  value: number;
  min: number;
  max: number;
  unit?: string;
  hint: string;
  tip?: string;
  disabled: boolean;
  onChange: (value: number) => void;
}) {
  const stops = ladder(min, max);
  const at = stops.findIndex((v) => v >= value);
  return (
    <Field
      id={id}
      layout="row"
      label={
        <span className="flex w-full justify-between">
          <span className="flex items-center gap-1.5">
            {label}
            {tip && <InfoTip label={`About ${label.toLowerCase()}`}>{tip}</InfoTip>}
            <Locked locked={disabled} />
          </span>
          <span className="font-mono tabular-nums">{size(value, unit)}</span>
        </span>
      }
      hint={hint}
    >
      <Slider
        id={id}
        aria-label={label}
        min={0}
        max={stops.length - 1}
        step={1}
        disabled={disabled}
        value={[at === -1 ? stops.length - 1 : at]}
        onValueChange={([i]) => onChange(stops[i!]!)}
      />
    </Field>
  );
}

function NumberField({
  id,
  label,
  unit,
  value,
  min,
  max,
  hint,
  error,
  disabled,
  onChange,
}: {
  id: string;
  label: string;
  unit: string;
  value: number;
  min: number;
  max: number;
  hint: string;
  error?: string;
  disabled: boolean;
  onChange: (value: number) => void;
}) {
  return (
    <Field
      id={id}
      layout="row"
      label={
        <>
          {label}
          <Locked locked={disabled} />
        </>
      }
      hint={hint}
      error={error}
    >
      <div className="relative">
        <Input
          id={id}
          type="number"
          className="pr-16 tabular-nums"
          required
          min={min}
          max={max}
          disabled={disabled}
          value={value}
          onChange={(e) => e.target.value !== "" && onChange(Math.trunc(e.target.valueAsNumber))}
        />
        <span className="text-muted-foreground pointer-events-none absolute inset-y-0 right-8 flex items-center text-xs">{unit}</span>
      </div>
    </Field>
  );
}

/** Cache results since the last start, in one line. */
function SinceStart({ view }: { view: Schemas["PerformanceView"] }) {
  const c = view.cache;
  const shared = c.memory_hits + c.disk_hits + c.joined;
  const total = shared + c.misses;
  return (
    <p className="text-muted-foreground flex items-center gap-1.5 text-xs">
      Since start: {total > 0 ? `${Math.round((shared / total) * 100)}% hit rate` : "no reads yet"} ·{" "}
      {bytes(c.bytes_served, true)} served without a download
      <InfoTip label="About cache results">
        {c.memory_hits} memory hits, {c.disk_hits} disk hits, {c.joined} shared fetches and {view.missing_hits} known-missing
        skips out of {total} segments.
      </InfoTip>
    </p>
  );
}
