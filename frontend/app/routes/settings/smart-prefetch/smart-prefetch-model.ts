export const PREFETCH_KEY = "smart-prefetch.settings";
export type PrefetchSource = {
  ServerId: string;
  LibraryId: string;
  Kind: string;
  Key: string;
  Title: string;
  Type: string;
  Enabled: boolean;
  Limit: number;
  ExcludedShows: string[];
};
export type DisabledPlexLibrary = {
  ServerId: string;
  LibraryId: string;
  Type: "movie" | "show";
};
export const numericDefaults = {
  SyncIntervalMinutes: 15,
  RealtimeCheckIntervalSeconds: 30,
  MovieSyncIntervalMinutes: 60,
  TvSyncIntervalMinutes: 60,
  LookbackDays: 7,
  MoviePartialWatchLookbackDays: 7,
  MinEpisodesForPrediction: 2,
  ConfidenceThreshold: 0.5,
  CooldownMinutes: 15,
  QueueCapacity: 256,
  MaxRetries: 3,
  IntentTtlHours: 24,
  VerifiedSessionExpirySeconds: 60,
  MaxQueueAhead: 2,
  TvEpisodesPerShow: 2,
  MaxConcurrentJobs: 1,
  ConnectionsPerJob: 2,
  MaxBytesPerItem: 500_000_000_000,
  DailyByteBudget: 10_000_000_000,
  MinimumHeadMb: 16,
  MinimumTailMb: 8,
};
export const booleanDefaults = {
  Enabled: false,
  HistoryEnabled: false,
  RealtimeEnabled: true,
  ReadActivityEnabled: false,
  PredictionsEnabled: true,
  MinimumWarmEnabled: false,
  FullFileWarming: true,
  MovieEnabled: true,
  TvEnabled: true,
  WarmLocalFiles: false,
  PauseDuringPlayback: true,
};
export const DECIMAL_GB_BYTES = 1_000_000_000;

export function bytesToDecimalGb(bytes: number): number {
  return bytes / DECIMAL_GB_BYTES;
}

export function decimalGbToBytes(gigabytes: number): number {
  return Math.round(gigabytes * DECIMAL_GB_BYTES);
}

export type PrefetchSettings = typeof numericDefaults &
  typeof booleanDefaults & {
    Users: string[];
    Sources: PrefetchSource[];
    DisabledLibraries: DisabledPlexLibrary[];
  };
export type NumericKey = keyof typeof numericDefaults;
const customizableBooleanKeys = Object.keys(booleanDefaults).filter(
  (key): key is keyof typeof booleanDefaults => key !== "Enabled",
);
const customizableNumericKeys = Object.keys(numericDefaults) as (keyof typeof numericDefaults)[];

export function hasCustomizedPrefetchPolicy(settings: PrefetchSettings): boolean {
  return (
    customizableBooleanKeys.some((key) => settings[key] !== booleanDefaults[key]) ||
    customizableNumericKeys.some((key) => settings[key] !== numericDefaults[key])
  );
}

export function resetPrefetchPolicyDefaults(settings: PrefetchSettings): PrefetchSettings {
  return {
    ...settings,
    ...numericDefaults,
    ...booleanDefaults,
    Enabled: settings.Enabled,
  };
}

export const numericFields: {
  key: NumericKey;
  label: string;
  min: number;
  max: number;
  step?: number;
}[] = [
  { key: "QueueCapacity", label: "Maximum queued intents", min: 1, max: 256 },
  { key: "MaxRetries", label: "Retries after failure (0 means no retries)", min: 0, max: 10 },
  { key: "IntentTtlHours", label: "Intent lifetime (hours)", min: 1, max: 168 },
  {
    key: "VerifiedSessionExpirySeconds",
    label: "Verified playback expiry (seconds)",
    min: 5,
    max: 300,
  },
  {
    key: "ConfidenceThreshold",
    label: "Prediction confidence threshold (0–1)",
    min: 0,
    max: 1,
    step: 0.01,
  },
  { key: "CooldownMinutes", label: "Prediction cooldown (minutes)", min: 1, max: 1440 },
  { key: "SyncIntervalMinutes", label: "History sync interval (minutes)", min: 1, max: 1440 },
  {
    key: "RealtimeCheckIntervalSeconds",
    label: "Playback polling interval (seconds)",
    min: 5,
    max: 3600,
  },
  {
    key: "MovieSyncIntervalMinutes",
    label: "Movie source sync interval (minutes)",
    min: 1,
    max: 1440,
  },
  { key: "TvSyncIntervalMinutes", label: "TV source sync interval (minutes)", min: 1, max: 1440 },
  { key: "LookbackDays", label: "History lookback (days)", min: 1, max: 365 },
  {
    key: "MoviePartialWatchLookbackDays",
    label: "Partially watched movie lookback (days)",
    min: 1,
    max: 365,
  },
  {
    key: "MinEpisodesForPrediction",
    label: "Minimum watched episodes for prediction",
    min: 1,
    max: 100,
  },
  { key: "MaxQueueAhead", label: "Episodes to queue ahead", min: 1, max: 20 },
  { key: "TvEpisodesPerShow", label: "Episodes per show", min: 1, max: 20 },
  { key: "MaxConcurrentJobs", label: "Concurrent warm jobs", min: 1, max: 4 },
  { key: "ConnectionsPerJob", label: "Connections per job", min: 1, max: 8 },
  { key: "MaxBytesPerItem", label: "Maximum bytes per item", min: 1, max: 100_000_000_000_000 },
  {
    key: "DailyByteBudget",
    label: "Daily byte budget (0 means unlimited)",
    min: 0,
    max: 100_000_000_000_000,
  },
  { key: "MinimumHeadMb", label: "Minimum head range (MiB)", min: 0, max: 1024 },
  { key: "MinimumTailMb", label: "Minimum tail range (MiB)", min: 0, max: 1024 },
];
export function parsePrefetchSettings(json: string | undefined): PrefetchSettings {
  const parsed: unknown = json?.trim() ? JSON.parse(json) : {};
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed))
    throw new Error("Smart Prefetch settings must be an object.");
  const defaults: PrefetchSettings = {
    ...numericDefaults,
    ...booleanDefaults,
    Users: [],
    Sources: [],
    DisabledLibraries: [],
  };
  const settings = normalizeKnownFields(parsed, defaults);
  if (Array.isArray(settings.Sources))
    settings.Sources = settings.Sources.map((source) =>
      normalizeKnownFields(source, {
        ServerId: "",
        LibraryId: "",
        Kind: "hub",
        Key: "",
        Title: "",
        Type: "movie",
        Enabled: true,
        Limit: 10,
        ExcludedShows: [] as string[],
      }),
    );
  if (Array.isArray(settings.DisabledLibraries))
    settings.DisabledLibraries = settings.DisabledLibraries.map((library) =>
      normalizeKnownFields(library, {
        ServerId: "",
        LibraryId: "",
        Type: "movie",
      }),
    );
  if (
    !Array.isArray(settings.Users) ||
    settings.Users.some((user) => typeof user !== "string") ||
    !Array.isArray(settings.Sources) ||
    settings.Sources.some(
      (source) =>
        !source ||
        [
          source.ServerId,
          source.LibraryId,
          source.Kind,
          source.Key,
          source.Title,
          source.Type,
        ].some((value) => typeof value !== "string") ||
        typeof source.Enabled !== "boolean" ||
        typeof source.Limit !== "number" ||
        !Array.isArray(source.ExcludedShows) ||
        source.ExcludedShows.some((id) => typeof id !== "string"),
    ) ||
    !Array.isArray(settings.DisabledLibraries) ||
    settings.DisabledLibraries.some(
      (library) =>
        !library ||
        typeof library.ServerId !== "string" ||
        typeof library.LibraryId !== "string" ||
        typeof library.Type !== "string",
    ) ||
    Object.keys(booleanDefaults).some(
      (key) => typeof settings[key as keyof typeof booleanDefaults] !== "boolean",
    ) ||
    numericFields.some((field) => typeof settings[field.key] !== "number")
  )
    throw new Error("Smart Prefetch settings have an invalid shape.");
  return settings;
}
function normalizeKnownFields<T extends object>(value: unknown, defaults: T): T {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("Smart Prefetch settings have an invalid shape.");
  const result: Record<string, unknown> = Object.fromEntries(Object.entries(defaults));
  const names = Object.keys(defaults);
  for (const [name, field] of Object.entries(value)) {
    const canonical = names.find((candidate) => candidate.toLowerCase() === name.toLowerCase());
    if (!canonical) throw new Error("Smart Prefetch settings contain unknown fields.");
    result[canonical] = field;
  }
  return result as T;
}
export function validatePrefetchSettings(settings: PrefetchSettings): string | null {
  if (
    Object.keys(booleanDefaults).some(
      (key) => typeof settings[key as keyof typeof booleanDefaults] !== "boolean",
    )
  )
    return "Trigger choices must be boolean values.";
  for (const field of numericFields)
    if (
      !Number.isFinite(settings[field.key]) ||
      (!field.step && !Number.isSafeInteger(settings[field.key])) ||
      settings[field.key] < field.min ||
      settings[field.key] > field.max
    )
      return `${field.label} must be ${field.step ? "a number" : "a whole number"} from ${field.min} to ${field.max}.`;
  if (
    !Array.isArray(settings.Users) ||
    settings.Users.length > 256 ||
    settings.Users.some((user) => typeof user !== "string" || !user.trim() || user.length > 256)
  )
    return "Choose at most 256 valid scoped users.";
  if (!Array.isArray(settings.Sources) || settings.Sources.length > 128)
    return "Choose at most 128 sources.";
  if (!Array.isArray(settings.DisabledLibraries) || settings.DisabledLibraries.length > 128)
    return "Disable at most 128 Plex libraries.";
  const libraryKeys = new Set<string>();
  for (const library of settings.DisabledLibraries) {
    if (
      !library ||
      typeof library.ServerId !== "string" ||
      !library.ServerId.trim() ||
      library.ServerId.length > 128 ||
      typeof library.LibraryId !== "string" ||
      library.LibraryId.length > 128 ||
      !["movie", "show"].includes(library.Type)
    )
      return "A disabled Plex library has an invalid server, library, or media identity.";
    const key = `${library.ServerId}\n${library.LibraryId}\n${library.Type}`;
    if (libraryKeys.has(key)) return "Remove duplicate disabled Plex libraries.";
    libraryKeys.add(key);
  }
  const keys = new Set<string>();
  for (const source of settings.Sources) {
    if (
      !source ||
      typeof source.ServerId !== "string" ||
      !source.ServerId ||
      source.ServerId.length > 128 ||
      typeof source.LibraryId !== "string" ||
      source.LibraryId.length > 128 ||
      typeof source.Title !== "string" ||
      source.Title.length > 512 ||
      !["hub", "collection"].includes(source.Kind) ||
      !["movie", "show", "episode", "clip"].includes(source.Type) ||
      typeof source.Enabled !== "boolean" ||
      !Number.isInteger(source.Limit) ||
      source.Limit < 1 ||
      source.Limit > 1000 ||
      typeof source.Key !== "string" ||
      source.Key.length > 2048 ||
      !/^\/(library|hubs)\//.test(source.Key) ||
      source.Key.includes("..") ||
      source.Key.includes("//") ||
      /x-plex-token/i.test(source.Key) ||
      !Array.isArray(source.ExcludedShows) ||
      source.ExcludedShows.length > 1000 ||
      source.ExcludedShows.some((id) => typeof id !== "string" || !id.trim() || id.length > 128)
    )
      return "A selected source has invalid identity, limit, or show exclusions.";
    const key = `${source.ServerId}\n${source.Kind}\n${source.Key}`;
    if (keys.has(key)) return "Remove duplicate source selections.";
    keys.add(key);
  }
  return null;
}
export function isSmartPrefetchSettingsValid(config: Record<string, string>): boolean {
  try {
    return validatePrefetchSettings(parsePrefetchSettings(config[PREFETCH_KEY])) === null;
  } catch {
    return false;
  }
}
export function hasSmartPrefetchSettingsChanged(
  original: Record<string, string>,
  current: Record<string, string>,
): boolean {
  try {
    return (
      JSON.stringify(parsePrefetchSettings(original[PREFETCH_KEY])) !==
      JSON.stringify(parsePrefetchSettings(current[PREFETCH_KEY]))
    );
  } catch {
    return original[PREFETCH_KEY] !== current[PREFETCH_KEY];
  }
}
