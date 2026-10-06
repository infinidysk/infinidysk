import { redirect } from "react-router";
import type { Route } from "./+types/route";
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  backendClient,
  type WatchdogEntry,
  type WatchdogOutcome,
} from "~/clients/backend-client.server";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import {
  Alert,
  Badge,
  Button,
  Icon,
  PageHeader,
  PortalTooltip,
  RadioJoinFilter,
  Tooltip,
} from "~/components/ui";
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
  { key: "live", label: "In flight" },
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

  const activeFilterLabel = FILTER_OPTIONS.find((option) => option.key === filter)?.label ?? "";

  return (
    <section className="flex min-h-full min-w-0 flex-col gap-4 px-4 py-4 text-sm md:px-8">
      <PageHeader
        title="Watchdog"
        subtitle="Each play request and the releases tried to start it. Kept across restarts."
        actions={
          <>
            <Tooltip
              content={
                autoRefresh
                  ? "Updating every 3 seconds. Click to pause."
                  : "Updates paused. Click to resume."
              }
            >
              <button
                type="button"
                aria-pressed={autoRefresh}
                className={`btn btn-sm gap-2 max-sm:min-h-11 ${autoRefresh ? "border-primary/60 bg-primary/15 text-primary" : ""}`}
                onClick={() => setAutoRefresh((v) => !v)}
              >
                <span
                  aria-hidden="true"
                  className={`status status-sm ${autoRefresh ? "status-primary animate-pulse" : "status-neutral"}`}
                />
                {autoRefresh ? "Live" : "Paused"}
              </button>
            </Tooltip>
            <Button onClick={() => void refresh()} disabled={refreshing || clearing}>
              <Icon name="refresh" className={`!text-[16px] ${refreshing ? "animate-spin" : ""}`} />
              Refresh
            </Button>
            {!isReadOnly && groups.length > 0 && (
              <Button
                variant="danger"
                className="btn-soft"
                onClick={() => setShowClearConfirm(true)}
                disabled={clearing}
              >
                <Icon
                  name={clearing ? "progress_activity" : "delete"}
                  className={`!text-[16px] ${clearing ? "animate-spin" : ""}`}
                />
                {clearing ? "Clearing…" : "Clear log"}
              </Button>
            )}
          </>
        }
      />

      {error && (
        <Alert className="alert-soft" variant="danger">
          <Icon name="error" className="shrink-0 !text-[20px]" />
          <span>Could not refresh the watchdog log: {error}</span>
          <Button variant="ghost" onClick={() => void refresh()} disabled={refreshing}>
            Retry
          </Button>
        </Alert>
      )}

      <section className="card w-full border border-base-content/10 bg-base-100 shadow-sm">
        <div className="card-body grid grid-cols-2 gap-3 p-4 sm:grid-cols-4 md:p-6">
          <Stat
            icon="play_circle"
            iconClassName="text-base-content/50"
            title="Play requests"
            value={stats.total}
          />
          <Stat
            icon="check_circle"
            iconClassName="text-success"
            title="Resolved"
            value={stats.resolved}
            valueClassName="text-success"
          />
          <Stat
            icon="error"
            iconClassName="text-error"
            title="Failed"
            value={stats.failed}
            valueClassName="text-error"
          />
          <Stat
            icon="pending"
            iconClassName="text-info"
            title="In flight"
            value={stats.inFlight}
            valueClassName="text-info"
          />
        </div>
      </section>

      <section className="card w-full overflow-hidden border border-base-content/10 bg-base-100 shadow-sm">
        <div className="card-body gap-4 p-4 md:p-6">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <h2 className="card-title text-xl">Play requests</h2>
            <RadioJoinFilter
              name="watchdog-filter"
              aria-label="Watchdog status filter"
              value={filter}
              onChange={setFilter}
              options={FILTER_OPTIONS.filter(
                (option) =>
                  option.key === "all" || option.key === filter || filterCounts[option.key] > 0,
              ).map((option) => ({
                id: option.key,
                label: `${option.label} ${filterCounts[option.key].toLocaleString()}`,
              }))}
            />
          </div>

          {filteredGroups.length === 0 ? (
            <div className="flex flex-col items-center gap-2 rounded-box border border-dashed border-base-content/15 px-4 py-12 text-center">
              <Icon
                name={groups.length === 0 ? "monitor_heart" : "filter_alt_off"}
                className="!text-[36px] text-base-content/40"
              />
              <h3 className="text-base font-semibold text-base-content">
                {groups.length === 0 ? "No play requests yet" : "No matching play requests"}
              </h3>
              <p className="max-w-prose text-base-content/60">
                {groups.length === 0
                  ? "Press Play in your media client. Each request and the releases tried for it will appear here."
                  : `No play requests match the ${activeFilterLabel} filter.`}
              </p>
              {groups.length === 0 ? (
                <Button className="mt-2" onClick={() => void refresh()} disabled={refreshing}>
                  <Icon
                    name="refresh"
                    className={`!text-[16px] ${refreshing ? "animate-spin" : ""}`}
                  />
                  Refresh
                </Button>
              ) : (
                <Button className="mt-2" onClick={() => setFilter("all")}>
                  Show all requests
                </Button>
              )}
            </div>
          ) : (
            <ul className="-mx-4 -mb-4 divide-y divide-base-content/10 border-t border-base-content/10 md:-mx-6 md:-mb-6">
              {filteredGroups.map((g) => (
                <li key={g.clickId}>
                  <ClickCard group={g} />
                </li>
              ))}
            </ul>
          )}
        </div>
      </section>

      <ConfirmModal
        show={showClearConfirm}
        title="Clear watchdog log?"
        message={`Permanently delete ${groups.length.toLocaleString()} play request${groups.length === 1 ? "" : "s"} and ${attempts.length.toLocaleString()} recorded attempt${attempts.length === 1 ? "" : "s"}? This can't be undone.`}
        confirmText="Clear log"
        cancelText="Cancel"
        onCancel={() => setShowClearConfirm(false)}
        onConfirm={() => void performClear()}
      />
    </section>
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
      className="min-w-0"
      open={expanded}
      onToggle={(event) => setExpanded(event.currentTarget.open)}
    >
      <summary className="cursor-pointer list-none px-4 py-3 transition-colors hover:bg-base-200/60 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary md:px-6">
        <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2">
          <div className="flex min-w-0 flex-1 basis-72 items-center gap-2.5">
            <Icon
              name="chevron_right"
              className={`shrink-0 !text-[20px] text-base-content/60 transition-transform ${expanded ? "rotate-90" : ""}`}
            />
            <StatusPill status={status} />
            <span className="min-w-0 break-words font-semibold text-base-content [overflow-wrap:anywhere]">
              {group.requestedTitle}
            </span>
          </div>
          <div className="flex flex-wrap items-center gap-2 pl-[30px] sm:pl-0">
            <Badge className="badge-ghost badge-sm">{formatContentType(group.contentType)}</Badge>
            <Badge className="badge-ghost badge-sm">
              {group.attempts.length} attempt{group.attempts.length === 1 ? "" : "s"}
            </Badge>
            <PortalTooltip content={new Date(group.firstAt * 1000).toLocaleString()}>
              <time
                dateTime={new Date(group.firstAt * 1000).toISOString()}
                className="text-xs tabular-nums text-base-content/60"
              >
                {formatAge(group.firstAt)}
              </time>
            </PortalTooltip>
          </div>
        </div>

        {winner && (
          <div className="mt-1.5 flex flex-wrap items-center gap-2 pl-[30px] text-xs text-base-content/60">
            <span>
              {winner.indexerName?.trim() && winner.indexerName.trim() !== "—"
                ? `Resolved via ${winner.indexerName}`
                : "Resolved · indexer unavailable"}
            </span>
            <span className="text-base-content/30">·</span>
            <span className="tabular-nums">{formatAttemptDuration(winner.durationMs)}</span>
            {winner.size > 0 && (
              <>
                <span className="text-base-content/30">·</span>
                <span className="tabular-nums">{formatBytes(winner.size)}</span>
              </>
            )}
          </div>
        )}
      </summary>
      <ol
        className="mb-4 ml-[27px] mr-4 mt-1 border-l border-base-content/15 pl-5 md:ml-[35px] md:mr-6"
        aria-label={`Attempts for ${group.requestedTitle}`}
      >
        {group.attempts.map((attempt, index) => (
          <li key={`${attempt.rankIndex}-${index}`} className="relative min-w-0 pb-5 last:pb-1">
            <span
              aria-hidden="true"
              className={`absolute -left-[25px] top-1.5 h-2 w-2 rounded-full ${toneDotClass[attempt.isWinner ? "ok" : outcomeToTone(attempt.outcome)]}`}
            />
            <div className="flex flex-wrap items-center gap-2 text-xs text-base-content/70">
              <span className="tabular-nums text-base-content/50">#{attempt.rankIndex + 1}</span>
              <OutcomeBadge outcome={attempt.outcome} winner={attempt.isWinner} />
              <span className="tabular-nums">{formatAttemptDuration(attempt.durationMs)}</span>
              <span className="text-base-content/30">·</span>
              <span className="tabular-nums">{formatBytes(attempt.size)}</span>
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
              <p className="mt-2 whitespace-pre-wrap break-words rounded-box bg-base-200 px-3 py-2 text-xs leading-relaxed text-base-content/80 [overflow-wrap:anywhere]">
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
  icon,
  iconClassName,
  title,
  value,
  valueClassName = "",
}: {
  icon: string;
  iconClassName: string;
  title: string;
  value: number;
  valueClassName?: string;
}) {
  // Status colour only when there is something to report; zero counts stay neutral.
  const muted = value === 0 && valueClassName !== "";
  return (
    <div className="min-w-0 py-2">
      <div className={`mb-1 ${muted ? "text-base-content/30" : iconClassName}`}>
        <Icon name={icon} filled={valueClassName !== ""} className="!text-[22px]" />
      </div>
      <div className="text-xs text-base-content/70">{title}</div>
      <div
        className={`font-mono text-2xl font-semibold tabular-nums ${muted ? "text-base-content/40" : valueClassName}`}
      >
        {value.toLocaleString()}
      </div>
    </div>
  );
}

function StatusPill({ status }: { status: "win" | "loss" | "inflight" }) {
  if (status === "inflight")
    return (
      <Badge className="badge-info badge-soft badge-sm shrink-0 gap-1.5">
        <span aria-hidden="true" className="status status-info status-xs animate-pulse" />
        In flight
      </Badge>
    );
  return status === "win" ? (
    <Badge className="badge-success badge-soft badge-sm shrink-0">Resolved</Badge>
  ) : (
    <Badge className="badge-error badge-soft badge-sm shrink-0">Failed</Badge>
  );
}

const toneBadgeClass = {
  ok: "badge-success",
  warn: "badge-warning",
  bad: "badge-error",
} as const;

const toneDotClass = {
  ok: "bg-success",
  warn: "bg-warning",
  bad: "bg-error",
} as const;

function OutcomeBadge({ outcome, winner }: { outcome: WatchdogOutcome; winner: boolean }) {
  if (winner)
    return (
      <Badge className="badge-success badge-soft badge-sm gap-1">
        <Icon name="emoji_events" filled className="!text-[14px]" />
        Winner
      </Badge>
    );
  return (
    <Badge className={`badge-soft badge-sm ${toneBadgeClass[outcomeToTone(outcome)]}`}>
      {shortOutcome(outcome)}
    </Badge>
  );
}

function formatContentType(value: string): string {
  const trimmed = value.trim();
  return trimmed ? trimmed.charAt(0).toUpperCase() + trimmed.slice(1).toLowerCase() : "Unknown";
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
      return "Completed";
    case "QueueFailed":
      return "Queue failed";
    case "EnqueueFailed":
      return "Couldn't queue";
    case "PreVerifyDead":
      return "Articles missing";
    case "PreVerifyTimeout":
      return "Check timed out";
    case "PreVerifyAvailable":
      return "Articles available";
    case "BudgetTimeout":
      return "Out of time";
    case "Cancelled":
      return "Cancelled";
    case "ExcludedByPattern":
      return "Excluded";
    default:
      return "Unknown outcome";
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
