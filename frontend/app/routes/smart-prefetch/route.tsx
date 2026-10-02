import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { useIsReadOnly } from "~/auth/authorization";
import { Icon } from "~/components/ui";
import { settingsPath } from "~/navigation/settings-tabs";
import { withUrlBase } from "~/utils/url-base";
import { PrefetchQueue } from "~/components/prefetch-queue";

type Job = {
  id: string;
  itemId: string;
  displayName?: string;
  source?: string;
  reason?: string;
  trigger: string;
  state: string;
  start: number;
  length: number;
  committedBytes: number;
  fileSize?: number;
  error?: string | null;
  updated: number;
};
type Status = {
  available: boolean;
  healthy: boolean;
  paused: boolean;
  initializationError?: string | null;
  runtimeError?: string | null;
  lastError?: string | null;
  lastSuccess?: string | null;
  dailyBudgetUsed?: number;
  settings?: { enabled?: boolean; dailyByteBudget?: number; maxConcurrentJobs?: number };
  jobs: Job[];
};
const terminal = new Set(["completed", "failed", "cancelled", "expired"]);
const filterStates = ["all", "running", "queued", "deferred", "paused", "failed"];
const historyStates = ["all", "completed", "failed", "cancelled", "expired"];
const format = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
function bytes(value?: number | null) {
  if (value == null || !Number.isFinite(value)) return "—";
  if (value < 1000) return `${format.format(value)} B`;
  const unit = Math.min(4, Math.floor(Math.log(value) / Math.log(1000)));
  return `${format.format(value / 1000 ** unit)} ${["B", "KB", "MB", "GB", "TB"][unit]}`;
}
function when(value: number) {
  return Number.isFinite(value) ? new Date(value).toLocaleString() : "—";
}
function coverage(job: Job) {
  return job.fileSize && job.fileSize > 0
    ? Math.min(100, Math.max(0, Math.round((job.committedBytes / job.fileSize) * 100)))
    : null;
}
function badge(state: string) {
  const colors: Record<string, string> = {
    running: "badge-info",
    completed: "badge-success",
    failed: "badge-error",
    deferred: "badge-warning",
    paused: "badge-warning",
    queued: "badge-primary",
  };
  return <span className={`badge badge-sm ${colors[state] ?? "badge-ghost"}`}>{state}</span>;
}
function Card({
  label,
  value,
  foot,
  color = "",
}: {
  label: string;
  value: string;
  foot: string;
  color?: string;
}) {
  return (
    <div className="rounded-xl border border-base-content/10 bg-base-200/50 p-4">
      <p className="text-xs font-semibold text-base-content/65">{label}</p>
      <p className={`mt-2 text-2xl font-bold ${color}`}>{value}</p>
      <p className="mt-1 text-xs text-base-content/50">{foot}</p>
    </div>
  );
}
function JobRow({ job }: { job: Job }) {
  const pct = coverage(job);
  return (
    <article className="border-b border-base-content/10 p-4 last:border-0">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0 font-semibold">{job.displayName || job.itemId}</div>
        {badge(job.state)}
      </div>
      <p className="mt-1 text-xs text-base-content/55">
        {job.source || job.trigger} · {when(job.updated)}
      </p>
      {pct !== null && (
        <div
          className="mt-3 h-1.5 overflow-hidden rounded-full bg-base-content/10"
          aria-label={`${pct}% whole-file cache coverage`}
        >
          <div className="h-full rounded-full bg-primary" style={{ width: `${pct}%` }} />
        </div>
      )}
      <div className="mt-2 flex justify-between gap-2 text-xs text-base-content/65">
        <span>
          {bytes(job.committedBytes)}
          {job.fileSize ? ` of ${bytes(job.fileSize)}` : ""} cached file bytes
        </span>
        {pct !== null && <span>{pct}% whole-file coverage</span>}
      </div>
      <details className="mt-2 text-xs text-base-content/70">
        <summary className="cursor-pointer font-semibold text-primary">Details</summary>
        <div className="mt-2 rounded-lg bg-base-300/65 p-3">
          <p>
            Requested range: {bytes(job.start)} to{" "}
            {job.length === 0 ? "end of file" : bytes(job.start + job.length)}
          </p>
          {job.reason && <p>Reason: {job.reason}</p>}
          {job.error && <p className="text-error">{job.error}</p>}
          <p className="break-all">Item ID: {job.itemId}</p>
        </div>
      </details>
    </article>
  );
}
export default function SmartPrefetchActivityPage() {
  const readOnly = useIsReadOnly();
  const [status, setStatus] = useState<Status | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [view, setView] = useState<"activity" | "history">("activity");
  const [search, setSearch] = useState("");
  const [filter, setFilter] = useState("all");
  const [page, setPage] = useState(0);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const response = await fetch(withUrlBase("/api/prefetch"), signal ? { signal } : {});
    if (!response.ok)
      throw new Error(`Could not load Smart Prefetch activity (${response.status}).`);
    const body = (await response.json()) as Status;
    if (!signal?.aborted) {
      setStatus(body);
      setError(null);
    }
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    const tick = () =>
      void refresh(controller.signal).catch((cause) => {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Could not load activity.");
      });
    tick();
    const timer = setInterval(tick, 10_000);
    return () => {
      controller.abort();
      clearInterval(timer);
    };
  }, [refresh]);
  const operate = async (operation: string) => {
    setBusy(true);
    try {
      const response = await fetch(withUrlBase("/api/prefetch/operations"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ operation }),
      });
      if (!response.ok)
        throw new Error(`Could not ${operation.replaceAll("-", " ")} (${response.status}).`);
      await refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Warming operation failed.");
    } finally {
      setBusy(false);
    }
  };
  const jobs = status?.jobs ?? [];
  const active = jobs.filter((job) => !terminal.has(job.state));
  const history = jobs.filter((job) => terminal.has(job.state));
  const current = view === "activity" ? active : history;
  const visible = useMemo(
    () =>
      current.filter(
        (job) =>
          (filter === "all" || job.state === filter) &&
          `${job.displayName ?? ""} ${job.itemId} ${job.source ?? job.trigger}`
            .toLowerCase()
            .includes(search.toLowerCase()),
      ),
    [current, filter, search],
  );
  const lastPage = Math.max(0, Math.ceil(visible.length / 25) - 1);
  const shownPage = Math.min(page, lastPage);
  const pageJobs = visible.slice(shownPage * 25, (shownPage + 1) * 25);
  const running = active.filter((job) => job.state === "running").length;
  const today = new Date().toDateString();
  const completedToday = history.filter(
    (job) => job.state === "completed" && new Date(job.updated).toDateString() === today,
  ).length;
  const budget = status?.settings?.dailyByteBudget;
  const used = status?.dailyBudgetUsed;
  const pct = budget && used != null ? Math.min(100, (used / budget) * 100) : 0;
  const unavailable = !status?.available || status?.healthy === false;
  return (
    <div className="mx-auto max-w-7xl space-y-5 px-4 py-6 sm:px-6 lg:px-8">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-xs font-bold uppercase tracking-widest text-primary">
            Storage &amp; activity
          </p>
          <h1 className="mt-1 text-3xl font-bold tracking-tight">Smart Prefetch</h1>
          <p className="mt-1 text-sm text-base-content/65">
            Follow warming in real time and review recent outcomes.
          </p>
        </div>
        <div className="flex gap-2">
          <button
            type="button"
            className="btn btn-sm btn-outline"
            onClick={() => void refresh().catch((cause) => setError(String(cause)))}
          >
            <Icon name="refresh" /> Refresh
          </button>
          <Link className="btn btn-sm btn-outline" to={settingsPath("streaming")}>
            <Icon name="settings" /> Prefetch settings
          </Link>
        </div>
      </div>
      {error && (
        <div className="alert alert-error text-sm" role="alert">
          {error}
        </div>
      )}
      {status?.initializationError && (
        <div className="alert alert-warning text-sm">{status.initializationError}</div>
      )}
      {status?.runtimeError && (
        <div className="alert alert-warning text-sm">{status.runtimeError}</div>
      )}
      {status?.lastError && (
        <div className="alert alert-warning text-sm">Policy refresh: {status.lastError}</div>
      )}
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Card
          label="Queue status"
          value={
            !status ? "Loading…" : unavailable ? "Unavailable" : status.paused ? "Paused" : "Active"
          }
          foot={
            status?.lastSuccess
              ? `Last policy refresh ${new Date(status.lastSuccess).toLocaleString()}`
              : "No successful policy refresh yet"
          }
          color={status && !unavailable && !status.paused ? "text-success" : "text-warning"}
        />
        <Card
          label="Warming now"
          value={`${running}${status?.settings?.maxConcurrentJobs ? ` / ${status.settings.maxConcurrentJobs} slots` : ""}`}
          foot={`${active.filter((job) => job.state === "queued").length} waiting in queue`}
        />
        <Card
          label="Completed today"
          value={String(completedToday)}
          foot="Within recent retained jobs"
        />
        <div className="rounded-xl border border-base-content/10 bg-base-200/50 p-4">
          <p className="text-xs font-semibold text-base-content/65">Daily provider budget</p>
          <p className="mt-2 text-2xl font-bold">
            {status?.available ? bytes(used) : "—"}{" "}
            <span className="text-sm font-normal text-base-content/60">
              / {budget === 0 ? "unlimited" : bytes(budget)}
            </span>
          </p>
          <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-base-content/10">
            <div className="h-full rounded-full bg-warning" style={{ width: `${pct}%` }} />
          </div>
          <p className="mt-1 text-xs text-base-content/50">Accounting resets at midnight UTC</p>
        </div>
      </div>
      <div className="tabs tabs-border border-b border-base-content/10" role="tablist">
        <button
          type="button"
          role="tab"
          aria-selected={view === "activity"}
          className={`tab ${view === "activity" ? "tab-active" : ""}`}
          onClick={() => {
            setView("activity");
            setSearch("");
            setFilter("all");
            setPage(0);
          }}
        >
          Activity · {active.length}
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={view === "history"}
          className={`tab ${view === "history" ? "tab-active" : ""}`}
          onClick={() => {
            setView("history");
            setSearch("");
            setFilter("all");
            setPage(0);
          }}
        >
          Warming history · {history.length}
        </button>
      </div>
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 className="text-lg font-semibold">
            {view === "activity" ? "Current warming" : "Warming history"}
          </h2>
          <p className="text-xs text-base-content/55">
            {view === "activity"
              ? "Current queue jobs refresh every 10 seconds."
              : "Recent finished and interrupted jobs retained by the warming queue."}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <input
            className="input input-sm input-bordered w-56"
            type="search"
            aria-label={`Search ${view}`}
            placeholder="Search media…"
            value={search}
            onChange={(event) => {
              setSearch(event.target.value);
              setPage(0);
            }}
          />
          <select
            className="select select-sm select-bordered"
            aria-label={`Filter ${view}`}
            value={filter}
            onChange={(event) => {
              setFilter(event.target.value);
              setPage(0);
            }}
          >
            {(view === "activity" ? filterStates : historyStates).map((state) => (
              <option value={state} key={state}>
                {state === "all" ? "All states" : state}
              </option>
            ))}
          </select>
        </div>
      </div>
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_19rem]">
        <section className="overflow-hidden rounded-xl border border-base-content/10 bg-base-200/50">
          <div className="border-b border-base-content/10 px-4 py-3 text-sm font-semibold">
            {view === "activity" ? "Warming queue" : "Recent outcomes"}{" "}
            <span className="float-right text-xs font-normal text-base-content/50">
              {visible.length} jobs
            </span>
          </div>
          {visible.length ? (
            pageJobs.map((job) => <JobRow key={job.id} job={job} />)
          ) : (
            <p className="px-4 py-16 text-center text-sm text-base-content/60">
              {status ? "No matching jobs." : "Loading warming jobs…"}
            </p>
          )}
          {visible.length > 25 && (
            <div className="flex items-center justify-between border-t border-base-content/10 p-3 text-xs">
              <button
                type="button"
                className="btn btn-xs btn-outline"
                disabled={shownPage === 0}
                onClick={() => setPage(shownPage - 1)}
              >
                Previous
              </button>
              <span>
                Page {shownPage + 1} of {lastPage + 1}
              </span>
              <button
                type="button"
                className="btn btn-xs btn-outline"
                disabled={shownPage === lastPage}
                onClick={() => setPage(shownPage + 1)}
              >
                Next
              </button>
            </div>
          )}
        </section>
        <div className="space-y-4">
          {view === "activity" ? (
            <section className="space-y-3 rounded-xl border border-base-content/10 bg-base-200/50 p-4">
              <h3 className="font-semibold">Queue controls</h3>
              <p className="text-xs text-base-content/60">
                Warming yields to foreground playback and respects provider limits.
              </p>
              <div className="flex justify-between text-xs">
                <span>State</span>
                <strong>
                  {status?.paused ? "Paused" : unavailable ? "Unavailable" : "Active"}
                </strong>
              </div>
              <div className="flex justify-between text-xs">
                <span>Concurrency</span>
                <strong>
                  {running} of {status?.settings?.maxConcurrentJobs ?? "—"} jobs
                </strong>
              </div>
              <button
                type="button"
                className="btn btn-sm btn-outline w-full"
                disabled={readOnly || busy || unavailable}
                onClick={() => void operate(status?.paused ? "resume-all" : "pause-all")}
              >
                {status?.paused ? "Resume all warming" : "Pause all warming"}
              </button>
              <button
                type="button"
                className="btn btn-sm btn-outline w-full"
                disabled={readOnly || busy || unavailable}
                onClick={() => void operate("sync")}
              >
                Sync Plex policies now
              </button>
            </section>
          ) : (
            <section className="rounded-xl border border-base-content/10 bg-base-200/50 p-4">
              <h3 className="font-semibold">About this history</h3>
              <p className="mt-2 text-xs text-base-content/60">
                Cached bytes show whole-file coverage after each job, including bytes already
                present. A completed range may leave some of the file uncached.
              </p>
              <p className="mt-2 text-xs text-base-content/60">
                The queue keeps a bounded set of recent outcomes. This is not a permanent audit log.
              </p>
            </section>
          )}
          <section className="rounded-xl border border-base-content/10 bg-base-200/50 p-4">
            <h3 className="font-semibold">Why a job might wait</h3>
            <p className="mt-2 text-xs text-base-content/60">
              Active playback, provider limits, exhausted budget, and unavailable sources can pause
              warming.
            </p>
          </section>
        </div>
      </div>
      {!readOnly && (
        <details
          className="collapse collapse-arrow rounded-xl border border-base-content/10 bg-base-200/50"
          onToggle={(event) => setAdvancedOpen(event.currentTarget.open)}
        >
          <summary className="collapse-title text-sm font-semibold">
            Advanced warming controls
          </summary>
          <div className="collapse-content">{advancedOpen && <PrefetchQueue />}</div>
        </details>
      )}
    </div>
  );
}
