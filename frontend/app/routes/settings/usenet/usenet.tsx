import {
  type Dispatch,
  type SetStateAction,
  type ReactNode,
  type CSSProperties,
  useState,
  useCallback,
  useEffect,
  useMemo,
  useRef,
} from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import {
  Alert,
  Badge,
  Button,
  HelpText,
  Icon,
  Input,
  Label,
  ManagedSetting,
  Modal,
  Select,
  SettingsIntro,
  SettingsPage,
  Tooltip,
  Toggle,
} from "~/components/ui";
import { subscribeWebsocketTopics, useWebsocketTopic } from "~/utils/shared-websocket";
import { isMaskedSecret } from "~/utils/config-mask";
import { generateUuid } from "~/utils/uuid";
import { shouldWarnCleartextCredentials } from "./cleartext-credentials";
import {
  applyAutoTuneTransferRecommendation,
  resolveBenchmarkConnectionLimits,
} from "./provider-autotune";
import {
  calculateProviderConnectionBudget,
  formatMetadataCapacity,
} from "./provider-connection-budget";
import {
  DndContext,
  type DragEndEvent,
  type DraggableAttributes,
  type DraggableSyntheticListeners,
  closestCenter,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
} from "@dnd-kit/core";
import {
  SortableContext,
  arrayMove,
  rectSortingStrategy,
  sortableKeyboardCoordinates,
  useSortable,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { useSearchParams } from "react-router";
import { isPositiveInteger } from "../validation";
import { withUrlBase } from "~/utils/url-base";

const USAGE_POLL_INTERVAL_MS = 10_000;

// Mirrors the camelCase JSON the backend benchmark endpoint + websocket emit.
type BenchmarkLatency = { minMs: number; avgMs: number; samples: number };
type BenchmarkSweepPoint = { connections: number; megaBytesPerSec: number; cv?: number };
type BenchmarkPipeliningPoint = { depth: number; megaBytesPerSec: number };
type BenchmarkPipelining = {
  testedAtConnections: number;
  baselineMegaBytesPerSec: number;
  tested: BenchmarkPipeliningPoint[];
  recommendEnabled: boolean;
  recommendedDepth: number;
};
type BenchmarkResult = {
  latency?: BenchmarkLatency | null;
  throughputTested: boolean;
  pipeliningOnly: boolean;
  sweep: BenchmarkSweepPoint[];
  recommendedConnections?: number | null;
  providerConnectionCap?: number | null;
  pipelining?: BenchmarkPipelining | null;
  dataUsedBytes: number;
  dataBudgetBytes?: number;
  elapsedSeconds?: number;
  confidence?: "high" | "medium" | "low";
  contentionWarnings?: string[];
  verificationRun?: boolean;
  budgetLimited?: boolean;
  stillClimbing?: boolean;
  wrappedPool?: boolean;
  warnings: string[];
};
type BenchmarkProgress = {
  phase: string;
  status: string;
  percent: number;
  currentConnections?: number | null;
  dataUsedBytes: number;
  dataBudgetBytes?: number;
  sweep: BenchmarkSweepPoint[];
  result?: BenchmarkResult | null;
  error?: string | null;
};
type BenchmarkIntensity = "quick" | "thorough";

// Mirrors backend TestUsenetConnectionResponse (BaseApiResponse + Connected), camelCase JSON.
type TestConnectionResult = {
  status?: boolean;
  connected?: boolean;
  error?: string | null;
};

// Mirrors backend BenchmarkUsenetConnectionResponse (BaseApiResponse + Result), camelCase JSON.
type BenchmarkPostResult = {
  status?: boolean;
  result?: BenchmarkResult | null;
  error?: string | null;
};

type UsenetSettingsProps = {
  config: Record<string, string>;
  savedConfig: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
  persistConfigPatch: (patch: Record<string, string>) => Promise<void>;
};

enum ProviderType {
  Disabled = 0,
  Pooled = 1,
  BackupAndStats = 2,
  BackupOnly = 3,
}

type ConnectionDetails = {
  ProviderId?: string;
  Type: ProviderType;
  Host: string;
  Port: number;
  UseSsl: boolean;
  SkipTlsVerification?: boolean;
  User: string;
  Pass: string;
  MaxConnections: number;
  MaxTransferConnections?: number | null;
  Priority?: number;
  PipeliningDepth?: number | null;
  // Optional user-set label. Shown in the UI in place of Host when present;
  // Host stays the real NNTP target. ProviderId is the stable metrics key.
  Nickname?: string;
  // Optional label for providers that share upstream storage. When one reports
  // an article missing (NNTP 430), siblings with the same label are skipped
  // for that request.
  StorageGroup?: string;
  PreviousType?: ProviderType;
  // null/0 = uncapped. Stored as bytes; the modal lets the user type a
  // friendlier MB/GB/TB value that gets converted on save.
  ByteLimit?: number | null;
  // Counter adjustment, used for "initial used" on a freshly added block
  // and zeroed on reset. Bytes.
  BytesUsedOffset?: number;
  // unix-ms cutoff. Hourly rows older than this are excluded from the live
  // usage gauge. A reset bumps this to Date.now().
  BytesUsedResetAt?: number;
};

const DEMO_PROVIDERS: ConnectionDetails[] = [
  {
    ProviderId: "demo-primary",
    Type: ProviderType.Pooled,
    Host: "news.omicron.example",
    Port: 563,
    UseSsl: true,
    User: "alice",
    Pass: "",
    MaxConnections: 40,
    Priority: 0,
    Nickname: "Primary",
    StorageGroup: "omicron",
  },
  {
    ProviderId: "demo-omicron-backup",
    Type: ProviderType.BackupAndStats,
    Host: "news.omicron.example",
    Port: 563,
    UseSsl: true,
    User: "bob",
    Pass: "",
    MaxConnections: 20,
    Priority: 1,
    Nickname: "",
    StorageGroup: "omicron",
  },
  {
    ProviderId: "demo-omicron-third",
    Type: ProviderType.Pooled,
    Host: "news2.omicron.example",
    Port: 563,
    UseSsl: true,
    User: "grace",
    Pass: "",
    MaxConnections: 15,
    Priority: 2,
    Nickname: "Omicron Two",
    StorageGroup: "omicron",
  },
  {
    ProviderId: "demo-omicron-fourth",
    Type: ProviderType.BackupOnly,
    Host: "backup2.omicron.example",
    Port: 563,
    UseSsl: true,
    User: "heidi",
    Pass: "",
    MaxConnections: 10,
    Priority: 3,
    Nickname: "",
    StorageGroup: "omicron",
  },
  {
    ProviderId: "demo-omicron-fifth",
    Type: ProviderType.Disabled,
    Host: "archive.omicron.example",
    Port: 119,
    UseSsl: false,
    User: "ivan",
    Pass: "",
    MaxConnections: 5,
    Priority: 4,
    Nickname: "Omicron Archive",
    StorageGroup: "omicron",
  },
  {
    ProviderId: "demo-eweka",
    Type: ProviderType.Pooled,
    Host: "news.eweka.example",
    Port: 563,
    UseSsl: true,
    User: "carol",
    Pass: "",
    MaxConnections: 30,
    Priority: 5,
    Nickname: "Eweka",
    StorageGroup: "eweka",
  },
  {
    ProviderId: "demo-eweka-backup",
    Type: ProviderType.BackupOnly,
    Host: "backup.eweka.example",
    Port: 563,
    UseSsl: true,
    User: "dave",
    Pass: "",
    MaxConnections: 10,
    Priority: 6,
    Nickname: "",
    StorageGroup: "eweka",
  },
  {
    ProviderId: "demo-disabled",
    Type: ProviderType.Disabled,
    Host: "news.other.example",
    Port: 119,
    UseSsl: false,
    User: "erin",
    Pass: "",
    MaxConnections: 5,
    Priority: 7,
    Nickname: "Idle",
    StorageGroup: "",
  },
  {
    ProviderId: "demo-solo",
    Type: ProviderType.Pooled,
    Host: "news.solo.example",
    Port: 563,
    UseSsl: true,
    User: "frank",
    Pass: "",
    MaxConnections: 25,
    Priority: 8,
    Nickname: "",
    StorageGroup: "",
  },
];

// camelCase matches the JSON wire format — ASP.NET Core MVC defaults to
// camelCase serialization, so we mirror that here instead of fighting it.
type ProviderUsage = {
  index: number;
  host: string;
  nickname?: string | null;
  providerId?: string | null;
  bytesUsed: number;
  byteLimit: number | null;
  overLimit: boolean;
  bytesPerDay: number;
  daysRemaining: number | null;
  learnedConnectionLimit?: number | null;
  effectiveMaxConnections?: number | null;
  configuredMaxConnections?: number | null;
  connectionBudget?: {
    configuredTransferLimit: number;
    effectiveTransferLimit: number;
    baseMetadataCapacity: number;
    metadataBurstAllowance: number;
    maxMetadataCapacity: number;
    activeTransferOperations: number;
    activeMetadataOperations: number;
    waitingTransferOperations: number;
    waitingMetadataOperations: number;
  } | null;
};

function formatDaysRemaining(days: number): string {
  // Friendlier than "0.3 days" or "847 days" — round to the unit that's
  // actually useful at this horizon.
  if (days < 1) {
    const hours = Math.max(1, Math.round(days * 24));
    return `~${hours}h left at this pace`;
  }
  if (days < 60) return `~${Math.round(days)} days left at this pace`;
  const months = days / 30;
  if (months < 24) return `~${Math.round(months)} months left at this pace`;
  return `~${Math.round(months / 12)} years left at this pace`;
}

const BYTE_UNITS = [
  { label: "MB", multiplier: 1_000_000 },
  { label: "GB", multiplier: 1_000_000_000 },
  { label: "TB", multiplier: 1_000_000_000_000 },
] as const;
type ByteUnitLabel = (typeof BYTE_UNITS)[number]["label"];

function bytesToValueAndUnit(bytes: number | null | undefined): {
  value: string;
  unit: ByteUnitLabel;
} {
  if (!bytes || bytes <= 0) return { value: "", unit: "GB" };
  // Pick the largest unit that keeps the number readable (>= 1).
  const choice = [...BYTE_UNITS].reverse().find((u) => bytes >= u.multiplier) ?? BYTE_UNITS[1];
  const v = bytes / choice.multiplier;
  // Trim trailing zeros so "500" doesn't display as "500.000".
  return { value: Number(v.toFixed(3)).toString(), unit: choice.label };
}

function valueAndUnitToBytes(value: string, unit: ByteUnitLabel): number | null {
  const trimmed = value.trim();
  if (trimmed === "") return null;
  const n = Number(trimmed);
  if (!isFinite(n) || n <= 0) return null;
  const u = BYTE_UNITS.find((x) => x.label === unit) ?? BYTE_UNITS[1];
  return Math.round(n * u.multiplier);
}

function formatBytes(bytes: number): string {
  if (!isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB", "PB"];
  let i = 0;
  let v = bytes;
  while (v >= 1000 && i < units.length - 1) {
    v /= 1000;
    i++;
  }
  return v >= 100 ? `${v.toFixed(0)} ${units[i]}` : `${v.toFixed(1)} ${units[i]}`;
}

/** Wall-clock duration for speed-test results (e.g. 72 → "1m 12s"). */
function formatElapsed(seconds: number | undefined): string | null {
  if (seconds == null || !isFinite(seconds) || seconds <= 0) return null;
  const s = Math.round(seconds);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  const rem = s % 60;
  return rem === 0 ? `${m}m` : `${m}m ${rem}s`;
}

type ConnectionCounts = {
  live: number;
  active: number;
  max: number;
};

type UsenetProviderConfig = {
  Providers: ConnectionDetails[];
};

function providerCardTone(type: ProviderType): string {
  if (type === ProviderType.Disabled) return "border-error bg-error/15";
  if (type === ProviderType.BackupOnly || type === ProviderType.BackupAndStats) {
    return "border-warning bg-base-100";
  }
  return "border-info bg-base-100";
}

type DisplayedProvider = { provider: ConnectionDetails; index: number };

type StorageGroupPartition = {
  ungrouped: DisplayedProvider[];
  groups: { name: string; items: DisplayedProvider[] }[];
};

function partitionByStorageGroup(items: DisplayedProvider[]): StorageGroupPartition {
  const ungrouped: DisplayedProvider[] = [];
  const byName = new Map<string, DisplayedProvider[]>();
  const order: string[] = [];

  for (const item of items) {
    const name = item.provider.StorageGroup?.trim() ?? "";
    if (!name) {
      ungrouped.push(item);
      continue;
    }
    let bucket = byName.get(name);
    if (!bucket) {
      bucket = [];
      byName.set(name, bucket);
      order.push(name);
    }
    bucket.push(item);
  }

  return {
    ungrouped,
    groups: order.map((name) => ({ name, items: byName.get(name)! })),
  };
}

function parseProviderConfig(jsonString: string): UsenetProviderConfig {
  try {
    if (!jsonString || jsonString.trim() === "") {
      return { Providers: [] };
    }
    // Config key "usenet.providers" holds the backend UsenetProviderConfig JSON.
    const parsed = JSON.parse(jsonString) as UsenetProviderConfig | null;
    return parsed && Array.isArray(parsed.Providers) ? parsed : { Providers: [] };
  } catch {
    return { Providers: [] };
  }
}

function serializeProviderConfig(config: UsenetProviderConfig): string {
  return JSON.stringify(config);
}

function providerKey(p: ConnectionDetails): string {
  return `${p.Host}::${p.Port}::${p.User}`;
}

// Match UsenetProviderIdentity.MetricsKey (Guid "N"): strip dashes, lowercase.
// Demo ids like "demo-primary" are unchanged aside from case.
function normalizeProviderId(id: string): string {
  return id.replace(/-/g, "").toLowerCase();
}

function providerIdentity(p: ConnectionDetails): string {
  return p.ProviderId ? normalizeProviderId(p.ProviderId) : providerKey(p);
}

function usagePollIdentityKey(item: ProviderUsage, providers: ConnectionDetails[]): string {
  if (item.providerId) return normalizeProviderId(item.providerId);
  const nickname = item.nickname?.trim() ?? "";
  const match = providers.find(
    (p) => p.Host === item.host && (p.Nickname?.trim() ?? "") === nickname,
  );
  if (match) return providerIdentity(match);
  return `${item.host}::${nickname}`;
}

function generateProviderId(): string {
  return generateUuid();
}

type DragBits = {
  setNodeRef: (node: HTMLElement | null) => void;
  setActivatorNodeRef: (node: HTMLElement | null) => void;
  attributes: DraggableAttributes;
  listeners: DraggableSyntheticListeners;
  style: CSSProperties;
  isDragging: boolean;
};

function SortableItem({
  id,
  disabled,
  children,
}: {
  id: string;
  disabled: boolean;
  children: (drag: DragBits) => ReactNode;
}) {
  const {
    setNodeRef,
    setActivatorNodeRef,
    attributes,
    listeners,
    transform,
    transition,
    isDragging,
  } = useSortable({ id, disabled });
  const style: CSSProperties = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.6 : 1,
    zIndex: isDragging ? 2 : undefined,
  };
  return (
    <>{children({ setNodeRef, setActivatorNodeRef, attributes, listeners, style, isDragging })}</>
  );
}

export function UsenetSettings({
  config,
  savedConfig,
  setNewConfig,
  persistConfigPatch,
}: UsenetSettingsProps) {
  // state
  const [searchParams] = useSearchParams();
  const isDemoPreview = searchParams.get("demoProviders") === "1";
  const [showModal, setShowModal] = useState(false);
  const [resetUsageIndex, setResetUsageIndex] = useState<number | null>(null);
  const [editingIndex, setEditingIndex] = useState<number | null>(null);
  const [connections, setConnections] = useState<Record<string, ConnectionCounts>>({});
  const [usage, setUsage] = useState<Record<string, ProviderUsage>>({});
  const providersJson = config["usenet.providers"] ?? "";
  const providerConfig = useMemo(() => parseProviderConfig(providersJson), [providersJson]);
  const savedProviderConfig = useMemo(
    () => parseProviderConfig(savedConfig["usenet.providers"] ?? ""),
    [savedConfig],
  );
  const displayedProviderConfig = useMemo(
    () => (isDemoPreview ? { Providers: DEMO_PROVIDERS } : providerConfig),
    [isDemoPreview, providerConfig],
  );
  const cascadeEnabled = config["usenet.cascade.enabled"] === "true";

  // Display-sort by type then priority when cascade is off. Cascade mode keeps
  // array/drag order so dnd-kit and #N badges stay coherent. Mutations still
  // use the original config index — this never rewrites persisted order.
  const displayedProviders = useMemo(() => {
    const items = displayedProviderConfig.Providers.map((provider, index) => ({ provider, index }));
    if (cascadeEnabled) return items;
    return items.sort((a, b) => {
      const getGroup = (type: ProviderType) => {
        if (type === ProviderType.Pooled) return 0;
        if (type === ProviderType.BackupAndStats || type === ProviderType.BackupOnly) return 1;
        return 2;
      };
      const groupDiff = getGroup(a.provider.Type) - getGroup(b.provider.Type);
      if (groupDiff !== 0) return groupDiff;
      const prioDiff = (a.provider.Priority ?? 0) - (b.provider.Priority ?? 0);
      if (prioDiff !== 0) return prioDiff;
      return a.index - b.index;
    });
  }, [displayedProviderConfig.Providers, cascadeEnabled]);

  const storagePartitions = useMemo(
    () => partitionByStorageGroup(displayedProviders),
    [displayedProviders],
  );

  const existingStorageGroups = useMemo(
    () =>
      [
        ...new Set(
          providerConfig.Providers.map((p) => p.StorageGroup?.trim() ?? "").filter(Boolean),
        ),
      ].sort((a, b) => a.localeCompare(b)),
    [providerConfig.Providers],
  );

  // handlers
  const handleAddProvider = useCallback(() => {
    setEditingIndex(null);
    setShowModal(true);
  }, []);

  const handleEditProvider = useCallback((index: number) => {
    setEditingIndex(index);
    setShowModal(true);
  }, []);

  const handleDeleteProvider = useCallback(
    (index: number) => {
      const newProviderConfig = { ...providerConfig };
      newProviderConfig.Providers = providerConfig.Providers.filter((_, i) => i !== index);
      setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    },
    [config, providerConfig, setNewConfig],
  );

  const handleToggleProvider = useCallback(
    (index: number) => {
      const current = providerConfig.Providers[index];
      if (!current) return;
      const isDisabled = current.Type === ProviderType.Disabled;
      // When re-enabling, clear PreviousType by omitting it (JSON.stringify drops undefined,
      // so this matches the prior runtime behavior). When disabling, store the current type.
      const { PreviousType: _previousType, ...rest } = current;
      const updated: ConnectionDetails = isDisabled
        ? { ...rest, Type: current.PreviousType ?? ProviderType.Pooled }
        : { ...current, Type: ProviderType.Disabled, PreviousType: current.Type };
      const newProviderConfig = { ...providerConfig };
      newProviderConfig.Providers = providerConfig.Providers.map((p, i) =>
        i === index ? updated : p,
      );
      setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    },
    [config, providerConfig, setNewConfig],
  );

  const handleResetUsage = useCallback(
    (index: number) => {
      const current = providerConfig.Providers[index];
      if (!current) return;
      const updated: ConnectionDetails = {
        ...current,
        BytesUsedOffset: 0,
        BytesUsedResetAt: Date.now(),
      };
      const newProviderConfig = { ...providerConfig };
      newProviderConfig.Providers = providerConfig.Providers.map((p, i) =>
        i === index ? updated : p,
      );
      setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
      setResetUsageIndex(null);
    },
    [config, providerConfig, setNewConfig],
  );

  const handleCloseModal = useCallback(() => {
    setShowModal(false);
    setEditingIndex(null);
  }, []);

  const handleSaveProvider = useCallback(
    async (provider: ConnectionDetails) => {
      const providers = [...providerConfig.Providers];
      if (editingIndex !== null) {
        providers[editingIndex] = provider;
      } else {
        providers.push({
          ...provider,
          ProviderId: provider.ProviderId || generateProviderId(),
          Priority: providers.length,
        });
      }
      const patch: Record<string, string> = {
        "usenet.providers": serializeProviderConfig({ ...providerConfig, Providers: providers }),
        // Include current draft so a speed-test "Apply" pipelining change persists with the provider.
        "usenet.queue-pipelining.enabled": config["usenet.queue-pipelining.enabled"] ?? "false",
      };
      await persistConfigPatch(patch);
      handleCloseModal();
    },
    [config, providerConfig, editingIndex, persistConfigPatch, handleCloseModal],
  );

  const handleApplyPipelining = useCallback(
    (enabled: boolean) => {
      setNewConfig((prev) => ({
        ...prev,
        "usenet.queue-pipelining.enabled": enabled ? "true" : "false",
      }));
    },
    [setNewConfig],
  );

  const handleReorder = useCallback(
    (from: number, to: number) => {
      if (from === to) return;
      const providers = arrayMove(providerConfig.Providers, from, to).map((p, i) => ({
        ...p,
        Priority: i,
      }));
      setNewConfig({
        ...config,
        "usenet.providers": serializeProviderConfig({ ...providerConfig, Providers: providers }),
      });
    },
    [config, providerConfig, setNewConfig],
  );

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const handleDragEnd = useCallback(
    (event: DragEndEvent) => {
      const { active, over } = event;
      if (!over || active.id === over.id) return;
      const ids = providerConfig.Providers.map(providerKey);
      const from = ids.indexOf(String(active.id));
      const to = ids.indexOf(String(over.id));
      if (from !== -1 && to !== -1) handleReorder(from, to);
    },
    [providerConfig, handleReorder],
  );

  const handleConnectionsMessage = useCallback(
    (message: string) => {
      const parts = (message || "0|0|0|0|1|0").split("|");
      const [index, live = 0, idle = 0, _0, _1, _2] = parts.map((x) => Number(x));
      if (showModal) return;
      if (index === undefined) return;
      const savedProvider = savedProviderConfig.Providers[index];
      if (!savedProvider) return;
      const identity = providerIdentity(savedProvider);
      setConnections((prev) => ({
        ...prev,
        [identity]: {
          active: live - idle,
          live: live,
          max: savedProvider.MaxConnections || 1,
        },
      }));
    },
    [showModal, savedProviderConfig.Providers],
  );

  useWebsocketTopic("cxs", "state", handleConnectionsMessage, {
    onClose: () => setConnections({}),
  });

  // Poll provider usage. Backend computes "bytes since reset + offset" from
  // the persisted hourly rollup plus the in-memory tracker; cheap enough to
  // hit on a 10s tick. We skip while the edit modal is open since the user
  // may be mid-edit and we don't want the card behind the modal flickering.
  useEffect(() => {
    let disposed = false;
    async function fetchUsage() {
      try {
        const response = await fetch(withUrlBase("/api/get-provider-usage"));
        if (!response.ok || disposed) return;
        // Response of GET /api/get-provider-usage (backend GetProviderUsageResponse).
        const data = (await response.json()) as { providers?: ProviderUsage[] };
        if (disposed || !data.providers) return;
        const next: Record<string, ProviderUsage> = {};
        for (const p of data.providers) {
          next[usagePollIdentityKey(p, providerConfig.Providers)] = p;
        }
        setUsage(next);
      } catch {
        // network blips are fine — next tick retries.
      }
    }
    void fetchUsage(); // fire-and-forget: fetchUsage swallows its own errors
    if (showModal)
      return () => {
        disposed = true;
      };
    const id = setInterval(() => void fetchUsage(), USAGE_POLL_INTERVAL_MS);
    return () => {
      disposed = true;
      clearInterval(id);
    };
  }, [showModal, providerConfig.Providers]);

  const renderProviderCard = (provider: ConnectionDetails, index: number) => {
    const isDisabled = provider.Type === ProviderType.Disabled;
    const displayName = provider.Nickname?.trim() || provider.Host;
    const liveConnections = isDemoPreview
      ? 0
      : (connections[providerIdentity(provider)]?.live ?? 0);
    const providerUsage = isDemoPreview ? undefined : usage[providerIdentity(provider)];
    const learnedLimit = providerUsage?.learnedConnectionLimit;
    const effectiveMax = providerUsage?.effectiveMaxConnections ?? provider.MaxConnections;
    const configuredBudget =
      provider.MaxTransferConnections == null
        ? null
        : calculateProviderConnectionBudget(
            provider.MaxConnections,
            provider.MaxTransferConnections,
          );
    const liveBudget = providerUsage?.connectionBudget ?? null;
    const displayedTransferLimit =
      liveBudget?.effectiveTransferLimit ?? configuredBudget?.transferLimit;
    const displayedMetadataBudget = liveBudget ?? configuredBudget;

    return (
      <SortableItem
        key={providerKey(provider)}
        id={providerKey(provider)}
        disabled={!cascadeEnabled || isDemoPreview}
      >
        {({ setNodeRef, setActivatorNodeRef, attributes, listeners, style, isDragging }) => (
          <div
            ref={setNodeRef}
            style={style}
            className={`w-full max-w-md overflow-hidden rounded-lg border ${providerCardTone(provider.Type)}`}
          >
            <div className="space-y-3 p-3.5">
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0 flex-1">
                  <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
                    {cascadeEnabled && !isDisabled && (
                      <Badge className="badge-ghost badge-sm shrink-0">#{index + 1}</Badge>
                    )}
                    <span className="min-w-0 break-all text-sm font-semibold leading-snug text-base-content">
                      {displayName}
                    </span>
                    {isDisabled && (
                      <Badge className="badge-ghost badge-sm shrink-0">Disabled</Badge>
                    )}
                  </div>
                  <div className="mt-0.5 break-all text-xs text-base-content/60">
                    {provider.User
                      ? `${provider.User}@${provider.Host}:${provider.Port}`
                      : `${provider.Host}:${provider.Port}`}
                  </div>
                </div>
                <div className="flex shrink-0 gap-0.5">
                  {cascadeEnabled && (
                    <button
                      type="button"
                      ref={setActivatorNodeRef}
                      className="btn btn-ghost btn-sm btn-square"
                      style={{ cursor: isDragging ? "grabbing" : "grab", touchAction: "none" }}
                      title="Drag to reorder"
                      aria-label="Drag to reorder"
                      disabled={isDemoPreview}
                      {...attributes}
                      {...listeners}
                    >
                      <svg
                        width="14"
                        height="14"
                        viewBox="0 0 24 24"
                        fill="currentColor"
                        aria-hidden="true"
                      >
                        <circle cx="9" cy="5" r="1.6" />
                        <circle cx="15" cy="5" r="1.6" />
                        <circle cx="9" cy="12" r="1.6" />
                        <circle cx="15" cy="12" r="1.6" />
                        <circle cx="9" cy="19" r="1.6" />
                        <circle cx="15" cy="19" r="1.6" />
                      </svg>
                    </button>
                  )}
                  <button
                    type="button"
                    className={`btn btn-ghost btn-sm btn-square ${isDisabled ? "text-base-content/40" : "text-success"}`}
                    onClick={() => handleToggleProvider(index)}
                    title={
                      isDemoPreview
                        ? "Disabled in demo preview"
                        : isDisabled
                          ? "Enable Provider"
                          : "Disable Provider"
                    }
                    aria-pressed={!isDisabled}
                    disabled={isDemoPreview}
                  >
                    <svg
                      width="14"
                      height="14"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="2"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                    >
                      <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                      <line x1="12" y1="2" x2="12" y2="12" />
                    </svg>
                  </button>
                  <button
                    type="button"
                    className="btn btn-ghost btn-sm btn-square"
                    onClick={() => handleEditProvider(index)}
                    title={isDemoPreview ? "Disabled in demo preview" : "Edit Provider"}
                    disabled={isDemoPreview}
                  >
                    <Icon name="edit" className="!text-[14px]" />
                  </button>
                  <button
                    type="button"
                    className="btn btn-ghost btn-sm btn-square hover:text-error"
                    onClick={() => handleDeleteProvider(index)}
                    title={isDemoPreview ? "Disabled in demo preview" : "Delete Provider"}
                    disabled={isDemoPreview}
                  >
                    <Icon name="delete" className="!text-[14px]" />
                  </button>
                </div>
              </div>

              <div className="border-t border-base-content/10 pt-2.5">
                <div>
                  <ProviderCardMeta
                    icon="hub"
                    label="Connections"
                    value={`${liveConnections} / ${effectiveMax} max`}
                    emphasize
                  />
                  {learnedLimit != null && (
                    <div className="mt-1.5 flex items-center gap-1.5 text-[11px] text-warning">
                      <Icon name="warning" className="!text-[13px] shrink-0" />
                      <span>
                        Provider caps at {learnedLimit} — runtime capacities use this lower limit
                      </span>
                    </div>
                  )}
                  {displayedTransferLimit != null && displayedMetadataBudget && (
                    <div className="mt-2.5 grid grid-cols-2 gap-2.5">
                      <ProviderCardMeta
                        icon="download"
                        label="Transfer Connections"
                        value={`${displayedTransferLimit} max`}
                      />
                      <ProviderCardMeta
                        icon="query_stats"
                        label="Metadata Capacity"
                        value={formatMetadataCapacity(displayedMetadataBudget)}
                      />
                    </div>
                  )}
                  {provider.MaxTransferConnections == null && (
                    <div className="mt-1.5 text-[11px] text-base-content/50">
                      Legacy shared-pool connection scheduling
                    </div>
                  )}
                </div>

                <UsageRow
                  provider={provider}
                  usage={providerUsage}
                  onReset={() => setResetUsageIndex(index)}
                  resetDisabled={isDemoPreview}
                />
              </div>
            </div>
          </div>
        )}
      </SortableItem>
    );
  };

  // view
  return (
    <SettingsPage>
      <SettingsIntro>
        Configure NNTP providers, decide whether they share load or cascade in priority order, and
        tune pipelining for faster queue first-segment fetches.
      </SettingsIntro>

      <ManagedSetting
        configKeys={[
          "usenet.cascade.enabled",
          "usenet.cascade.retry-primary-on-miss",
          "usenet.queue-pipelining.enabled",
          "usenet.queue-pipelining.depth",
          "usenet.article-miss-cache-ttl-seconds",
          "usenet.article-miss-cache-max-entries",
        ]}
      >
        <section className="rounded-lg border border-base-content/10 bg-base-100 px-3 py-2.5">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:gap-6">
            <div className="flex shrink-0 items-center gap-1.5 text-base-content/60">
              <Icon name="tune" className="!text-[16px]" />
              <span className="text-[10px] font-semibold uppercase tracking-wide">Global</span>
            </div>

            <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-2">
              <Tooltip
                content="Prefer providers in drag order. While off, all enabled providers share work in the pool. Thinly-spared primaries (at most 25% of their pool free) yield to idler same-tier peers; larger Provider Connection Limits alone do not outrank priority."
                className="min-w-0 tooltip-start"
              >
                <Toggle
                  id="cascade-enabled"
                  className="min-w-0 cursor-pointer gap-2 p-0"
                  checked={cascadeEnabled}
                  onChange={(e) => {
                    const enabling = e.target.checked;
                    const needsSeed =
                      enabling && providerConfig.Providers.every((p) => !p.Priority);
                    const providers = needsSeed
                      ? providerConfig.Providers.map((p, i) => ({ ...p, Priority: i }))
                      : providerConfig.Providers;
                    setNewConfig({
                      ...config,
                      "usenet.cascade.enabled": enabling ? "true" : "false",
                      "usenet.providers": serializeProviderConfig({
                        ...providerConfig,
                        Providers: providers,
                      }),
                    });
                  }}
                  label={<span className="text-sm text-base-content">Cascade routing</span>}
                />
              </Tooltip>
              <Tooltip content="After a clean 430/451 on the first batch attempt, try the primary once more before cascading. Helps multi-node spool routing; turn off to skip straight to backups. Skipped automatically when the article-miss cache already knows the primary is missing.">
                <Toggle
                  id="cascade-retry-primary-on-miss"
                  className={`gap-2 p-0 ${cascadeEnabled ? "cursor-pointer" : "cursor-not-allowed opacity-60"}`}
                  disabled={!cascadeEnabled}
                  checked={(config["usenet.cascade.retry-primary-on-miss"] ?? "true") !== "false"}
                  onChange={(e) =>
                    setNewConfig({
                      ...config,
                      "usenet.cascade.retry-primary-on-miss": e.target.checked ? "true" : "false",
                    })
                  }
                  label={<span className="text-sm text-base-content">Re-probe primary</span>}
                />
              </Tooltip>
            </div>

            <div
              className="hidden h-4 w-px shrink-0 bg-base-content/10 lg:block"
              aria-hidden="true"
            />

            <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-2">
              <Tooltip
                className="tooltip-start"
                content="Batch first-segment BODY requests during queue imports and provider Auto-tune benchmarks. Does not affect WebDAV playback — streaming batching lives under Settings → Streaming."
              >
                <Toggle
                  id="pipelining-enabled"
                  className="cursor-pointer gap-2 p-0"
                  checked={config["usenet.queue-pipelining.enabled"] === "true"}
                  onChange={(e) =>
                    setNewConfig({
                      ...config,
                      "usenet.queue-pipelining.enabled": e.target.checked ? "true" : "false",
                    })
                  }
                  label={<span className="text-sm text-base-content">Queue pipelining</span>}
                />
              </Tooltip>
              <Tooltip content="Requests kept in flight per connection during queue imports (1–64). 8 is a good default. Each provider can override this.">
                <div className="flex flex-col items-start gap-1">
                  <Label
                    htmlFor="pipelining-depth"
                    className="mb-0 shrink-0 text-[11px] text-base-content/50"
                  >
                    Queue depth
                  </Label>
                  <Input
                    type="text"
                    id="pipelining-depth"
                    className={`input-sm w-16 ${config["usenet.queue-pipelining.depth"] !== undefined && config["usenet.queue-pipelining.depth"] !== "" && !isPositiveInteger(config["usenet.queue-pipelining.depth"]) ? "input-error" : ""}`}
                    placeholder="8"
                    value={config["usenet.queue-pipelining.depth"] ?? ""}
                    onChange={(e) =>
                      setNewConfig({ ...config, "usenet.queue-pipelining.depth": e.target.value })
                    }
                  />
                </div>
              </Tooltip>
            </div>

            <div
              className="hidden h-4 w-px shrink-0 bg-base-content/10 lg:block"
              aria-hidden="true"
            />

            <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-2">
              <Tooltip
                className="tooltip-start"
                content="After a provider (or storage group) reports a definitive article miss (430/451), skip re-probing that provider for the same article until the TTL expires. Default 300s (30–86400)."
              >
                <div className="flex flex-col items-start gap-1">
                  <Label
                    htmlFor="article-miss-cache-ttl"
                    className="mb-0 shrink-0 text-[11px] text-base-content/50"
                  >
                    Miss TTL
                  </Label>
                  <Input
                    type="text"
                    id="article-miss-cache-ttl"
                    className={`input-sm w-16 ${config["usenet.article-miss-cache-ttl-seconds"] !== undefined && config["usenet.article-miss-cache-ttl-seconds"] !== "" && !isArticleMissCacheTtl(config["usenet.article-miss-cache-ttl-seconds"]) ? "input-error" : ""}`}
                    placeholder="300"
                    value={config["usenet.article-miss-cache-ttl-seconds"] ?? ""}
                    onChange={(e) =>
                      setNewConfig({
                        ...config,
                        "usenet.article-miss-cache-ttl-seconds": e.target.value,
                      })
                    }
                  />
                </div>
              </Tooltip>
              <Tooltip
                className="tooltip-start"
                content="Max negative-cache entries before oldest are evicted. Default 10000 (100–1000000)."
              >
                <div className="flex flex-col items-start gap-1">
                  <Label
                    htmlFor="article-miss-cache-max"
                    className="mb-0 shrink-0 text-[11px] text-base-content/50"
                  >
                    Miss max
                  </Label>
                  <Input
                    type="text"
                    id="article-miss-cache-max"
                    className={`input-sm w-20 ${config["usenet.article-miss-cache-max-entries"] !== undefined && config["usenet.article-miss-cache-max-entries"] !== "" && !isArticleMissCacheMaxEntries(config["usenet.article-miss-cache-max-entries"]) ? "input-error" : ""}`}
                    placeholder="10000"
                    value={config["usenet.article-miss-cache-max-entries"] ?? ""}
                    onChange={(e) =>
                      setNewConfig({
                        ...config,
                        "usenet.article-miss-cache-max-entries": e.target.value,
                      })
                    }
                  />
                </div>
              </Tooltip>
            </div>
          </div>
        </section>
      </ManagedSetting>

      <ManagedSetting configKey="usenet.providers">
        <section className="space-y-3">
          <div className="flex items-end justify-between gap-4">
            <div>
              <h2 className="text-lg font-semibold text-base-content">Providers</h2>
              <p className="mt-1 text-xs leading-relaxed text-base-content/50">
                Add Usenet accounts, monitor connection usage, and edit credentials.
              </p>
            </div>
            <div className="flex items-center gap-2">
              <span className="badge badge-ghost badge-sm shrink-0">
                {displayedProviderConfig.Providers.length}{" "}
                {displayedProviderConfig.Providers.length === 1 ? "provider" : "providers"}
              </span>
              <Button
                variant="primary"
                size="small"
                onClick={handleAddProvider}
                disabled={isDemoPreview}
                title={isDemoPreview ? "Disabled in demo preview" : undefined}
              >
                <Icon name="add" className="!text-[18px]" />
                Add
              </Button>
            </div>
          </div>

          {isDemoPreview && (
            <Alert variant="info">
              Demo providers (preview only). Provider actions are disabled and demo data is never
              saved.
            </Alert>
          )}

          {displayedProviderConfig.Providers.length === 0 ? (
            <div className="rounded-lg border border-dashed border-base-content/15 bg-base-200/20 px-4 py-8 text-center">
              <Icon name="cloud_off" className="!text-[28px] text-base-content/35" />
              <p className="mt-2 text-sm text-base-content/55">No Usenet providers configured.</p>
              <p className="mt-1 text-xs text-base-content/40">
                Click Add to connect your first NNTP account.
              </p>
            </div>
          ) : (
            <DndContext
              sensors={sensors}
              collisionDetection={closestCenter}
              onDragEnd={handleDragEnd}
            >
              <SortableContext
                items={displayedProviderConfig.Providers.map(providerKey)}
                strategy={rectSortingStrategy}
              >
                <div className="space-y-3">
                  {storagePartitions.ungrouped.length > 0 && (
                    <div className="flex max-w-full flex-wrap gap-3">
                      {storagePartitions.ungrouped.map(({ provider, index }) =>
                        renderProviderCard(provider, index),
                      )}
                    </div>
                  )}
                  {storagePartitions.groups.map(({ name, items }) => (
                    <div
                      key={name}
                      className="w-full rounded-lg bg-gradient-to-br from-primary via-info to-success p-px"
                    >
                      <div className="space-y-2 rounded-[calc(var(--radius-box)-1px)] bg-base-200/90 p-2.5">
                        <div className="flex items-center gap-2 px-1">
                          <Icon name="storage" className="!text-[16px] text-secondary" />
                          <span className="text-xs font-semibold uppercase tracking-wide text-base-content/80">
                            {name}
                          </span>
                          <span className="badge badge-ghost badge-sm">
                            {items.length} {items.length === 1 ? "provider" : "providers"}
                          </span>
                        </div>
                        <div className="flex max-w-full flex-wrap gap-2">
                          {items.map(({ provider, index }) => renderProviderCard(provider, index))}
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              </SortableContext>
            </DndContext>
          )}
        </section>
      </ManagedSetting>

      <ProviderModal
        show={showModal}
        provider={editingIndex !== null ? (providerConfig.Providers[editingIndex] ?? null) : null}
        existingStorageGroups={existingStorageGroups}
        onClose={handleCloseModal}
        onSave={handleSaveProvider}
        onApplyPipelining={handleApplyPipelining}
        defaultPipeliningDepth={config["usenet.queue-pipelining.depth"] || "8"}
      />
      <ConfirmModal
        show={resetUsageIndex !== null}
        title="Reset data-cap counter?"
        message={
          resetUsageIndex !== null
            ? `Reset the bytes-used counter for "${providerConfig.Providers[resetUsageIndex]?.Nickname?.trim() || providerConfig.Providers[resetUsageIndex]?.Host}" to zero? This only rewinds the gauge for this provider's data cap. Historical metrics and graphs are untouched. Takes effect after you save settings.`
            : ""
        }
        confirmText="Reset counter"
        cancelText="Cancel"
        onCancel={() => setResetUsageIndex(null)}
        onConfirm={() => {
          if (resetUsageIndex !== null) handleResetUsage(resetUsageIndex);
        }}
      />
    </SettingsPage>
  );
}

type UsageRowProps = {
  provider: ConnectionDetails;
  usage: ProviderUsage | undefined;
  onReset: () => void;
  resetDisabled?: boolean;
};

function ProviderCardMeta({
  icon,
  label,
  value,
  emphasize = false,
}: {
  icon: string;
  label: string;
  value: string;
  emphasize?: boolean;
}) {
  if (emphasize) {
    return (
      <div className="flex items-center gap-2.5 rounded-lg border border-base-content/10 bg-base-200/40 px-2.5 py-2">
        <div className="flex h-[26px] w-[26px] shrink-0 items-center justify-center rounded-md bg-base-300 text-base-content/60">
          <Icon name={icon} className="!text-[16px]" />
        </div>
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <span className="text-[10px] font-medium uppercase tracking-wide text-base-content/50">
            {label}
          </span>
          <span className="truncate text-sm font-medium tabular-nums text-base-content">
            {value}
          </span>
        </div>
      </div>
    );
  }

  return (
    <div className="relative flex min-w-0 items-center gap-2">
      <div className="text-primary">
        <Icon name={icon} className="!text-[18px]" />
      </div>
      <div className="flex min-w-0 flex-col">
        <span className="text-[11px] uppercase tracking-wide text-base-content/50">{label}</span>
        <span className="truncate text-sm text-base-content">{value}</span>
      </div>
    </div>
  );
}

function UsageRow({ provider, usage, onReset, resetDisabled = false }: UsageRowProps) {
  const limit = provider.ByteLimit ?? null;
  const used = usage?.bytesUsed ?? 0;
  const hasLimit = limit !== null && limit > 0;
  const pct = hasLimit ? Math.min(100, (used / limit) * 100) : 0;
  // Thresholds match the soft-warning levels the backend would alert on if
  // we wired notifications. Keeping the same numbers here means the colors
  // tell the same story as any future alert email or webhook.
  const tone = hasLimit
    ? pct >= 100
      ? "danger"
      : pct >= 95
        ? "danger"
        : pct >= 80
          ? "warn"
          : "ok"
    : "neutral";

  const valueToneClass =
    tone === "danger" ? "text-error" : tone === "warn" ? "text-warning" : "text-base-content";
  const progressToneClass =
    tone === "danger"
      ? "progress-error"
      : tone === "warn"
        ? "progress-warning"
        : tone === "ok"
          ? "progress-success"
          : "progress-primary";

  return (
    <div className="mt-2.5 flex flex-col gap-1.5 rounded-lg border border-base-content/10 bg-base-200/40 p-2.5">
      <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1">
        <span className="shrink-0 text-[10px] font-medium uppercase tracking-wide text-base-content/50">
          {hasLimit ? "Data Cap" : "Data Used"}
        </span>
        <span className={`min-w-0 flex-1 text-xs font-semibold tabular-nums ${valueToneClass}`}>
          {hasLimit
            ? `${formatBytes(used)} / ${formatBytes(limit)} · ${pct.toFixed(1)}%`
            : formatBytes(used)}
        </span>
        {hasLimit &&
          usage &&
          usage.daysRemaining !== null &&
          usage.daysRemaining !== undefined &&
          !usage.overLimit && (
            <span className="text-[11px] tabular-nums text-base-content/50">
              {formatDaysRemaining(usage.daysRemaining)}
            </span>
          )}
        <button
          type="button"
          className="btn btn-ghost btn-xs"
          onClick={onReset}
          title={
            resetDisabled
              ? "Disabled in demo preview"
              : "Reset the counter to zero (e.g. after buying a new block)"
          }
          disabled={resetDisabled}
        >
          Reset
        </button>
      </div>
      {hasLimit ? (
        <progress className={`progress h-1.5 w-full ${progressToneClass}`} value={pct} max={100} />
      ) : (
        <progress
          className="progress progress-neutral h-1.5 w-full"
          value={0}
          max={100}
          aria-label="Data used (no cap)"
        />
      )}
      {usage?.overLimit && (
        <div className="text-[11px] leading-snug text-error">
          Data cap reached. This provider is paused to keep in-flight fetches from overshooting.
          Reset the counter or raise the cap to resume.
        </div>
      )}
    </div>
  );
}

type ProviderModalProps = {
  show: boolean;
  provider: ConnectionDetails | null;
  existingStorageGroups: string[];
  onClose: () => void;
  onSave: (provider: ConnectionDetails) => void | Promise<void>;
  onApplyPipelining: (enabled: boolean) => void;
  defaultPipeliningDepth: string;
};

function ProviderModal({
  show,
  provider,
  existingStorageGroups,
  onClose,
  onSave,
  onApplyPipelining,
  defaultPipeliningDepth,
}: ProviderModalProps) {
  const isEditing = provider !== null;
  const initialLimit = bytesToValueAndUnit(provider?.ByteLimit);
  const initialUsed = bytesToValueAndUnit(provider?.BytesUsedOffset);

  const [nickname, setNickname] = useState(provider?.Nickname || "");
  const [storageGroup, setStorageGroup] = useState(provider?.StorageGroup || "");
  const [storageGroupOpen, setStorageGroupOpen] = useState(false);
  const [host, setHost] = useState(provider?.Host || "");
  const [port, setPort] = useState(provider?.Port?.toString() || "563");
  const [useSsl, setUseSsl] = useState(provider?.UseSsl ?? true);
  const [skipTlsVerification, setSkipTlsVerification] = useState(
    provider?.SkipTlsVerification ?? false,
  );
  const [user, setUser] = useState(provider?.User || "");
  const [pass, setPass] = useState(provider?.Pass || "");
  const [maxConnections, setMaxConnections] = useState(
    provider?.MaxConnections?.toString() || "20",
  );
  const [maxTransferConnections, setMaxTransferConnections] = useState(
    provider?.MaxTransferConnections?.toString() || "",
  );
  const [pipeliningDepth, setPipeliningDepth] = useState(
    provider?.PipeliningDepth?.toString() || "",
  );
  const [type, setType] = useState<ProviderType>(provider?.Type ?? ProviderType.Pooled);
  const [limitValue, setLimitValue] = useState(initialLimit.value);
  const [limitUnit, setLimitUnit] = useState<ByteUnitLabel>(initialLimit.unit);
  const [initialUsedValue, setInitialUsedValue] = useState(initialUsed.value);
  const [initialUsedUnit, setInitialUsedUnit] = useState<ByteUnitLabel>(initialUsed.unit);
  const [isTestingConnection, setIsTestingConnection] = useState(false);
  const [connectionTested, setConnectionTested] = useState(false);
  const [testError, setTestError] = useState<string | null>(null);
  const [intensity, setIntensity] = useState<BenchmarkIntensity>("quick");
  const [dataBudget, setDataBudget] = useState<string>("");
  const [isBenchmarking, setIsBenchmarking] = useState(false);
  const [benchmarkProgress, setBenchmarkProgress] = useState<BenchmarkProgress | null>(null);
  const [benchmarkResult, setBenchmarkResult] = useState<BenchmarkResult | null>(null);
  const [benchmarkError, setBenchmarkError] = useState<string | null>(null);
  const [pipeliningOnly, setPipeliningOnly] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const benchmarkAbortRef = useRef<AbortController | null>(null);
  const passIsMasked = isMaskedSecret(pass);
  // Stable across parent re-parses of the same provider so Apply transfer recommendation
  // (and other dirty-config updates) don't wipe in-progress form state.
  const providerIdentityKey = provider ? providerIdentity(provider) : "new";

  // Reset form when modal opens or a different provider is selected
  useEffect(() => {
    if (show) {
      const lim = bytesToValueAndUnit(provider?.ByteLimit);
      const used = bytesToValueAndUnit(provider?.BytesUsedOffset);
      setNickname(provider?.Nickname || "");
      setStorageGroup(provider?.StorageGroup || "");
      setStorageGroupOpen(false);
      setHost(provider?.Host || "");
      setPort(provider?.Port?.toString() || "563");
      setUseSsl(provider?.UseSsl ?? true);
      setSkipTlsVerification(provider?.SkipTlsVerification ?? false);
      setUser(provider?.User || "");
      setPass(provider?.Pass || "");
      setMaxConnections(provider?.MaxConnections?.toString() || "20");
      setMaxTransferConnections(provider?.MaxTransferConnections?.toString() || "");
      setPipeliningDepth(provider?.PipeliningDepth?.toString() || "");
      setType(provider?.Type ?? ProviderType.Pooled);
      setLimitValue(lim.value);
      setLimitUnit(lim.unit);
      setInitialUsedValue(used.value);
      setInitialUsedUnit(used.unit);
      setConnectionTested(false);
      setTestError(null);
      setIntensity("quick");
      setDataBudget("");
      setIsBenchmarking(false);
      setBenchmarkProgress(null);
      setBenchmarkResult(null);
      setBenchmarkError(null);
      setPipeliningOnly(false);
      setIsSaving(false);
      setSaveError(null);
    }
    // Intentionally keyed on providerIdentity, not provider object identity —
    // parent config updates re-parse providers and would otherwise reset the form.
    // eslint-disable-next-line react-hooks/exhaustive-deps -- provider fields read when identity/show change
  }, [show, providerIdentityKey]);

  // Stop any in-flight speed test when the modal closes or unmounts so it
  // aborts on the backend and frees its connections immediately.
  useEffect(() => {
    if (!show) {
      benchmarkAbortRef.current?.abort();
      void fetch(withUrlBase("/api/benchmark-usenet-connection"), {
        method: "POST",
        body: (() => {
          const f = new FormData();
          f.append("cancel", "true");
          return f;
        })(),
      }).catch(() => {
        /* best-effort cancel */
      });
    }
  }, [show]);
  useEffect(
    () => () => {
      benchmarkAbortRef.current?.abort();
      void fetch(withUrlBase("/api/benchmark-usenet-connection"), {
        method: "POST",
        body: (() => {
          const f = new FormData();
          f.append("cancel", "true");
          return f;
        })(),
      }).catch(() => {
        /* best-effort cancel */
      });
    },
    [],
  );

  const handleTestConnection = useCallback(async () => {
    setIsTestingConnection(true);
    setConnectionTested(false);
    setTestError(null);

    try {
      const formData = new FormData();
      formData.append("host", host);
      formData.append("port", port);
      formData.append("use-ssl", useSsl.toString());
      formData.append("skip-tls-verification", skipTlsVerification.toString());
      formData.append("user", user);
      formData.append("pass", pass);

      const response = await fetch(withUrlBase("/api/test-usenet-connection"), {
        method: "POST",
        body: formData,
      });

      if (response.ok) {
        // Response of POST /api/test-usenet-connection (backend TestUsenetConnectionResponse).
        const data = (await response.json()) as TestConnectionResult;
        if (data.connected) {
          setConnectionTested(true);
          setTestError(null);
        } else {
          setTestError("Connection test failed");
        }
      } else {
        const data = (await response.json().catch(() => null)) as TestConnectionResult | null;
        setTestError(data?.error || "Failed to test connection");
      }
    } catch (error) {
      setTestError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
    } finally {
      setIsTestingConnection(false);
    }
  }, [host, port, useSsl, skipTlsVerification, user, pass]);

  const handleAutoTune = useCallback(
    async (verifyConnections?: number) => {
      const benchmarkLimits = resolveBenchmarkConnectionLimits(
        {
          providerConnectionLimit: maxConnections,
          transferConnections: maxTransferConnections,
        },
        pipeliningOnly,
      );
      if (benchmarkLimits === null) {
        setBenchmarkError(
          "Enter a valid Provider Connection Limit and, for a pipelining-only test, valid Transfer Connections.",
        );
        return;
      }

      // Abort any previous run still in flight before starting a new one.
      benchmarkAbortRef.current?.abort();
      await fetch(withUrlBase("/api/benchmark-usenet-connection"), {
        method: "POST",
        body: (() => {
          const f = new FormData();
          f.append("cancel", "true");
          return f;
        })(),
      }).catch(() => {
        /* best-effort */
      });

      const controller = new AbortController();
      benchmarkAbortRef.current = controller;
      let finished = false;
      let unsubscribeProgress = () => {};
      const finish = (result?: BenchmarkResult | null, error?: string | null) => {
        if (finished) return;
        finished = true;
        if (result) {
          setBenchmarkResult(result);
          setConnectionTested(true);
          setBenchmarkError(null);
        } else if (error) {
          setBenchmarkError(error);
        }
        setIsBenchmarking(false);
        setBenchmarkProgress(null);
        if (benchmarkAbortRef.current === controller) benchmarkAbortRef.current = null;
        unsubscribeProgress();
      };

      setIsBenchmarking(true);
      setBenchmarkError(null);
      setBenchmarkResult(null);
      setBenchmarkProgress({
        phase: "latency",
        status: "Starting speed test…",
        percent: 0,
        dataUsedBytes: 0,
        sweep: [],
      });

      // Progress + terminal result over the websocket. The POST may be dropped by
      // an intermediary timeout on large budgets; the done frame still finishes the UI.
      // Ignore a replayed previous "done" (state topics resend lastMessage on subscribe)
      // until we've seen live progress from this run.
      let sawLiveProgress = false;
      unsubscribeProgress = subscribeWebsocketTopics({ bench: "state" }, (topic, message) => {
        if (topic !== "bench") return;
        try {
          const update = JSON.parse(message) as BenchmarkProgress;
          if (update.phase === "done") {
            if (!sawLiveProgress) return;
            finish(update.result ?? null, update.error ?? null);
            return;
          }
          sawLiveProgress = true;
          setBenchmarkProgress(update);
        } catch {
          /* ignore malformed progress */
        }
      });

      try {
        const formData = new FormData();
        formData.append("host", host);
        formData.append("port", port);
        formData.append("use-ssl", useSsl.toString());
        formData.append("skip-tls-verification", skipTlsVerification.toString());
        formData.append("user", user);
        formData.append("pass", pass);
        formData.append("max-connections", benchmarkLimits.providerConnectionLimit);
        if (pipeliningOnly) {
          formData.append("transfer-connections", benchmarkLimits.testConnections);
        }
        formData.append("intensity", intensity);
        formData.append("pipelining-only", pipeliningOnly ? "true" : "false");
        if (dataBudget) formData.append("data-budget-mb", dataBudget);
        if (verifyConnections) formData.append("verify-connections", String(verifyConnections));

        const response = await fetch(withUrlBase("/api/benchmark-usenet-connection"), {
          method: "POST",
          body: formData,
          signal: controller.signal,
        });
        // Response of POST /api/benchmark-usenet-connection (backend BenchmarkUsenetConnectionResponse).
        const data = (await response.json().catch(() => null)) as BenchmarkPostResult | null;
        if (finished) return;

        if (response.ok && data?.status && data.result) {
          finish(data.result, null);
          return;
        }
        if (data?.error) {
          finish(null, data.error);
          return;
        }
        // POST dropped or returned an empty body (common when a proxy hits an
        // idle/TTFB limit). Keep listening for the websocket done frame.
        setBenchmarkProgress((prev) =>
          prev
            ? { ...prev, status: "Speed test still running…" }
            : {
                phase: "sweep",
                status: "Speed test still running…",
                percent: 50,
                dataUsedBytes: 0,
                sweep: [],
              },
        );
      } catch (error) {
        if (error instanceof DOMException && error.name === "AbortError") {
          // Cancelled by the user (Cancel button or closing the modal).
          if (!finished) finish(null, null);
          return;
        }
        // Network / proxy drop mid-run: keep listening for the websocket done frame.
        setBenchmarkProgress((prev) =>
          prev
            ? { ...prev, status: "Speed test still running…" }
            : {
                phase: "sweep",
                status: "Speed test still running…",
                percent: 50,
                dataUsedBytes: 0,
                sweep: [],
              },
        );
      }
    },
    [
      host,
      port,
      useSsl,
      skipTlsVerification,
      user,
      pass,
      maxConnections,
      maxTransferConnections,
      intensity,
      pipeliningOnly,
      dataBudget,
    ],
  );

  const handleApplyRecommendation = useCallback(() => {
    if (!benchmarkResult) return;
    const connectionLimits = applyAutoTuneTransferRecommendation(
      {
        providerConnectionLimit: maxConnections,
        transferConnections: maxTransferConnections,
      },
      benchmarkResult.recommendedConnections,
      benchmarkResult.pipeliningOnly,
      benchmarkResult.verificationRun ?? false,
    );
    setMaxTransferConnections(connectionLimits.transferConnections);
    if (benchmarkResult.pipelining) {
      setPipeliningDepth(String(benchmarkResult.pipelining.recommendedDepth));
      onApplyPipelining(benchmarkResult.pipelining.recommendEnabled);
    }
  }, [benchmarkResult, maxConnections, maxTransferConnections, onApplyPipelining]);

  const handleCancelBenchmark = useCallback(() => {
    benchmarkAbortRef.current?.abort();
    void fetch(withUrlBase("/api/benchmark-usenet-connection"), {
      method: "POST",
      body: (() => {
        const f = new FormData();
        f.append("cancel", "true");
        return f;
      })(),
    }).catch(() => {
      /* best-effort cancel */
    });
    setIsBenchmarking(false);
    setBenchmarkProgress(null);
  }, []);

  const handleSave = useCallback(async () => {
    const byteLimit = valueAndUnitToBytes(limitValue, limitUnit);
    const initialUsedBytes = valueAndUnitToBytes(initialUsedValue, initialUsedUnit);

    // On a brand-new provider, an initial-used value also sets ResetAt to
    // now — otherwise the metrics rollup would count any pre-existing
    // history for the same hostname twice. On edit, leave ResetAt alone
    // (the dedicated Reset button is the right surface for that).
    const isNew = !isEditing;
    const offsetToPersist = initialUsedBytes ?? (isNew ? 0 : (provider?.BytesUsedOffset ?? 0));
    const resetAtToPersist =
      isNew && initialUsedBytes !== null ? Date.now() : (provider?.BytesUsedResetAt ?? 0);

    const trimmedNickname = nickname.trim();
    const trimmedStorageGroup = storageGroup.trim();
    setIsSaving(true);
    setSaveError(null);
    try {
      await onSave({
        Type: type,
        Host: host,
        Port: parseInt(port, 10),
        UseSsl: useSsl,
        SkipTlsVerification: useSsl && skipTlsVerification,
        User: user,
        Pass: pass,
        MaxConnections: parseInt(maxConnections, 10),
        MaxTransferConnections:
          maxTransferConnections.trim() === "" ? null : parseInt(maxTransferConnections, 10),
        PipeliningDepth: pipeliningDepth.trim() === "" ? null : parseInt(pipeliningDepth, 10),
        Priority: provider?.Priority ?? 0,
        ...(provider?.ProviderId ? { ProviderId: provider.ProviderId } : {}),
        ...(trimmedNickname !== "" ? { Nickname: trimmedNickname } : {}),
        StorageGroup: trimmedStorageGroup,
        ...(type === ProviderType.Disabled && provider?.PreviousType !== undefined
          ? { PreviousType: provider.PreviousType }
          : {}),
        ByteLimit: byteLimit,
        BytesUsedOffset: offsetToPersist,
        BytesUsedResetAt: resetAtToPersist,
      });
    } catch {
      setSaveError("Could not save provider settings. Check the server logs and try again.");
    } finally {
      setIsSaving(false);
    }
  }, [
    type,
    host,
    port,
    useSsl,
    skipTlsVerification,
    user,
    pass,
    maxConnections,
    maxTransferConnections,
    pipeliningDepth,
    nickname,
    storageGroup,
    provider,
    isEditing,
    limitValue,
    limitUnit,
    initialUsedValue,
    initialUsedUnit,
    onSave,
  ]);

  const isPipeliningDepthValid =
    pipeliningDepth.trim() === "" ||
    (isPositiveInteger(pipeliningDepth) && Number(pipeliningDepth) <= 64);

  const hasTransferLimit = maxTransferConnections.trim() !== "";
  const transferLimitIsPositiveInteger = isPositiveInteger(maxTransferConnections);
  const transferLimitExceedsProvider =
    hasTransferLimit &&
    transferLimitIsPositiveInteger &&
    isPositiveInteger(maxConnections) &&
    Number(maxTransferConnections) > Number(maxConnections);
  const isTransferLimitValid =
    !hasTransferLimit || (transferLimitIsPositiveInteger && !transferLimitExceedsProvider);
  const budgetPreview =
    hasTransferLimit && isTransferLimitValid && isPositiveInteger(maxConnections)
      ? calculateProviderConnectionBudget(Number(maxConnections), Number(maxTransferConnections))
      : null;
  const transferLimitHelp =
    hasTransferLimit && !transferLimitIsPositiveInteger
      ? "Enter a positive whole number."
      : transferLimitExceedsProvider
        ? "Transfer Connections cannot exceed the Provider Connection Limit."
        : hasTransferLimit
          ? "Maximum concurrent article-transfer operations."
          : "Blank preserves legacy shared-pool behavior; Auto-tune can set this value.";

  const isFormValid =
    host.trim() !== "" &&
    isPositiveInteger(port) &&
    user.trim() !== "" &&
    pass.trim() !== "" &&
    isPositiveInteger(maxConnections) &&
    isTransferLimitValid &&
    isPipeliningDepthValid;

  const benchmarkConnectionLimits = resolveBenchmarkConnectionLimits(
    {
      providerConnectionLimit: maxConnections,
      transferConnections: maxTransferConnections,
    },
    pipeliningOnly,
  );
  // Credentials and the provider ceiling are always required. Pipelining-only
  // runs additionally require a valid transfer count (blank uses the ceiling).
  const canBenchmark =
    host.trim() !== "" &&
    isPositiveInteger(port) &&
    user.trim() !== "" &&
    pass.trim() !== "" &&
    benchmarkConnectionLimits !== null;

  const canSave =
    isFormValid && (connectionTested || passIsMasked || type == ProviderType.Disabled);

  const storageGroupSuggestions = useMemo(() => {
    const query = storageGroup.trim().toLowerCase();
    if (!query) return existingStorageGroups;
    return existingStorageGroups.filter((name) => name.toLowerCase().includes(query));
  }, [existingStorageGroups, storageGroup]);

  return (
    <Modal
      open={show}
      title={provider ? "Edit Provider" : "Add Provider"}
      onClose={onClose}
      preventClose={isSaving}
      className="!max-w-4xl"
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={isSaving}>
            Cancel
          </Button>
          {canSave && type !== ProviderType.Disabled && (
            <Button
              variant="outline"
              onClick={() => void handleTestConnection()}
              disabled={!isFormValid || isTestingConnection || isSaving}
            >
              {isTestingConnection ? "Testing..." : "Test Connection"}
            </Button>
          )}
          {!canSave ? (
            <Button
              variant="primary"
              onClick={() => void handleTestConnection()}
              disabled={!isFormValid || isTestingConnection || isSaving}
            >
              {isTestingConnection ? "Testing..." : "Test Connection"}
            </Button>
          ) : (
            <Button
              variant="primary"
              onClick={() => void handleSave()}
              disabled={!canSave || isSaving}
            >
              {isSaving ? "Saving..." : "Save Provider"}
            </Button>
          )}
        </>
      }
    >
      <div className="flex flex-col gap-5">
        {saveError && (
          <Alert variant="danger" className="text-sm">
            {saveError}
          </Alert>
        )}
        <ProviderModalSection title="Connection">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-[minmax(0,1fr)_7.5rem]">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="provider-host">Host</Label>
              <Input
                type="text"
                id="provider-host"
                className="w-full"
                placeholder="news.provider.com"
                value={host}
                onChange={(e) => {
                  setHost(e.target.value);
                  setConnectionTested(false);
                }}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="provider-port">Port</Label>
              <Input
                type="text"
                id="provider-port"
                className={`w-full ${!isPositiveInteger(port) && port !== "" ? "input-error" : ""}`}
                placeholder="563"
                value={port}
                onChange={(e) => {
                  setPort(e.target.value);
                  setConnectionTested(false);
                }}
              />
            </div>
          </div>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="provider-user">Username</Label>
              <Input
                type="text"
                id="provider-user"
                className="w-full"
                placeholder="username"
                value={user}
                onChange={(e) => {
                  setUser(e.target.value);
                  setConnectionTested(false);
                }}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="provider-pass">Password</Label>
              <Input
                type="password"
                id="provider-pass"
                className="w-full"
                placeholder="password"
                value={pass}
                onChange={(e) => {
                  setPass(e.target.value);
                  setConnectionTested(false);
                }}
              />
            </div>
          </div>

          <div className="flex flex-col gap-2">
            <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
              <Tooltip
                className="tooltip-start"
                content="Encrypt the NNTP connection. Prefer port 563 with SSL enabled; without SSL credentials are sent in cleartext."
              >
                <Toggle
                  id="provider-ssl"
                  className="cursor-pointer gap-2 p-0"
                  checked={useSsl}
                  onChange={(e) => {
                    setUseSsl(e.target.checked);
                    setConnectionTested(false);
                  }}
                  label={<span className="text-sm text-base-content">Use SSL</span>}
                />
              </Tooltip>
              {useSsl && (
                <Tooltip content="TLS stays encrypted, but accepts an untrusted or mismatched certificate. Only enable for a provider you trust.">
                  <Toggle
                    id="provider-skip-tls-verification"
                    className="cursor-pointer gap-2 p-0"
                    checked={skipTlsVerification}
                    onChange={(e) => {
                      setSkipTlsVerification(e.target.checked);
                      setConnectionTested(false);
                    }}
                    label={<span className="text-sm text-base-content">Skip TLS verification</span>}
                  />
                </Tooltip>
              )}
            </div>
            {shouldWarnCleartextCredentials(useSsl, user) && (
              <Alert variant="warning" className="text-xs">
                Credentials are sent unencrypted without SSL. Prefer port 563 with SSL enabled.
              </Alert>
            )}
            {useSsl && skipTlsVerification && (
              <Alert variant="warning" className="text-xs">
                TLS remains encrypted, but this accepts an untrusted or mismatched certificate. Only
                enable it for a provider you trust.
              </Alert>
            )}
          </div>
        </ProviderModalSection>

        <ProviderModalSection title="Identity">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="provider-nickname">Nickname</Label>
              <Input
                type="text"
                id="provider-nickname"
                className="w-full"
                placeholder="e.g. Main provider"
                value={nickname}
                onChange={(e) => setNickname(e.target.value)}
              />
              <HelpText>Shown in the UI instead of the hostname.</HelpText>
            </div>
            <div className="flex flex-col gap-1.5">
              <div className="flex items-center gap-1.5">
                <Label htmlFor="provider-storage-group">Storage group</Label>
                <Tooltip
                  placement="bottom"
                  content="Same label for providers that share upstream storage. When one reports an article missing, siblings with this label are skipped for that request."
                >
                  <Icon name="info" className="!text-[15px] text-base-content/45" />
                </Tooltip>
              </div>
              <div className="relative w-full">
                <Input
                  type="text"
                  id="provider-storage-group"
                  className="w-full"
                  placeholder="e.g. omicron"
                  value={storageGroup}
                  autoComplete="off"
                  role="combobox"
                  aria-expanded={storageGroupOpen && storageGroupSuggestions.length > 0}
                  aria-controls="provider-storage-group-listbox"
                  aria-autocomplete="list"
                  onChange={(e) => {
                    setStorageGroup(e.target.value);
                    setStorageGroupOpen(true);
                  }}
                  onFocus={() => setStorageGroupOpen(true)}
                  onBlur={() => {
                    window.setTimeout(() => setStorageGroupOpen(false), 150);
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Escape") setStorageGroupOpen(false);
                  }}
                />
                {storageGroupOpen && storageGroupSuggestions.length > 0 && (
                  <ul
                    id="provider-storage-group-listbox"
                    role="listbox"
                    className="absolute inset-x-0 top-full z-50 mt-1 max-h-48 overflow-y-auto rounded-box border border-base-content/10 bg-base-300 py-1 shadow-lg"
                  >
                    {storageGroupSuggestions.map((name) => (
                      <li key={name} role="option">
                        <button
                          type="button"
                          className="block w-full px-3 py-2 text-left text-sm text-base-content hover:bg-base-content/10"
                          onMouseDown={(e) => e.preventDefault()}
                          onClick={() => {
                            setStorageGroup(name);
                            setStorageGroupOpen(false);
                          }}
                        >
                          {name}
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="provider-type">Type</Label>
              <Select
                id="provider-type"
                className="w-full"
                value={type}
                onChange={(e) => setType(parseInt(e.target.value, 10))}
              >
                <option value={ProviderType.Disabled}>Disabled</option>
                <option value={ProviderType.Pooled}>Pool Connections</option>
                <option value={ProviderType.BackupOnly}>Backup Only</option>
              </Select>
            </div>
          </div>
        </ProviderModalSection>

        <ProviderModalSection title="Performance">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <div className="flex flex-col gap-1.5">
              <div className="flex items-center gap-1.5">
                <Label htmlFor="provider-max-connections">Provider Connection Limit</Label>
                <Tooltip
                  placement="bottom"
                  content="The absolute maximum number of NNTP connections InfiniDysk may use for this provider account."
                >
                  <Icon name="info" className="!text-[15px] text-base-content/45" />
                </Tooltip>
              </div>
              <Input
                type="text"
                inputMode="numeric"
                id="provider-max-connections"
                className={`w-full max-w-48 ${!isPositiveInteger(maxConnections) && maxConnections !== "" ? "input-error" : ""}`}
                placeholder="20"
                value={maxConnections}
                onChange={(e) => setMaxConnections(e.target.value)}
              />
              <HelpText>
                Provider-wide ceiling for transfers, metadata, and warm sockets combined.
              </HelpText>
            </div>
            <div className="flex flex-col gap-1.5">
              <div className="flex items-center gap-1.5">
                <Label htmlFor="provider-transfer-connections">Transfer Connections</Label>
                <Tooltip
                  placement="bottom"
                  content="Hard limit for concurrent BODY and ARTICLE transfers. Leave blank to retain legacy shared-pool scheduling."
                >
                  <Icon name="info" className="!text-[15px] text-base-content/45" />
                </Tooltip>
              </div>
              <Input
                type="text"
                inputMode="numeric"
                id="provider-transfer-connections"
                className={`w-full ${hasTransferLimit && !isTransferLimitValid ? "input-error" : ""}`}
                placeholder="Legacy shared pool"
                value={maxTransferConnections}
                onChange={(e) => setMaxTransferConnections(e.target.value)}
              />
              <HelpText>{transferLimitHelp}</HelpText>
            </div>
            <div className="flex flex-col gap-1.5 sm:col-span-2 lg:col-span-1">
              <Label>Metadata Capacity</Label>
              <div
                className="flex min-h-12 items-center rounded-lg border border-base-content/10 bg-base-200/60 px-3 py-2"
                role="status"
                aria-live="polite"
              >
                <span className="font-mono text-sm font-semibold tabular-nums text-base-content">
                  {budgetPreview
                    ? formatMetadataCapacity(budgetPreview)
                    : hasTransferLimit
                      ? "—"
                      : "Legacy shared pool"}
                </span>
              </div>
              <HelpText>
                {budgetPreview
                  ? `${budgetPreview.baseMetadataCapacity} connections remain available for metadata. Metadata may temporarily borrow up to ${budgetPreview.metadataBurstAllowance} unused transfer connections.`
                  : hasTransferLimit
                    ? "Enter valid connection limits to calculate metadata capacity."
                    : "Transfers and metadata share the Provider Connection Limit until budgeting is enabled."}
              </HelpText>
            </div>
          </div>
          {transferLimitExceedsProvider && (
            <Alert variant="danger" className="alert-soft text-xs">
              Transfer Connections ({maxTransferConnections}) cannot exceed the Provider Connection
              Limit ({maxConnections}). Correct either value before saving.
            </Alert>
          )}

          <div className="max-w-sm">
            <div className="flex flex-col gap-1.5">
              <div className="flex items-center gap-1.5">
                <Label htmlFor="provider-pipelining-depth">Pipeline depth</Label>
                <Tooltip
                  placement="bottom"
                  content="Requests kept in flight per connection (1–64) when NNTP pipelining is enabled. Leave blank to use the global default."
                >
                  <Icon name="info" className="!text-[15px] text-base-content/45" />
                </Tooltip>
              </div>
              <Input
                type="text"
                id="provider-pipelining-depth"
                className={`w-full max-w-48 ${!isPipeliningDepthValid ? "input-error" : ""}`}
                placeholder={defaultPipeliningDepth || "8"}
                value={pipeliningDepth}
                onChange={(e) => setPipeliningDepth(e.target.value)}
              />
            </div>
          </div>

          <BenchmarkPanel
            canBenchmark={canBenchmark}
            providerConnectionLimit={maxConnections}
            isBenchmarking={isBenchmarking}
            intensity={intensity}
            setIntensity={setIntensity}
            dataBudget={dataBudget}
            setDataBudget={setDataBudget}
            pipeliningOnly={pipeliningOnly}
            setPipeliningOnly={setPipeliningOnly}
            progress={benchmarkProgress}
            result={benchmarkResult}
            error={benchmarkError}
            onRun={() => void handleAutoTune()}
            onVerify={(connections) => void handleAutoTune(connections)}
            onCancel={handleCancelBenchmark}
            onApply={handleApplyRecommendation}
          />
        </ProviderModalSection>

        <ProviderModalSection title="Data quota">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <div className="flex items-center gap-1.5">
                <Label htmlFor="provider-data-cap">Data Cap</Label>
                <Tooltip
                  placement="bottom"
                  content="For block accounts: total bytes purchased. The provider auto-pauses near 95% of this value to absorb in-flight requests."
                >
                  <Icon name="info" className="!text-[15px] text-base-content/45" />
                </Tooltip>
              </div>
              <div className="join w-full max-w-72">
                <Input
                  id="provider-data-cap"
                  type="text"
                  inputMode="decimal"
                  className="join-item min-w-0 flex-1"
                  placeholder="No cap"
                  value={limitValue}
                  onChange={(e) => setLimitValue(e.target.value)}
                />
                <Select
                  className="join-item w-20"
                  aria-label="Data cap unit"
                  value={limitUnit}
                  onChange={(e) => setLimitUnit(e.target.value as ByteUnitLabel)}
                >
                  {BYTE_UNITS.map((u) => (
                    <option key={u.label} value={u.label}>
                      {u.label}
                    </option>
                  ))}
                </Select>
              </div>
            </div>
            <div className="flex flex-col gap-1.5">
              <div className="flex items-center gap-1.5">
                <Label htmlFor="provider-data-used">Already Used</Label>
                <Tooltip
                  placement="bottom"
                  content="Seed the counter when migrating a partially-used block from another client. Leave empty for a fresh block."
                >
                  <Icon name="info" className="!text-[15px] text-base-content/45" />
                </Tooltip>
              </div>
              <div className="join w-full max-w-72">
                <Input
                  id="provider-data-used"
                  type="text"
                  inputMode="decimal"
                  className="join-item min-w-0 flex-1"
                  placeholder="0"
                  value={initialUsedValue}
                  onChange={(e) => setInitialUsedValue(e.target.value)}
                />
                <Select
                  className="join-item w-20"
                  aria-label="Already used unit"
                  value={initialUsedUnit}
                  onChange={(e) => setInitialUsedUnit(e.target.value as ByteUnitLabel)}
                >
                  {BYTE_UNITS.map((u) => (
                    <option key={u.label} value={u.label}>
                      {u.label}
                    </option>
                  ))}
                </Select>
              </div>
            </div>
          </div>
        </ProviderModalSection>

        {testError && (
          <Alert variant="danger" className="text-xs">
            {testError}
          </Alert>
        )}

        {connectionTested && (
          <Alert variant="success" className="text-xs">
            Connection test successful!
          </Alert>
        )}
      </div>
    </Modal>
  );
}

function ProviderModalSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="space-y-3">
      <h3 className="text-xs font-semibold uppercase tracking-wide text-base-content/50">
        {title}
      </h3>
      <div className="space-y-3">{children}</div>
    </section>
  );
}

type BenchmarkPanelProps = {
  canBenchmark: boolean;
  providerConnectionLimit: string;
  isBenchmarking: boolean;
  intensity: BenchmarkIntensity;
  setIntensity: (value: BenchmarkIntensity) => void;
  dataBudget: string;
  setDataBudget: (value: string) => void;
  pipeliningOnly: boolean;
  setPipeliningOnly: (value: boolean) => void;
  progress: BenchmarkProgress | null;
  result: BenchmarkResult | null;
  error: string | null;
  onRun: () => void;
  onVerify: (connections: number) => void;
  onCancel: () => void;
  onApply: () => void;
};

const BENCH_PHASES = [
  { id: "latency", label: "Latency" },
  { id: "corpus", label: "Corpus" },
  { id: "sweep", label: "Sweep" },
  { id: "pipelining", label: "Pipelining" },
  { id: "done", label: "Done" },
] as const;

function BenchmarkPanel(props: BenchmarkPanelProps) {
  const {
    canBenchmark,
    providerConnectionLimit,
    isBenchmarking,
    intensity,
    setIntensity,
    dataBudget,
    setDataBudget,
    pipeliningOnly,
    setPipeliningOnly,
    progress,
    result,
    error,
    onRun,
    onVerify,
    onCancel,
    onApply,
  } = props;
  const [applied, setApplied] = useState(false);
  // A fresh result means the previous "Applied" state no longer holds.
  useEffect(() => {
    setApplied(false);
  }, [result]);

  const recommended = result?.recommendedConnections ?? null;
  const livePoints = isBenchmarking ? (progress?.sweep ?? []) : (result?.sweep ?? []);
  const recommendedSpeed =
    recommended != null && result?.sweep
      ? (result.sweep.find((p) => p.connections === recommended)?.megaBytesPerSec ??
        (result.sweep.length > 0 ? Math.max(...result.sweep.map((p) => p.megaBytesPerSec)) : null))
      : null;
  const pipe = result?.pipelining ?? null;
  const pipeBest =
    pipe && pipe.tested.length > 0
      ? Math.max(...pipe.tested.map((t) => t.megaBytesPerSec))
      : (pipe?.baselineMegaBytesPerSec ?? 0);
  const pipeGainPct =
    pipe && pipe.baselineMegaBytesPerSec > 0
      ? Math.round((pipeBest / pipe.baselineMegaBytesPerSec - 1) * 100)
      : 0;
  const canApply =
    !!result &&
    !result.verificationRun &&
    result.throughputTested &&
    (recommended != null || (result.pipeliningOnly && !!pipe));
  const phaseIndex = progress
    ? Math.max(
        0,
        BENCH_PHASES.findIndex((p) => p.id === progress.phase),
      )
    : -1;
  const elapsedLabel = formatElapsed(result?.elapsedSeconds);
  const highestTestedConnections =
    result?.sweep.reduce((highest, point) => Math.max(highest, point.connections), 0) ?? 0;
  const reachedProviderCeiling =
    isPositiveInteger(providerConnectionLimit) &&
    highestTestedConnections >= Number(providerConnectionLimit);

  return (
    <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-4">
      <div className="space-y-1">
        <div className="text-sm font-semibold text-base-content">
          Auto-tune transfer connections
        </div>
        <HelpText>
          {pipeliningOnly
            ? "Keeps your Transfer Connections and measures the best NNTP pipelining depth at that count."
            : "Runs a real speed and latency test, then recommends Transfer Connections and pipelining settings without changing your Provider Connection Limit. Speeds use megabytes per second (MB/s), matching SABnzbd. 1 Gb/s is about 125 MB/s."}
        </HelpText>
      </div>

      <div className="mt-4 grid gap-3 sm:grid-cols-[auto_minmax(14rem,1fr)_auto] sm:items-end">
        <fieldset className="fieldset gap-1 p-0">
          <legend className="fieldset-legend mb-1 py-0 text-[11px] text-base-content/60">
            Test depth
          </legend>
          <div className="join" role="group" aria-label="Test intensity">
            <button
              type="button"
              className={`btn btn-sm join-item ${intensity === "quick" ? "btn-active" : "btn-ghost"}`}
              onClick={() => setIntensity("quick")}
              disabled={isBenchmarking}
              aria-pressed={intensity === "quick"}
            >
              Quick
            </button>
            <button
              type="button"
              className={`btn btn-sm join-item ${intensity === "thorough" ? "btn-active" : "btn-ghost"}`}
              onClick={() => setIntensity("thorough")}
              disabled={isBenchmarking}
              aria-pressed={intensity === "thorough"}
            >
              Thorough
            </button>
          </div>
        </fieldset>
        <div className="flex min-w-0 flex-col gap-1">
          <Label htmlFor="benchmark-data-budget">Data budget</Label>
          <Select
            id="benchmark-data-budget"
            className="w-full"
            value={dataBudget}
            onChange={(e) => setDataBudget(e.target.value)}
            disabled={isBenchmarking}
          >
            <option value="">Auto ({intensity === "quick" ? "up to 500 MB" : "up to 2 GB"})</option>
            <option value="100">100 MB</option>
            <option value="250">250 MB</option>
            <option value="500">500 MB</option>
            <option value="1000">1 GB</option>
            <option value="2000">2 GB</option>
            <option value="5000">5 GB</option>
            <option value="10000">10 GB</option>
            <option value="20000">20 GB</option>
            <option value="35000">35 GB</option>
            <option value="50000">50 GB</option>
          </Select>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button onClick={onRun} disabled={!canBenchmark || isBenchmarking}>
            {isBenchmarking && <span className="loading loading-spinner loading-xs" />}
            {isBenchmarking ? "Testing…" : pipeliningOnly ? "Test pipelining" : "Run speed test"}
          </Button>
          {isBenchmarking && (
            <Button variant="outline" onClick={onCancel}>
              Cancel
            </Button>
          )}
        </div>
      </div>

      <div className="mt-4 border-t border-base-content/10 pt-3">
        <Tooltip
          className="block"
          content={
            pipeliningOnly
              ? "Won't change Transfer Connections — tests pipelining depth at that count, or at the Provider Connection Limit in legacy mode. Run idle for the cleanest read."
              : "When off, also sweeps transfer connection counts. Prefer idle for the cleanest read."
          }
        >
          <Toggle
            id="bench-pipe-only"
            className="cursor-pointer gap-2 p-0"
            checked={pipeliningOnly}
            disabled={isBenchmarking}
            onChange={(e) => setPipeliningOnly(e.target.checked)}
            label={
              <span className="text-sm text-base-content">
                Only tune pipelining (keep my Transfer Connections)
              </span>
            }
          />
        </Tooltip>

        <HelpText className="mt-1">
          {intensity === "quick"
            ? "Quick sizes each step to your line speed, up to the data budget (default 500 MB). It is lighter on metered or block accounts."
            : "Thorough runs longer measurement windows for steadier numbers, up to the data budget (default 2 GB). Gigabit-class lines often need 10-20 GB for a full sweep."}
        </HelpText>
      </div>

      {error && (
        <Alert variant="danger" className="alert-soft mt-3 text-xs">
          {error}
        </Alert>
      )}

      {isBenchmarking && progress && (
        <div className="mt-3.5 flex flex-col gap-3">
          <ul className="steps steps-horizontal w-full text-xs">
            {BENCH_PHASES.map((phase, i) => (
              <li key={phase.id} className={`step ${i <= phaseIndex ? "step-primary" : ""}`}>
                {phase.label}
              </li>
            ))}
          </ul>
          <div>
            <div className="mb-1.5 flex justify-between gap-2.5 text-xs text-base-content/80">
              <span className="inline-flex items-center gap-1.5">
                <span className="loading loading-spinner loading-xs" />
                {progress.status}
              </span>
              <span className="font-mono tabular-nums">
                {formatBytes(progress.dataUsedBytes)}
                {progress.dataBudgetBytes ? ` / ${formatBytes(progress.dataBudgetBytes)}` : ""} used
              </span>
            </div>
            <progress
              className="progress progress-primary w-full"
              value={Math.max(2, Math.min(100, progress.percent))}
              max={100}
            />
          </div>
        </div>
      )}

      {livePoints.length > 0 && !(isBenchmarking ? pipeliningOnly : result?.pipeliningOnly) && (
        <SweepChart points={livePoints} recommended={recommended} />
      )}

      {result && !isBenchmarking && (
        <>
          {result.contentionWarnings?.map((warning) => (
            <Alert key={warning} variant="warning" className="alert-soft mt-3 text-xs">
              {warning}
            </Alert>
          ))}

          {result.stillClimbing &&
            reachedProviderCeiling &&
            !result.pipeliningOnly &&
            !result.verificationRun &&
            result.throughputTested && (
              <Alert variant="warning" className="alert-soft mt-3 text-xs">
                <span className="leading-relaxed">
                  <strong className="font-semibold">
                    Speed was still climbing at your Provider Connection Limit.
                  </strong>{" "}
                  Auto-tune does not probe above that ceiling. If your account permits more
                  connections, raise the limit and run Auto-tune again.
                </span>
              </Alert>
            )}

          {result.confidence && (
            <div className="mt-3">
              <Tooltip
                placement="right"
                content="How steady the measurements were (bucket-to-bucket throughput variation, article-pool reuse, and concurrent activity)."
              >
                <Badge
                  className={`badge-sm badge-soft font-medium ${
                    result.confidence === "high"
                      ? "badge-success"
                      : result.confidence === "medium"
                        ? "badge-warning"
                        : "badge-error"
                  }`}
                >
                  {result.confidence === "high"
                    ? "High confidence"
                    : result.confidence === "medium"
                      ? "Medium confidence"
                      : "Low confidence"}
                </Badge>
              </Tooltip>
            </div>
          )}

          {result.pipeliningOnly ? (
            pipe ? (
              <>
                <DepthChart pipe={pipe} />
                <div className="stats stats-vertical mt-4 w-full border border-base-content/10 bg-base-300 sm:stats-horizontal">
                  <div className="stat py-3">
                    <div className="stat-title text-[10px] uppercase tracking-wide">Pipelining</div>
                    <div className="stat-value text-xl font-mono">
                      {pipe.recommendEnabled ? `Depth ${pipe.recommendedDepth}` : "Off"}
                    </div>
                    <div className="stat-desc font-mono tabular-nums">
                      {pipe.recommendEnabled ? `≈ +${pipeGainPct}% vs off` : "no real gain"}
                    </div>
                  </div>
                  {result.latency && (
                    <div className="stat py-3">
                      <div className="stat-title text-[10px] uppercase tracking-wide">Latency</div>
                      <div className="stat-value text-sm font-mono">{result.latency.avgMs} ms</div>
                      <div className="stat-desc font-mono tabular-nums">
                        {result.latency.minMs} ms min
                      </div>
                    </div>
                  )}
                  <div className="stat py-3">
                    <div className="stat-title text-[10px] uppercase tracking-wide">Tested at</div>
                    <div className="stat-value text-sm font-mono">{pipe.testedAtConnections}</div>
                    <div className="stat-desc">connections</div>
                  </div>
                  <div className="stat py-3">
                    <div className="stat-title text-[10px] uppercase tracking-wide">Data used</div>
                    <div className="stat-value text-sm font-mono">
                      {formatBytes(result.dataUsedBytes)}
                    </div>
                    <div className="stat-desc">
                      whole run{elapsedLabel ? ` · ${elapsedLabel}` : ""}
                    </div>
                  </div>
                </div>
                <div className="mt-3 text-sm leading-relaxed text-base-content/80">
                  {pipe.recommendEnabled ? (
                    <>
                      Turn on{" "}
                      <strong className="font-semibold text-base-content">Queue pipelining</strong>{" "}
                      at depth{" "}
                      <strong className="font-semibold text-base-content">
                        {pipe.recommendedDepth}
                      </strong>{" "}
                      for faster queue imports at your {pipe.testedAtConnections} connections — not
                      WebDAV playback.
                    </>
                  ) : (
                    <>
                      Queue pipelining didn’t help at your {pipe.testedAtConnections} connections —
                      leave it off.
                    </>
                  )}
                </div>
              </>
            ) : (
              <div className="mt-3.5 text-sm leading-relaxed text-base-content/80">
                Couldn’t measure pipelining
                {result.latency ? ` (latency ${result.latency.avgMs} ms)` : ""}. Try again when
                idle.
              </div>
            )
          ) : result.verificationRun && result.sweep[0] ? (
            <div className="mt-4 text-sm leading-relaxed text-base-content/80">
              {result.sweep[0].megaBytesPerSec < 0.5 ? (
                <>
                  Verification at{" "}
                  <strong className="font-semibold text-base-content">
                    {result.sweep[0].connections} connection
                    {result.sweep[0].connections === 1 ? "" : "s"}
                  </strong>{" "}
                  didn’t measure usable throughput
                  {result.sweep[0].megaBytesPerSec > 0 ? (
                    <>
                      {" "}
                      (
                      <strong className="font-semibold text-base-content">
                        {result.sweep[0].megaBytesPerSec} MB/s
                      </strong>
                      )
                    </>
                  ) : null}
                  . Re-run when idle, or run a full speed test again.
                </>
              ) : (
                <>
                  Verified:{" "}
                  <strong className="font-semibold text-base-content">
                    {result.sweep[0].megaBytesPerSec} MB/s
                  </strong>{" "}
                  at{" "}
                  <strong className="font-semibold text-base-content">
                    {result.sweep[0].connections} connection
                    {result.sweep[0].connections === 1 ? "" : "s"}
                  </strong>
                  .
                </>
              )}
            </div>
          ) : result.throughputTested && recommended ? (
            <div className="stats stats-vertical mt-4 w-full max-w-full border border-base-content/10 bg-base-300 sm:stats-horizontal">
              <div className="stat min-w-0 py-3">
                <div className="stat-title text-[10px] uppercase tracking-wide">
                  Recommended Transfer Connections
                </div>
                <div className="stat-value text-xl font-mono">{recommended}</div>
                <div className="stat-desc font-mono tabular-nums">
                  connection{recommended === 1 ? "" : "s"}
                  {recommendedSpeed != null
                    ? ` · ≈ ${recommendedSpeed.toFixed(1)} MB/s steady`
                    : ""}
                </div>
              </div>
              <div className="stat min-w-0 py-3">
                <div className="stat-title text-[10px] uppercase tracking-wide">Pipelining</div>
                <div className="stat-value text-sm font-mono">
                  {pipe ? (pipe.recommendEnabled ? `Depth ${pipe.recommendedDepth}` : "Off") : "—"}
                </div>
                <div className="stat-desc font-mono tabular-nums">
                  {pipe
                    ? pipe.recommendEnabled
                      ? `≈ +${pipeGainPct}% vs off`
                      : "no real gain"
                    : "not tested"}
                </div>
              </div>
              {result.latency && (
                <div className="stat min-w-0 py-3">
                  <div className="stat-title text-[10px] uppercase tracking-wide">Latency</div>
                  <div className="stat-value text-sm font-mono">{result.latency.avgMs} ms</div>
                  <div className="stat-desc font-mono tabular-nums">
                    {result.latency.minMs} ms min
                  </div>
                </div>
              )}
              {result.providerConnectionCap != null && (
                <div className="stat min-w-0 py-3">
                  <div className="stat-title text-[10px] uppercase tracking-wide">Provider cap</div>
                  <div className="stat-value text-sm font-mono">{result.providerConnectionCap}</div>
                  <div className="stat-desc">max at once</div>
                </div>
              )}
              <div className="stat min-w-0 py-3">
                <div className="stat-title text-[10px] uppercase tracking-wide">Data used</div>
                <div className="stat-value text-sm font-mono">
                  {formatBytes(result.dataUsedBytes)}
                </div>
                <div className="stat-desc">whole run{elapsedLabel ? ` · ${elapsedLabel}` : ""}</div>
              </div>
            </div>
          ) : (
            <div className="mt-3.5 text-sm leading-relaxed text-base-content/80">
              Latency measured{result.latency ? ` — ${result.latency.avgMs} ms avg` : ""}. Download
              something first to get a transfer-connection recommendation.
            </div>
          )}

          {!result.pipeliningOnly &&
            result.throughputTested &&
            recommended != null &&
            !result.verificationRun && (
              <p className="mt-3 text-xs leading-relaxed text-base-content/60">
                We ramp connections, measure a few seconds at each step (warmup excluded), then pick
                the smallest count near peak speed. Most of the run is below that rate, so data used
                is much less than peak MB/s × elapsed time.
              </p>
            )}

          {!result.pipeliningOnly && pipe && (
            <div className="mt-3 text-sm leading-relaxed text-base-content/80">
              {pipe.recommendEnabled ? (
                <>
                  Turn on{" "}
                  <strong className="font-semibold text-base-content">Queue pipelining</strong> at
                  depth{" "}
                  <strong className="font-semibold text-base-content">
                    {pipe.recommendedDepth}
                  </strong>{" "}
                  for faster queue imports — not WebDAV playback.
                </>
              ) : (
                <>Queue pipelining didn’t help here — leave it off.</>
              )}
            </div>
          )}
          {!result.pipeliningOnly && !pipe && result.throughputTested && recommended != null && (
            <div className="mt-3 text-sm leading-relaxed text-base-content/80">
              Pipelining wasn’t tested this run. Raise the data budget, or use{" "}
              <strong className="font-semibold text-base-content">Only tune pipelining</strong>{" "}
              after applying Transfer Connections.
            </div>
          )}

          {result.warnings.length > 0 && (
            <Alert variant="warning" className="alert-soft mt-3 items-start py-3 text-xs">
              <ul className="list-disc space-y-1 pl-4 leading-relaxed">
                {result.warnings.map((w, i) => (
                  <li key={i}>{w}</li>
                ))}
              </ul>
            </Alert>
          )}

          {(canApply || (recommended != null && !result.verificationRun)) && (
            <div className="mt-3.5 flex flex-wrap gap-2">
              <Button
                variant={applied ? "secondary" : "primary"}
                onClick={() => {
                  onApply();
                  setApplied(true);
                }}
              >
                {applied && <Icon name="check" className="!text-[18px]" />}
                {applied
                  ? "Applied — review & save"
                  : result.pipeliningOnly
                    ? "Apply pipelining"
                    : "Apply transfer recommendation"}
              </Button>
              {recommended != null && !result.verificationRun && (
                <Button
                  variant="outline"
                  onClick={() => onVerify(recommended)}
                  disabled={isBenchmarking}
                >
                  Verify at {recommended} connection{recommended === 1 ? "" : "s"}
                </Button>
              )}
            </div>
          )}
        </>
      )}
    </div>
  );
}

function SweepChart({
  points,
  recommended,
}: {
  points: BenchmarkSweepPoint[];
  recommended: number | null;
}) {
  const max = Math.max(...points.map((p) => p.megaBytesPerSec), 0.0001);
  return (
    <div className="mt-4">
      <div className="flex h-[150px] items-end gap-2">
        {points.map((p, i) => {
          const isRec = recommended != null && p.connections === recommended;
          const height = Math.max(4, Math.round((p.megaBytesPerSec / max) * 104));
          return (
            <div key={i} className="flex min-w-0 flex-1 flex-col items-center gap-1.5">
              <span
                className={`font-mono text-[10.5px] tabular-nums whitespace-nowrap ${isRec ? "font-semibold text-primary" : "text-base-content/45"}`}
              >
                {p.megaBytesPerSec >= 10
                  ? p.megaBytesPerSec.toFixed(0)
                  : p.megaBytesPerSec.toFixed(1)}
              </span>
              <div
                className={`w-full max-w-8 rounded-t transition-all duration-200 ease-in-out ${isRec ? "bg-primary" : "bg-base-content/25"}`}
                style={{ height: `${height}px` }}
                title={`${p.connections} connections → ${p.megaBytesPerSec.toFixed(1)} MB/s`}
              />
              <span
                className={`font-mono text-[11px] tabular-nums ${isRec ? "font-semibold text-primary" : "text-base-content/45"}`}
              >
                {p.connections}
              </span>
            </div>
          );
        })}
      </div>
      <div className="mt-2 flex flex-col gap-0.5 sm:flex-row sm:items-baseline sm:justify-between sm:gap-2.5">
        <span className="text-[11px] text-base-content/45">
          Steady MB/s in a short window at each connection count (warmup excluded)
        </span>
        {recommended != null && (
          <span className="text-[11px] text-base-content/45">recommended: {recommended}</span>
        )}
      </div>
    </div>
  );
}

function DepthChart({ pipe }: { pipe: BenchmarkPipelining }) {
  const points = [
    { label: "Off", megaBytesPerSec: pipe.baselineMegaBytesPerSec, rec: !pipe.recommendEnabled },
    ...pipe.tested.map((t) => ({
      label: String(t.depth),
      megaBytesPerSec: t.megaBytesPerSec,
      rec: pipe.recommendEnabled && t.depth === pipe.recommendedDepth,
    })),
  ];
  const max = Math.max(...points.map((p) => p.megaBytesPerSec), 0.0001);
  return (
    <div className="mt-4">
      <div className="flex h-[150px] items-end gap-2">
        {points.map((p, i) => {
          const height = Math.max(4, Math.round((p.megaBytesPerSec / max) * 104));
          return (
            <div key={i} className="flex min-w-0 flex-1 flex-col items-center gap-1.5">
              <span
                className={`font-mono text-[10.5px] tabular-nums whitespace-nowrap ${p.rec ? "font-semibold text-primary" : "text-base-content/45"}`}
              >
                {p.megaBytesPerSec >= 10
                  ? p.megaBytesPerSec.toFixed(0)
                  : p.megaBytesPerSec.toFixed(1)}
              </span>
              <div
                className={`w-full max-w-8 rounded-t transition-all duration-200 ease-in-out ${p.rec ? "bg-primary" : "bg-base-content/25"}`}
                style={{ height: `${height}px` }}
                title={`${p.label} → ${p.megaBytesPerSec.toFixed(1)} MB/s`}
              />
              <span
                className={`font-mono text-[11px] tabular-nums ${p.rec ? "font-semibold text-primary" : "text-base-content/45"}`}
              >
                {p.label}
              </span>
            </div>
          );
        })}
      </div>
      <div className="mt-2 flex flex-col gap-0.5 sm:flex-row sm:items-baseline sm:justify-between sm:gap-2.5">
        <span className="text-[11px] text-base-content/45">
          Steady MB/s in a short window at each pipeline depth (warmup excluded)
        </span>
        <span className="text-[11px] text-base-content/45">
          {pipe.recommendEnabled ? `best: depth ${pipe.recommendedDepth}` : "best: off"}
        </span>
      </div>
    </div>
  );
}

export function isUsenetSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return (
    config["usenet.providers"] !== newConfig["usenet.providers"] ||
    config["usenet.queue-pipelining.enabled"] !== newConfig["usenet.queue-pipelining.enabled"] ||
    config["usenet.queue-pipelining.depth"] !== newConfig["usenet.queue-pipelining.depth"] ||
    config["usenet.cascade.enabled"] !== newConfig["usenet.cascade.enabled"] ||
    config["usenet.cascade.retry-primary-on-miss"] !==
      newConfig["usenet.cascade.retry-primary-on-miss"] ||
    config["usenet.article-miss-cache-ttl-seconds"] !==
      newConfig["usenet.article-miss-cache-ttl-seconds"] ||
    config["usenet.article-miss-cache-max-entries"] !==
      newConfig["usenet.article-miss-cache-max-entries"]
  );
}

export function isArticleMissCacheTtl(value: string) {
  if (!isPositiveInteger(value)) return false;
  const num = Number(value);
  return num >= 30 && num <= 86400;
}

export function isArticleMissCacheMaxEntries(value: string) {
  if (!isPositiveInteger(value)) return false;
  const num = Number(value);
  return num >= 100 && num <= 1_000_000;
}
