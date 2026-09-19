import { redirect } from "react-router";
import type { Route } from "./+types/route";
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  backendClient,
  type WatchdogEntry,
  type WatchdogOutcome,
} from "~/clients/backend-client.server";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { Alert, Badge, Icon, PageHeader, RadioJoinFilter } from "~/components/ui";
import { useIsReadOnly } from "~/auth/authorization";
import { withUrlBase } from "~/utils/url-base";

const POLL_INTERVAL_MS = 3000;

export async function loader() {
  const [config, entries] = await Promise.all([
    backendClient.getConfig(["play.watchdog-enabled"]),
    backendClient.getWatchdogEntries(200),
  ]);
  const enabledRaw =
    config.find((x) => x.configName === "play.watchdog-enabled")?.configValue ?? "true";
  const isEnabled = enabledRaw.toLowerCase() === "true";
  if (!isEnabled) {
    return redirect("/queue");
  }
  return { entries };
}

type FilterKey = "all" | "live" | "resolved" | "failed" | "excluded";

const FILTER_OPTIONS: { key: FilterKey; label: string }[] = [
  { key: "all", label: "All" },
  { key: "live", label: "Live" },
  { key: "resolved", label: "Resolved" },
  { key: "failed", label: "Failed" },
  { key: "excluded", label: "Excluded" },
];

export default function Watchdog({ loaderData }: Route.ComponentProps) {
  const isReadOnly = useIsReadOnly();
  const [attempts, setAttempts] = useState<WatchdogEntry[]>(loaderData.entries);
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [filter, setFilter] = useState<FilterKey>("all");
  const [refreshing, setRefreshing] = useState(false);
  const [clearing, setClearing] = useState(false);
  const [showClearConfirm, setShowClearConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async (silent: boolean = false) => {
    if (!silent) setRefreshing(true);
    try {
      const r = await fetch(withUrlBase("/settings/watchdog-attempts?limit=200"));
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      // /settings/watchdog-attempts resource route returns { entries: WatchdogEntry[] }
      const data = (await r.json()) as { entries?: WatchdogEntry[] };
      const next: WatchdogEntry[] = data.entries ?? [];
      setAttempts((prev) => (attemptsEqual(prev, next) ? prev : next));
      setError(null);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      if (!silent) setRefreshing(false);
    }
  }, []);

  const performClear = useCallback(async () => {
    setShowClearConfirm(false);
    setClearing(true);
    try {
      const r = await fetch(withUrlBase("/settings/watchdog-attempts"), { method: "POST" });
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      setAttempts([]);
      setError(null);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setClearing(false);
    }
  }, []);

  useEffect(() => {
    if (!autoRefresh) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const loop = async () => {
      if (cancelled) return;
      await refresh(true);
      if (cancelled) return;
      timer = setTimeout(() => {
        void loop();
      }, POLL_INTERVAL_MS);
    };
    timer = setTimeout(() => {
      void loop();
    }, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [autoRefresh, refresh]);

  const groups = useMemo(() => groupByClick(attempts), [attempts]);
  const filteredGroups = useMemo(
    () => groups.filter((g) => matchesFilter(g, filter)),
    [groups, filter],
  );
  const stats = useMemo(() => computeStats(groups), [groups]);

  const filterCounts: Record<FilterKey, number> = {
    all: groups.length,
    live: stats.inFlight,
    resolved: stats.resolved,
    failed: stats.failed,
    excluded: stats.excluded,
  };

  return (
    <div className="flex min-h-full min-w-full flex-col gap-6 px-4 py-4 text-sm text-base-content/70 md:px-8">
      <div className="card border border-base-content/10 bg-base-100 shadow-sm">
        <div className="card-body gap-4 p-4 md:p-6">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <PageHeader
                title="Watchdog"
                subtitle="Live playback resolution log. Persisted across restarts."
              />
            </div>
            <div className="join flex w-full flex-wrap sm:w-auto">
              <button
                type="button"
                className={`btn btn-sm join-item gap-2 ${autoRefresh ? "btn-success" : "btn-ghost"}`}
                onClick={() => setAutoRefresh((v) => !v)}
                title={
                  autoRefresh
                    ? "Auto-refresh on. Click to pause."
                    : "Auto-refresh paused. Click to resume."
                }
              >
                <span
                  className={`status status-xs ${autoRefresh ? "status-success animate-pulse" : "status-neutral"}`}
                />
                {autoRefresh ? (refreshing ? "Refreshing…" : "Live") : "Paused"}
              </button>
              <button
                type="button"
                className="btn btn-sm join-item gap-2"
                onClick={() => void refresh()}
                disabled={refreshing || clearing}
                title="Refresh now."
              >
                <Icon
                  name="refresh"
                  className={`!text-[16px] ${refreshing ? "animate-spin" : ""}`}
                />
                Refresh
              </button>
              {!isReadOnly && (
                <button
                  type="button"
                  className="btn btn-sm btn-error join-item gap-2"
                  onClick={() => setShowClearConfirm(true)}
                  disabled={groups.length === 0 || clearing}
                  title="Permanently delete all watchdog entries."
                >
                  <Icon name="delete" className="!text-[16px]" />
                  {clearing ? "Clearing…" : "Clear log"}
                </button>
              )}
            </div>
          </div>

          <div className="stats stats-vertical w-full border border-base-content/10 shadow sm:stats-horizontal">
            <Stat label="Clicks" value={stats.total} />
            <Stat label="Resolved" value={stats.resolved} tone="ok" />
            <Stat label="Failed" value={stats.failed} tone="bad" />
            <Stat label="In flight" value={stats.inFlight} tone="warn" />
          </div>

          <RadioJoinFilter
            name="watchdog-filter"
            aria-label="Watchdog status filter"
            value={filter}
            onChange={setFilter}
            options={FILTER_OPTIONS.map((option) => ({
              id: option.key,
              label: `${option.label} ${filterCounts[option.key]}`,
            }))}
          />

          {error && (
            <Alert variant="danger" className="text-xs">
              Could not load: {error}
            </Alert>
          )}
        </div>
      </div>

      {filteredGroups.length === 0 ? (
        <div className="card border border-base-content/10 bg-base-100 shadow-sm">
          <div className="card-body items-center py-12 text-center text-base-content/50">
            {groups.length === 0
              ? "No watchdog entries recorded yet. Click Play in your client to see live activity here."
              : "No clicks match this filter."}
          </div>
        </div>
      ) : (
        <div className="flex flex-col gap-3.5">
          {filteredGroups.map((g) => (
            <ClickCard key={g.clickId} group={g} />
          ))}
        </div>
      )}

      <ConfirmModal
        show={showClearConfirm}
        title="Clear watchdog log?"
        message="Permanently delete all watchdog entries? This can't be undone."
        confirmText="Clear log"
        cancelText="Cancel"
        onCancel={() => setShowClearConfirm(false)}
        onConfirm={() => void performClear()}
      />
    </div>
  );
}

function ClickCard({ group }: { group: ClickGroup }) {
  const status: "win" | "loss" | "inflight" = group.hasWinner
    ? "win"
    : group.allResolved
      ? "loss"
      : "inflight";
  const winner = group.attempts.find((a) => a.isWinner);
  const [expanded, setExpanded] = useState(status === "inflight");

  return (
    <details
      className="min-w-0 border-b border-base-content/10 py-3"
      open={expanded}
      onToggle={(event) => setExpanded(event.currentTarget.open)}
    >
      <summary className="cursor-pointer list-none rounded-sm p-2 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary">
        <div className="flex flex-wrap items-center justify-between gap-3 max-[899px]:gap-2">
          <div className="flex min-w-0 flex-1 items-center gap-2.5 max-[899px]:basis-full">
            <Icon
              name={expanded ? "expand_more" : "chevron_right"}
              className="shrink-0 !text-[20px]"
            />
            <StatusPill status={status} />
            <div
              className="min-w-0 truncate text-[13px] font-semibold text-base-content max-[899px]:overflow-visible max-[899px]:whitespace-normal max-[899px]:break-words"
              title={group.requestedTitle}
            >
              {group.requestedTitle}
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge className="badge-ghost badge-sm lowercase">{group.contentType}</Badge>
            <Badge className="badge-ghost badge-sm">
              {group.attempts.length} attempt{group.attempts.length === 1 ? "" : "s"}
            </Badge>
            <span
              className="font-mono text-xs tabular-nums text-base-content/70"
              title={new Date(group.firstAt * 1000).toLocaleString()}
            >
              {formatAge(group.firstAt)}
            </span>
          </div>
        </div>

        {winner && (
          <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-base-content/70">
            <span>
              {winner.indexerName?.trim() && winner.indexerName.trim() !== "—"
                ? `Resolved via ${winner.indexerName}`
                : "Resolved · indexer unavailable"}
            </span>
            <span className="text-base-content/30">·</span>
            <span className="font-mono tabular-nums text-base-content/70">
              {formatAttemptDuration(winner.durationMs)}
            </span>
            {winner.size > 0 && (
              <>
                <span className="text-base-content/30">·</span>
                <span className="font-mono tabular-nums text-base-content/70">
                  {formatBytes(winner.size)}
                </span>
              </>
            )}
          </div>
        )}
      </summary>
      <ol
        className="ml-4 mt-3 border-l border-base-content/20 pl-5"
        aria-label={`Attempts for ${group.requestedTitle}`}
      >
        {group.attempts.map((attempt, index) => (
          <li key={`${attempt.rankIndex}-${index}`} className="relative min-w-0 pb-5 last:pb-2">
            <span
              aria-hidden="true"
              className={`absolute -left-[25px] top-1.5 h-2 w-2 rounded-full ${attempt.isWinner ? "bg-success" : "bg-base-content/60"}`}
            />
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <span className="font-mono tabular-nums">#{attempt.rankIndex + 1}</span>
              <OutcomeBadge outcome={attempt.outcome} winner={attempt.isWinner} />
              <span className="font-mono tabular-nums">
                {formatAttemptDuration(attempt.durationMs)}
              </span>
              <span className="font-mono tabular-nums">{formatBytes(attempt.size)}</span>
            </div>
            <p className="mt-2 break-words font-medium text-base-content [overflow-wrap:anywhere]">
              {attempt.candidateTitle || "Candidate unavailable"}
            </p>
            <dl className="mt-2 flex flex-wrap gap-x-5 gap-y-1 text-xs text-base-content/70">
              <div>
                <dt className="inline font-medium">Indexer: </dt>
                <dd className="inline break-all">
                  {attempt.indexerName?.trim() && attempt.indexerName.trim() !== "—"
                    ? attempt.indexerName
                    : "Unavailable"}
                </dd>
              </div>
              <div>
                <dt className="inline font-medium">Provider: </dt>
                <dd className="inline break-all">
                  {attempt.providerNickname?.trim() ||
                    (attempt.providerHost
                      ? formatProviderShort(attempt.providerHost)
                      : "Unavailable")}
                </dd>
              </div>
            </dl>
            {attempt.failReason && (
              <p className="mt-2 whitespace-pre-wrap break-words text-xs leading-relaxed text-base-content/80 [overflow-wrap:anywhere]">
                {attempt.failReason}
              </p>
            )}
          </li>
        ))}
      </ol>
    </details>
  );
}

function formatAttemptDuration(durationMs: number): string {
  return durationMs >= 1000 ? `${(durationMs / 1000).toFixed(1)}s` : `${durationMs}ms`;
}

function Stat({
  label,
  value,
  tone,
}: {
  label: string;
  value: number;
  tone?: "ok" | "bad" | "warn";
}) {
  const valueClass =
    tone === "ok"
      ? "text-success"
      : tone === "bad"
        ? "text-error"
        : tone === "warn"
          ? "text-warning"
          : "";
  return (
    <div className="stat px-4 py-2">
      <div className="stat-title text-[10px] uppercase tracking-wider">{label}</div>
      <div className={`stat-value font-mono text-xl ${valueClass}`}>{value}</div>
    </div>
  );
}

function StatusPill({ status }: { status: "win" | "loss" | "inflight" }) {
  const label = status === "win" ? "Resolved" : status === "loss" ? "Failed" : "Live";
  const cls =
    status === "win" ? "badge-success" : status === "loss" ? "badge-error" : "badge-ghost";
  return <span className={`badge badge-sm uppercase ${cls}`}>{label}</span>;
}

function OutcomeBadge({ outcome, winner }: { outcome: WatchdogOutcome; winner: boolean }) {
  if (winner) return <Badge className="badge-success badge-sm uppercase">winner</Badge>;
  const tone = outcomeToTone(outcome);
  const cls = tone === "ok" ? "badge-success" : tone === "warn" ? "badge-warning" : "badge-error";
  return <Badge className={`badge-sm uppercase ${cls}`}>{shortOutcome(outcome)}</Badge>;
}

function outcomeToTone(o: WatchdogOutcome): "ok" | "warn" | "bad" {
  switch (o) {
    case "QueueCompleted":
    case "PreVerifyAvailable":
      return "ok";
    case "BudgetTimeout":
    case "Cancelled":
    case "ExcludedByPattern":
      return "warn";
    default:
      return "bad";
  }
}

function shortOutcome(o: WatchdogOutcome): string {
  switch (o) {
    case "QueueCompleted":
      return "completed";
    case "QueueFailed":
      return "queue failed";
    case "EnqueueFailed":
      return "enqueue failed";
    case "PreVerifyDead":
      return "verify: dead";
    case "PreVerifyTimeout":
      return "verify: timeout";
    case "PreVerifyAvailable":
      return "verify: ok";
    case "BudgetTimeout":
      return "budget timeout";
    case "Cancelled":
      return "cancelled";
    case "ExcludedByPattern":
      return "excluded";
    default:
      return o;
  }
}

type ClickGroup = {
  clickId: string;
  firstAt: number;
  requestedTitle: string;
  contentType: string;
  hasWinner: boolean;
  allResolved: boolean;
  attempts: WatchdogEntry[];
};

function attemptsEqual(a: WatchdogEntry[], b: WatchdogEntry[]): boolean {
  if (a === b) return true;
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    // Both defined: arrays have equal length and i < a.length
    const x = a[i]!,
      y = b[i]!;
    if (x.clickId !== y.clickId) return false;
    if (x.rankIndex !== y.rankIndex) return false;
    if (x.outcome !== y.outcome) return false;
    if (x.isWinner !== y.isWinner) return false;
    if (x.attemptedAtUnix !== y.attemptedAtUnix) return false;
    if (x.durationMs !== y.durationMs) return false;
    if (x.size !== y.size) return false;
    if (x.failReason !== y.failReason) return false;
  }
  return true;
}

function groupByClick(list: WatchdogEntry[]): ClickGroup[] {
  const map = new Map<string, ClickGroup>();
  for (const a of list) {
    const g = map.get(a.clickId);
    if (g) {
      g.attempts.push(a);
      if (a.attemptedAtUnix > g.firstAt) g.firstAt = a.attemptedAtUnix;
      if (a.isWinner) g.hasWinner = true;
    } else {
      map.set(a.clickId, {
        clickId: a.clickId,
        firstAt: a.attemptedAtUnix,
        requestedTitle: a.requestedTitle,
        contentType: a.contentType,
        hasWinner: a.isWinner,
        allResolved: false,
        attempts: [a],
      });
    }
  }
  const arr = Array.from(map.values());
  for (const g of arr) {
    g.attempts.sort((x, y) => x.rankIndex - y.rankIndex);
    g.allResolved = g.attempts.every(isTerminal);
  }
  arr.sort((x, y) => y.firstAt - x.firstAt);
  return arr;
}

function isTerminal(a: WatchdogEntry): boolean {
  switch (a.outcome) {
    case "QueueCompleted":
    case "QueueFailed":
    case "EnqueueFailed":
    case "PreVerifyDead":
    case "PreVerifyTimeout":
    case "Cancelled":
    case "BudgetTimeout":
    case "ExcludedByPattern":
      return true;
    case "PreVerifyAvailable":
      return false;
    default:
      return false;
  }
}

function hasExclusion(g: ClickGroup): boolean {
  return g.attempts.some((a) => a.outcome === "ExcludedByPattern");
}

function matchesFilter(g: ClickGroup, f: FilterKey): boolean {
  switch (f) {
    case "all":
      return true;
    case "live":
      return !g.hasWinner && !g.allResolved;
    case "resolved":
      return g.hasWinner;
    case "failed":
      return !g.hasWinner && g.allResolved;
    case "excluded":
      return hasExclusion(g);
  }
}

function computeStats(groups: ClickGroup[]) {
  let resolved = 0,
    failed = 0,
    inFlight = 0,
    excluded = 0;
  for (const g of groups) {
    if (g.hasWinner) resolved++;
    else if (g.allResolved) failed++;
    else inFlight++;
    if (hasExclusion(g)) excluded++;
  }
  return { total: groups.length, resolved, failed, inFlight, excluded };
}

function formatProviderShort(raw: string | null | undefined): string {
  if (!raw) return "—";
  return raw
    .split(",")
    .map((h) => stripHost(h.trim()))
    .filter(Boolean)
    .join(" · ");
}

const GENERIC_HOST_PREFIXES = new Set([
  "news",
  "reader",
  "premium",
  "secure",
  "ssl",
  "nntp",
  "usenet",
  "block",
]);

function stripHost(host: string): string {
  if (!host) return "";
  const labels = host.split(".").filter(Boolean);
  if (labels.length === 0) return host;
  const first = labels[0];
  if (!first) return host;
  if (labels.length === 1) return first;
  const second = labels[1];
  if (!second) return first;
  if (labels.length === 2) return first;
  if (GENERIC_HOST_PREFIXES.has(first.toLowerCase())) return second;
  return first.length >= second.length ? first : second;
}

function formatBytes(bytes: number): string {
  if (bytes <= 0) return "—";
  const u = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  let v = bytes;
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2)} ${u[i]}`;
}

function formatAge(unixSeconds: number): string {
  const age = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
  if (age < 5) return "just now";
  if (age < 60) return `${age}s ago`;
  if (age < 3600) return `${Math.floor(age / 60)}m ago`;
  if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
  return `${Math.floor(age / 86400)}d ago`;
}
