import {
  type Dispatch,
  type ReactNode,
  type SetStateAction,
  useCallback,
  useMemo,
  useState,
} from "react";
import { MultiCheckboxInput } from "~/components/multi-checkbox-input/multi-checkbox-input";
import {
  Button,
  Field,
  Icon,
  Input,
  Label,
  ManagedSetting,
  Select,
  SettingsCard,
  SettingsIntro,
  SettingsPage,
  Toggle,
  Tooltip,
} from "~/components/ui";
import { buildProfileAdapterUrl, type ProfileAdapterKey } from "./profile-adapter-urls";

type ProfilesSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

type FallbackMode = "Off" | "Title" | "Broad";
type QualitySortMode = "Off" | "Resolution" | "ResolutionAndSource";

interface Profile {
  Token: string;
  Name: string;
  IndexerNames: string[];
  EnabledAdapters?: string[] | null;
  QualitySort?: QualitySortMode;
  MovieFallback?: FallbackMode;
  TvFallback?: FallbackMode;
  MovieFallbackMinResults?: number;
  TvFallbackMinResults?: number;
  // `undefined` is a meaningful explicit value here: writeFallback spreads a
  // patch with `QueryFallbackMinResults: undefined` to clear any prior value.
  QueryFallbackMinResults?: number | undefined;
}

interface ProfileConfig {
  Profiles: Profile[];
}

interface IndexerSummary {
  Name: string;
  Enabled: boolean;
}

type AdapterKey = ProfileAdapterKey;

const ADAPTERS: {
  key: AdapterKey;
  name: string;
  description: string;
  buildUrl: (origin: string, token: string) => string;
}[] = [
  {
    key: "json",
    name: "JSON Search API",
    description: "Vendor-neutral JSON results. Use from custom clients or scripts.",
    buildUrl: (origin, token) => buildProfileAdapterUrl(origin, "json", token),
  },
  {
    key: "newznab",
    name: "Newznab",
    description: "Newznab-protocol meta-indexer endpoint. Add to Prowlarr / Sonarr / Radarr.",
    buildUrl: (origin, token) => buildProfileAdapterUrl(origin, "newznab", token),
  },
  {
    key: "addon",
    name: "Addon",
    description:
      "Manifest-based addon endpoint. Install the URL in a compatible client to query this Search Profile.",
    buildUrl: (origin, token) => buildProfileAdapterUrl(origin, "addon", token),
  },
];

const ALL_ADAPTER_KEYS: AdapterKey[] = ADAPTERS.map((a) => a.key);

function parseProfileConfig(raw: string): ProfileConfig {
  try {
    // The profiles.instances config value stores a serialized ProfileConfig.
    const parsed = JSON.parse(raw || "{}") as Partial<ProfileConfig>;
    return { Profiles: parsed.Profiles ?? [] };
  } catch {
    return { Profiles: [] };
  }
}

function parseIndexerNames(raw: string): string[] {
  try {
    // The indexers.instances config value stores a serialized { Indexers: IndexerSummary[] }.
    const parsed = JSON.parse(raw || "{}") as { Indexers?: IndexerSummary[] };
    const list: IndexerSummary[] = parsed.Indexers ?? [];
    return list.filter((i) => i.Enabled && i.Name?.trim()).map((i) => i.Name);
  } catch {
    return [];
  }
}

function makeToken(): string {
  const bytes = new Uint8Array(12);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

function isAdapterEnabled(profile: Profile, key: AdapterKey): boolean {
  const list = profile.EnabledAdapters;
  if (list === null || list === undefined || list.length === 0) return true;
  return list.some((x) => x?.toLowerCase() === key);
}

export function ProfilesSettings({ config, setNewConfig }: ProfilesSettingsProps) {
  const profileConfig = useMemo(() => parseProfileConfig(config["profiles.instances"]!), [config]);
  const availableIndexers = useMemo(
    () => parseIndexerNames(config["indexers.instances"]!),
    [config],
  );

  const update = useCallback(
    (next: ProfileConfig) => {
      setNewConfig({ ...config, "profiles.instances": JSON.stringify(next) });
    },
    [config, setNewConfig],
  );

  const add = useCallback(() => {
    update({
      Profiles: [
        ...profileConfig.Profiles,
        {
          Token: makeToken(),
          Name: "",
          IndexerNames: [],
          EnabledAdapters: [...ALL_ADAPTER_KEYS],
          QualitySort: "Off",
          MovieFallback: "Off",
          TvFallback: "Off",
          MovieFallbackMinResults: 3,
          TvFallbackMinResults: 3,
        },
      ],
    });
  }, [profileConfig, update]);

  const remove = useCallback(
    (index: number) => {
      update({ Profiles: profileConfig.Profiles.filter((_, i) => i !== index) });
    },
    [profileConfig, update],
  );

  const change = useCallback(
    (index: number, patch: Partial<Profile>) => {
      update({
        Profiles: profileConfig.Profiles.map((x, i) => (i === index ? { ...x, ...patch } : x)),
      });
    },
    [profileConfig, update],
  );

  return (
    <SettingsPage>
      <SettingsIntro>
        Each search profile exposes a token-scoped search API over its own indexer selection. Enable
        one or more output adapters (JSON, Newznab, or Addon) so *Arr and other clients can query
        it.
      </SettingsIntro>
      <ManagedSetting configKey="profiles.instances">
        <SettingsCard
          icon="person_search"
          title="Search profiles"
          description="Add a profile, pick indexers, then copy an adapter URL into Prowlarr, Sonarr, or Radarr."
          action={
            <Button size="xsmall" onClick={add}>
              <Icon name="add" className="!text-[16px]" />
              Add
            </Button>
          }
        >
          {profileConfig.Profiles.length === 0 ? (
            <p className="rounded-lg border border-dashed border-base-content/20 bg-base-200/40 p-5 text-base-content/60">
              No search profiles yet. Add one to expose a token-scoped search API over a subset of
              your indexers.
            </p>
          ) : (
            <div className="flex flex-col gap-2">
              {profileConfig.Profiles.map((profile, index) => (
                <ProfileForm
                  key={profile.Token}
                  profile={profile}
                  index={index}
                  availableIndexers={availableIndexers}
                  onChange={change}
                  onRemove={remove}
                />
              ))}
            </div>
          )}
        </SettingsCard>
      </ManagedSetting>
    </SettingsPage>
  );
}

const FALLBACK_DEFAULT_THRESHOLD = 3;

function effMode(
  explicit: FallbackMode | undefined,
  legacy: number | undefined,
  broadDefault: boolean,
): FallbackMode {
  if (explicit) return explicit;
  if (legacy && legacy > 0) return broadDefault ? "Broad" : "Title";
  return "Off";
}

function effThreshold(explicit: number | undefined, legacy: number | undefined): number {
  if (explicit && explicit > 0) return explicit;
  if (legacy && legacy > 0) return legacy;
  return FALLBACK_DEFAULT_THRESHOLD;
}

function fallbackHelp(mode: FallbackMode, allowBroad: boolean, indexerCount: number): string {
  if (mode === "Off") return "";
  const what =
    mode === "Broad"
      ? "Title + episode, then a whole-show query that also catches untagged specials."
      : allowBroad
        ? "Title + episode; wrong episodes are filtered out."
        : "Title search.";
  if (indexerCount <= 0) return what;
  const total = (mode === "Broad" ? 2 : 1) * indexerCount;
  const q = total === 1 ? "query" : "queries";
  const idxs = indexerCount === 1 ? "indexer" : "indexers";
  return `${what} Up to ${total} extra ${q} per search across this profile's ${indexerCount} ${idxs}.`;
}

interface FallbackControlProps {
  label: ReactNode;
  mode: FallbackMode;
  threshold: number;
  allowBroad: boolean;
  indexerCount: number;
  disabled: boolean;
  onModeChange: (mode: FallbackMode) => void;
  onThresholdChange: (n: number) => void;
}

function FallbackControl({
  label,
  mode,
  threshold,
  allowBroad,
  indexerCount,
  disabled,
  onModeChange,
  onThresholdChange,
}: FallbackControlProps) {
  return (
    <Field>
      <Label>{label}</Label>
      <Select
        className="mb-2 w-full"
        value={mode}
        disabled={disabled}
        onChange={(e) => onModeChange(e.target.value as FallbackMode)}
      >
        <option value="Off">Off (exact ID match only)</option>
        <option value="Title">
          {allowBroad ? "Title + episode (precise)" : "Also search by title"}
        </option>
        {allowBroad && <option value="Broad">Title + episode, then whole show (broad)</option>}
      </Select>
      {disabled ? (
        <p className="mb-0 mt-1 text-[11px] leading-relaxed text-base-content/45">
          Add indexers in the Indexers tab to use fallback.
        </p>
      ) : (
        mode !== "Off" && (
          <>
            <div className="my-1.5 flex flex-wrap items-center gap-1.5">
              <span className="text-[13px] text-base-content/60">
                when the ID search finds fewer than
              </span>
              <Input
                type="number"
                min={1}
                step={1}
                className="h-auto w-[46px] flex-none px-2 py-[3px] text-center text-[13px] leading-snug [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
                value={threshold}
                onChange={(e) => {
                  const n = parseInt(e.target.value, 10);
                  onThresholdChange(Number.isFinite(n) && n > 0 ? n : 1);
                }}
              />
              <span className="text-[13px] text-base-content/60">results</span>
            </div>
            <p className="mb-0 mt-1 text-[11px] leading-relaxed text-base-content/45">
              {fallbackHelp(mode, allowBroad, indexerCount)}
            </p>
          </>
        )
      )}
    </Field>
  );
}

interface ProfileFormProps {
  profile: Profile;
  index: number;
  availableIndexers: string[];
  onChange: (index: number, patch: Partial<Profile>) => void;
  onRemove: (index: number) => void;
}

function ProfileForm({ profile, index, availableIndexers, onChange, onRemove }: ProfileFormProps) {
  const origin = typeof window === "undefined" ? "" : window.location.origin;
  const indexersCsv = profile.IndexerNames.join(", ");

  const enabledKeys = useMemo(() => {
    return ADAPTERS.filter((a) => isAdapterEnabled(profile, a.key)).map((a) => a.key);
  }, [profile]);

  const setAdapterEnabled = useCallback(
    (key: AdapterKey, enabled: boolean) => {
      const next = new Set<AdapterKey>(enabledKeys);
      if (enabled) next.add(key);
      else next.delete(key);
      const list = ALL_ADAPTER_KEYS.filter((k) => next.has(k));
      onChange(index, { EnabledAdapters: list });
    },
    [enabledKeys, index, onChange],
  );

  const movieMode = effMode(profile.MovieFallback, profile.QueryFallbackMinResults, false);
  const tvMode = effMode(profile.TvFallback, profile.QueryFallbackMinResults, true);
  const movieThreshold = effThreshold(
    profile.MovieFallbackMinResults,
    profile.QueryFallbackMinResults,
  );
  const tvThreshold = effThreshold(profile.TvFallbackMinResults, profile.QueryFallbackMinResults);
  const indexerCount =
    profile.IndexerNames.length > 0 ? profile.IndexerNames.length : availableIndexers.length;
  const noIndexers = availableIndexers.length === 0;

  const writeFallback = useCallback(
    (patch: Partial<Profile>) => {
      onChange(index, {
        MovieFallback: movieMode,
        TvFallback: tvMode,
        MovieFallbackMinResults: movieThreshold,
        TvFallbackMinResults: tvThreshold,
        QueryFallbackMinResults: undefined,
        ...patch,
      });
    },
    [index, onChange, movieMode, tvMode, movieThreshold, tvThreshold],
  );

  return (
    <section className="relative mb-4 rounded-lg border border-base-content/10 bg-base-100 p-4 text-base-content/80 transition-colors duration-120 hover:border-base-content/20">
      <button
        type="button"
        className="absolute right-2.5 top-2.5 flex h-6 w-6 cursor-pointer items-center justify-center rounded-md border-none bg-transparent p-0 text-lg leading-none text-base-content/45 transition-colors hover:bg-base-content/5 hover:text-base-content"
        onClick={() => onRemove(index)}
        aria-label="Remove"
      >
        ×
      </button>
      <div className="flex flex-col gap-4">
        <Field>
          <Label htmlFor={`profile-name-${profile.Token}`}>Name</Label>
          <Input
            id={`profile-name-${profile.Token}`}
            type="text"
            className="mb-2 w-full"
            placeholder="e.g. Movies"
            value={profile.Name}
            onChange={(e) => onChange(index, { Name: e.target.value })}
          />
        </Field>
        <Field>
          <Label>
            Indexers{" "}
            <span className="font-normal text-base-content/60">
              (leave all unchecked to use every enabled indexer)
            </span>
          </Label>
          {availableIndexers.length === 0 ? (
            <p className="mb-0 mt-1 text-[11px] leading-relaxed text-base-content/45">
              No indexers configured yet. Add some under the Indexers tab.
            </p>
          ) : (
            <MultiCheckboxInput
              options={availableIndexers}
              value={indexersCsv}
              onChange={(v) =>
                onChange(index, {
                  IndexerNames: v
                    .split(",")
                    .map((s) => s.trim())
                    .filter(Boolean),
                })
              }
            />
          )}
        </Field>
        <Field>
          <Label>
            Output adapters{" "}
            <span className="font-normal text-base-content/60">
              (toggle the protocols this profile exposes)
            </span>
          </Label>
          <div className="mt-2">
            {ADAPTERS.map((adapter) => (
              <AdapterRow
                key={adapter.key}
                token={profile.Token}
                origin={origin}
                adapter={adapter}
                enabled={isAdapterEnabled(profile, adapter.key)}
                onToggle={(enabled) => setAdapterEnabled(adapter.key, enabled)}
              />
            ))}
          </div>
        </Field>
        <Field>
          <Label htmlFor={`profile-quality-sort-${profile.Token}`}>Result ordering</Label>
          <Select
            id={`profile-quality-sort-${profile.Token}`}
            className="mb-2 w-full"
            value={profile.QualitySort ?? "Off"}
            onChange={(e) => onChange(index, { QualitySort: e.target.value as QualitySortMode })}
          >
            <option value="Off">Off — keep current grabs/size/date order</option>
            <option value="Resolution">Resolution — prefer 2160p, then 1080p, 720p, SD</option>
            <option value="ResolutionAndSource">
              Resolution + source — resolution first, then Remux/BluRay/WEB-DL/...
            </option>
          </Select>
          <p className="mb-0 mt-1 text-[11px] leading-relaxed text-base-content/45">
            Quality sorting is opt-in. Existing profiles keep grabs, size, and posted-date order
            until you enable a mode. Resolution always ranks above source.
          </p>
        </Field>
        <Field>
          <Label>
            Query fallback{" "}
            <span className="font-normal text-base-content/60">
              (extra title searches when an ID lookup comes up short)
            </span>
          </Label>
          <p className="mb-0 mt-1 text-[11px] leading-relaxed text-base-content/45">
            These fire extra queries to this profile's indexers and spend their hit and rate limits,
            which you set per indexer in the Indexers tab.
          </p>
        </Field>
        <FallbackControl
          label="Movies"
          mode={movieMode}
          threshold={movieThreshold}
          allowBroad={false}
          indexerCount={indexerCount}
          disabled={noIndexers}
          onModeChange={(m) => writeFallback({ MovieFallback: m })}
          onThresholdChange={(n) => writeFallback({ MovieFallbackMinResults: n })}
        />
        <FallbackControl
          label="TV"
          mode={tvMode}
          threshold={tvThreshold}
          allowBroad={true}
          indexerCount={indexerCount}
          disabled={noIndexers}
          onModeChange={(m) => writeFallback({ TvFallback: m })}
          onThresholdChange={(n) => writeFallback({ TvFallbackMinResults: n })}
        />
      </div>
    </section>
  );
}

interface AdapterRowProps {
  token: string;
  origin: string;
  adapter: (typeof ADAPTERS)[number];
  enabled: boolean;
  onToggle: (enabled: boolean) => void;
}

function AdapterRow({ token, origin, adapter, enabled, onToggle }: AdapterRowProps) {
  const [copied, setCopied] = useState(false);
  const url = useMemo(() => adapter.buildUrl(origin, token), [origin, token, adapter]);

  const onCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // clipboard write can fail without focus/permission; nothing to report
    }
  }, [url]);

  return (
    <div
      className={`mb-2 rounded-md border border-base-content/10 bg-base-200 p-3 ${enabled ? "" : "opacity-55"}`}
    >
      <div className="flex items-center justify-between gap-3">
        <div className="flex flex-col gap-0.5">
          <span className="text-sm font-medium text-base-content">{adapter.name}</span>
          <span className="text-xs text-base-content/60">{adapter.description}</span>
        </div>
        <Tooltip content={adapter.description}>
          <Toggle
            id={`adapter-${token}-${adapter.key}`}
            label={`Enable ${adapter.name}`}
            className="cursor-pointer gap-2 p-0 [&>span:last-child]:sr-only"
            checked={enabled}
            onChange={(e) => onToggle(e.target.checked)}
          />
        </Tooltip>
      </div>
      {enabled && (
        <div className="join mt-2 w-full">
          <Input
            className="join-item min-w-0 flex-1 font-mono text-xs"
            type="text"
            readOnly
            value={url}
            onFocus={(e) => e.currentTarget.select()}
          />
          <Button
            className="join-item"
            variant={copied ? "success" : "secondary"}
            size="xsmall"
            onClick={() => void onCopy()}
          >
            {copied ? "Copied" : "Copy"}
          </Button>
        </div>
      )}
    </div>
  );
}

export function isProfilesSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return config["profiles.instances"] !== newConfig["profiles.instances"];
}

export function isProfilesSettingsValid(newConfig: Record<string, string>) {
  try {
    const c = parseProfileConfig(newConfig["profiles.instances"]!);
    const tokens = new Set<string>();
    for (const p of c.Profiles) {
      if (!p.Token?.trim()) return false;
      if (tokens.has(p.Token)) return false;
      tokens.add(p.Token);
      if (!p.Name?.trim()) return false;
    }
    return true;
  } catch {
    return false;
  }
}
