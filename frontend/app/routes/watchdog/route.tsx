import { redirect } from "react-router";
import type { Route } from "./+types/route";
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  backendClient,
  type WatchdogEntry,
  type WatchdogOutcome,
} from "~/clients/backend-client.server";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { Alert, Badge, Icon } from "~/components/ui";
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
              <h2 className="text-base font-semibold tracking-tight text-base-content">Watchdog</h2>
              <p className="mt-1 text-xs text-base-content/50">
                Live playback resolution log. Persisted across restarts.
              </p>
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
                className="btn btn-sm btn-primary join-item gap-2"
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

          <div className="join flex-wrap">
            {FILTER_OPTIONS.map((option) => (
              <FilterChip
                key={option.key}
                active={filter === option.key}
                onClick={() => setFilter(option.key)}
                count={filterCounts[option.key]}
              >
                {option.label}
              </FilterChip>
            ))}
          </div>

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

  return (
    <div className="card min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4 md:p-5">
        <div className="flex flex-wrap items-center justify-between gap-3 max-[899px]:gap-2">
          <div className="flex min-w-0 flex-1 items-center gap-2.5 max-[899px]:basis-full">
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
              className="font-mono text-[11px] tabular-nums text-base-content/50"
              title={new Date(group.firstAt * 1000).toLocaleString()}
            >
              {formatAge(group.firstAt)}
            </span>
          </div>
        </div>

        {winner && (
          <div className="alert alert-soft flex min-h-0 flex-wrap items-center gap-2 py-2 text-xs">
            <span className="text-base-content/60">Resolved via</span>
            <span className="font-semibold text-base-content">{winner.indexerName}</span>
            <span className="text-base-content/30">·</span>
            <span className="font-mono tabular-nums text-base-content/70">
              {winner.durationMs}ms
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

        <div className="-mx-4 -mb-4 border-t border-base-content/10 md:-mx-5 md:-mb-5">
          <div className="hidden min-[900px]:block overflow-x-auto">
            <table className="table table-xs w-full text-xs">
              <thead>
                <tr>
                  <th className="w-8 px-2.5 py-2 text-left text-[10px] font-semibold uppercase tracking-wider whitespace-nowrap text-base-content/50 tabular-nums first:pl-4 last:pr-4 last:text-right">
                    #
                  </th>
                  <th className="max-w-60 px-2.5 py-2 text-left text-[10px] font-medium uppercase tracking-wider whitespace-nowrap text-base-content/50 first:pl-4 last:pr-4 last:text-right">
                    Candidate
                  </th>
                  <th className="max-w-[110px] px-2.5 py-2 text-left text-[10px] font-medium uppercase tracking-wider whitespace-nowrap text-base-content/50 first:pl-4 last:pr-4 last:text-right">
                    Indexer
                  </th>
                  <th className="max-w-[140px] px-2.5 py-2 text-left text-[10px] font-medium uppercase tracking-wider whitespace-nowrap text-base-content/50 first:pl-4 last:pr-4 last:text-right">
                    Provider
                  </th>
                  <th className="w-[72px] px-2.5 py-2 text-left text-[10px] font-medium uppercase tracking-wider whitespace-nowrap text-base-content/50 first:pl-4 last:pr-4 last:text-right">
                    Size
                  </th>
                  <th className="w-[120px] px-2.5 py-2 text-left text-[10px] font-medium uppercase tracking-wider whitespace-nowrap text-base-content/50 first:pl-4 last:pr-4 last:text-right">
                    Outcome
                  </th>
                  <th className="max-w-[180px] px-2.5 py-2 text-left text-[10px] font-medium uppercase tracking-wider whitespace-nowrap text-base-content/50 first:pl-4 last:pr-4 last:text-right">
                    Reason
                  </th>
                  <th className="w-16 px-2.5 py-2 text-left text-[10px] font-medium uppercase tracking-wider whitespace-nowrap text-base-content/50 first:pl-4 last:pr-4 last:text-right">
                    Took
                  </th>
                </tr>
              </thead>
              <tbody>
                {group.attempts.map((a, i) => (
                  <tr key={i} className={a.isWinner ? "bg-success/5" : undefined}>
                    <td className="w-8 px-2.5 py-2 align-middle font-semibold tabular-nums text-base-content/50 first:pl-4">
                      {a.rankIndex + 1}
                    </td>
                    <td
                      className="max-w-60 truncate px-2.5 py-2 align-middle text-base-content"
                      title={a.candidateTitle}
                    >
                      {a.candidateTitle || "—"}
                    </td>
                    <td className="max-w-[110px] truncate whitespace-nowrap px-2.5 py-2 align-middle text-base-content/70">
                      {a.indexerName || "—"}
                    </td>
                    <td
                      className="max-w-[140px] truncate whitespace-nowrap px-2.5 py-2 align-middle text-base-content/70"
                      title={a.providerHost ?? undefined}
                    >
                      {a.providerNickname?.trim() || formatProviderShort(a.providerHost)}
                    </td>
                    <td className="w-[72px] whitespace-nowrap px-2.5 py-2 align-middle tabular-nums text-base-content/50">
                      {formatBytes(a.size)}
                    </td>
                    <td className="w-[120px] whitespace-nowrap px-2.5 py-2 align-middle text-base-content/70">
                      <OutcomeBadge outcome={a.outcome} winner={a.isWinner} />
                    </td>
                    <td
                      className="max-w-[180px] truncate whitespace-nowrap px-2.5 py-2 align-middle text-base-content/50"
                      title={a.failReason ?? undefined}
                    >
                      {a.failReason ?? "—"}
                    </td>
                    <td className="w-16 whitespace-nowrap px-2.5 py-2 align-middle text-right tabular-nums text-base-content/50 last:pr-4">
                      {a.durationMs}ms
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="flex flex-col gap-2 px-3.5 pt-3 pb-4 min-[900px]:hidden">
            {group.attempts.map((a, i) => (
              <div
                key={i}
                className={`card card-compact border border-base-content/10 bg-base-200 ${a.isWinner ? "border-success/30 bg-success/5" : ""}`}
              >
                <div className="card-body gap-1 p-3">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-[11px] font-semibold tabular-nums text-base-content/50">
                      #{a.rankIndex + 1}
                    </span>
                    <span
                      className="min-w-0 flex-1 truncate text-xs font-semibold text-base-content"
                      title={a.indexerName}
                    >
                      {a.indexerName || "—"}
                    </span>
                    <OutcomeBadge outcome={a.outcome} winner={a.isWinner} />
                  </div>
                  <div
                    className="mb-1 text-xs leading-snug break-words text-base-content/70"
                    title={a.candidateTitle}
                  >
                    {a.candidateTitle || "—"}
                  </div>
                  <div className="flex gap-1.5 text-[11px] tabular-nums text-base-content/50">
                    <span title={a.providerHost ?? undefined}>
                      📡 {a.providerNickname?.trim() || formatProviderShort(a.providerHost)}
                    </span>
                    <span className="text-base-content/40">·</span>
                    <span>{formatBytes(a.size)}</span>
                    <span className="text-base-content/40">·</span>
                    <span>{a.durationMs}ms</span>
                  </div>
                  {a.failReason && (
                    <div className="mt-1 rounded-box border border-base-content/10 bg-base-100 px-2 py-1 text-[11px] text-base-content/60 break-words">
                      {a.failReason}
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
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

function FilterChip({
  active,
  onClick,
  count,
  children,
}: {
  active: boolean;
  onClick: () => void;
  count: number;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      className={`btn btn-sm join-item gap-2 ${active ? "btn-primary" : "btn-ghost"}`}
      onClick={onClick}
    >
      <span>{children}</span>
      <span className="badge badge-xs badge-ghost font-mono tabular-nums">{count}</span>
    </button>
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
