import { useCallback, useEffect, useRef, useState } from "react";
import { withUrlBase } from "~/utils/url-base";

export type MigrationStepStatus = "pending" | "running" | "completed" | "failed";

export type MigrationStep = {
  id: string;
  name: string;
  status: MigrationStepStatus;
  slow: boolean;
  startedAt: number | null;
  finishedAt: number | null;
};

export type MigrationStatus = {
  state: "running" | "completed" | "failed";
  startedAt: number;
  completed: number;
  total: number;
  currentStep: string | null;
  error: string | null;
  steps: MigrationStep[];
};

export function isMigrationStatus(value: unknown): value is MigrationStatus {
  if (!value || typeof value !== "object") return false;
  const v = value as Record<string, unknown>;
  return typeof v["state"] === "string" && Array.isArray(v["steps"]);
}

export type MigrationPollDecision =
  | { action: "migrating"; status: MigrationStatus; reloadMs?: number }
  | { action: "connecting"; reloadMs: number }
  | { action: "fallback"; stopPolling: true };

export const MAX_RELOAD_ATTEMPTS = 3;

const RELOAD_KEY = "infinidysk.migration-reload-attempts";
const MIGRATION_OBSERVED_KEY = "infinidysk.migration-observed";
const RESET_AFTER_MS = 2 * 60 * 1000;

type ReloadAttemptState = {
  count: number;
  lastAt: number;
};

function isReloadAttemptState(value: unknown): value is ReloadAttemptState {
  if (!value || typeof value !== "object") return false;
  const v = value as Record<string, unknown>;
  return typeof v["count"] === "number" && typeof v["lastAt"] === "number";
}

export function readReloadAttempts(storage: Storage, now: number): number {
  const raw = storage.getItem(RELOAD_KEY);
  if (!raw) return 0;

  try {
    const parsed: unknown = JSON.parse(raw);
    if (!isReloadAttemptState(parsed)) return 0;
    if (now - parsed.lastAt > RESET_AFTER_MS) return 0;
    return parsed.count;
  } catch {
    return 0;
  }
}

export function writeReloadAttempts(storage: Storage, count: number, now: number): void {
  const state: ReloadAttemptState = { count, lastAt: now };
  storage.setItem(RELOAD_KEY, JSON.stringify(state));
}

export function clearReloadAttempts(storage: Storage): void {
  storage.removeItem(RELOAD_KEY);
}

export function readMigrationObserved(storage: Storage): boolean {
  return storage.getItem(MIGRATION_OBSERVED_KEY) === "1";
}

export function writeMigrationObserved(storage: Storage): void {
  storage.setItem(MIGRATION_OBSERVED_KEY, "1");
}

export function clearMigrationObserved(storage: Storage): void {
  storage.removeItem(MIGRATION_OBSERVED_KEY);
}

/** Pure decision helper for MigrationBoundary polling (testable without React). */
export function decideMigrationStatusPoll(
  httpStatus: number,
  body: unknown,
  reloadAttempts = 0,
  seenMigration = false,
): MigrationPollDecision {
  if (httpStatus >= 200 && httpStatus < 300) {
    if (isMigrationStatus(body)) {
      return body.state === "completed"
        ? { action: "migrating", status: body, reloadMs: 1500 }
        : { action: "migrating", status: body };
    }
    return { action: "fallback", stopPolling: true };
  }

  if (httpStatus === 404) {
    if (seenMigration) {
      return { action: "connecting", reloadMs: 1500 };
    }
    return reloadAttempts >= MAX_RELOAD_ATTEMPTS
      ? { action: "fallback", stopPolling: true }
      : { action: "connecting", reloadMs: 1500 };
  }

  if (httpStatus === 502 || httpStatus === 503) {
    return { action: "connecting", reloadMs: 5000 };
  }

  return { action: "fallback", stopPolling: true };
}

function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) ms = 0;
  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const pad = (n: number) => String(n).padStart(2, "0");
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${minutes}:${pad(seconds)}`;
}

type Phase = "checking" | "connecting" | "migrating" | "fallback";

type FallbackProps = {
  title: string;
  detail: string;
  showReload: boolean;
};

/**
 * Client-side wrapper rendered by the root ErrorBoundary. It polls
 * `/api/migration-status`; while the backend is applying database migrations
 * (the blocking startup phase) it renders a live progress page. Otherwise it
 * falls back to the generic error card the ErrorBoundary computed.
 */
export function MigrationBoundary({ fallback }: { fallback: FallbackProps }) {
  const [phase, setPhase] = useState<Phase>("checking");
  const [status, setStatus] = useState<MigrationStatus | null>(null);
  const seenMigration = useRef(false);
  const reloadScheduled = useRef(false);

  const scheduleReload = useCallback((delayMs: number) => {
    if (reloadScheduled.current) return;
    reloadScheduled.current = true;
    window.setTimeout(() => window.location.reload(), delayMs);
  }, []);

  useEffect(() => {
    let cancelled = false;
    let interval: number | undefined;

    const stopPolling = () => {
      if (interval !== undefined) {
        window.clearInterval(interval);
        interval = undefined;
      }
    };

    const scheduleReloadAndStop = (delayMs: number) => {
      scheduleReload(delayMs);
      stopPolling();
    };

    const poll = async () => {
      try {
        const res = await fetch(withUrlBase("/api/migration-status"), {
          headers: { accept: "application/json" },
          cache: "no-store",
        });
        if (cancelled) return;

        const body: unknown = res.ok ? await res.json().catch(() => null) : null;
        if (cancelled) return;

        let reloadAttempts = 0;
        let migrationObserved = false;
        try {
          reloadAttempts = readReloadAttempts(window.sessionStorage, Date.now());
          migrationObserved = readMigrationObserved(window.sessionStorage);
        } catch {
          reloadAttempts = 0;
          migrationObserved = false;
        }

        const hasSeenMigration = seenMigration.current || migrationObserved;
        const decision = decideMigrationStatusPoll(
          res.status,
          body,
          reloadAttempts,
          hasSeenMigration,
        );
        if (decision.action === "migrating") {
          try {
            clearReloadAttempts(window.sessionStorage);
            writeMigrationObserved(window.sessionStorage);
          } catch {
            // Ignore storage failures in private browsing modes.
          }
          seenMigration.current = true;
          setStatus(decision.status);
          setPhase("migrating");
          if (decision.reloadMs !== undefined) scheduleReloadAndStop(decision.reloadMs);
          return;
        }

        if (decision.action === "connecting") {
          setPhase("connecting");
          if (res.status === 404 && !hasSeenMigration) {
            try {
              writeReloadAttempts(window.sessionStorage, reloadAttempts + 1, Date.now());
            } catch {
              // Ignore storage failures in private browsing modes.
            }
          }
          scheduleReloadAndStop(decision.reloadMs);
          return;
        }

        try {
          clearReloadAttempts(window.sessionStorage);
          clearMigrationObserved(window.sessionStorage);
        } catch {
          // Ignore storage failures in private browsing modes.
        }
        setPhase("fallback");
        stopPolling();
      } catch {
        if (cancelled) return;
        // Network failure: nothing is listening on the backend port yet.
        setPhase("connecting");
        scheduleReloadAndStop(5000);
      }
    };

    // fire-and-forget: polling errors are handled inside poll()
    void poll();
    interval = window.setInterval(() => void poll(), 2000);
    return () => {
      cancelled = true;
      stopPolling();
    };
  }, [scheduleReload]);

  if (phase === "migrating" && status) {
    return <MigrationProgressView status={status} />;
  }

  if (phase === "checking" || phase === "connecting") {
    return (
      <MigrationShell
        title={seenMigration.current ? "Finishing up" : "Connecting to InfiniDysk"}
        subtitle={
          seenMigration.current
            ? "Database maintenance finished. Waiting for the server to start..."
            : "Waiting for the backend to respond..."
        }
      >
        <div className="flex items-center gap-3 text-sm text-base-content/70">
          <span className="loading loading-spinner loading-sm text-primary" />
          <span>This can take a moment during startup.</span>
        </div>
      </MigrationShell>
    );
  }

  // Generic error fallback (mirrors the previous ErrorBoundary card).
  return (
    <MigrationShell title={fallback.title} subtitle={fallback.detail}>
      {fallback.showReload ? (
        <button
          type="button"
          className="btn btn-primary btn-sm"
          onClick={() => window.location.reload()}
        >
          Reload
        </button>
      ) : null}
    </MigrationShell>
  );
}

export function MigrationProgressView({ status }: { status: MigrationStatus }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const interval = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(interval);
  }, []);

  const total = status.total || status.steps.length;
  const completed = status.completed;
  const percent = total > 0 ? Math.min(100, Math.round((completed / total) * 100)) : 0;
  const overallElapsed = formatDuration(now - status.startedAt);
  const runningStep = status.steps.find((s) => s.status === "running") ?? null;
  const currentElapsed = runningStep?.startedAt
    ? formatDuration(now - runningStep.startedAt)
    : null;

  const failed = status.state === "failed";
  const done = status.state === "completed";

  let title = "Database maintenance in progress";
  let subtitle =
    "InfiniDysk is upgrading your database. This is a one-time step after an update and can take a while on large libraries. The app will load automatically when it finishes.";
  if (done) {
    title = "Maintenance complete";
    subtitle = "Starting InfiniDysk...";
  } else if (failed) {
    title = "Database maintenance failed";
    subtitle = "The upgrade could not be completed. Check the container logs for details.";
  }

  return (
    <MigrationShell title={title} subtitle={subtitle} wide>
      {failed && status.error ? (
        <div role="alert" className="alert alert-error text-xs">
          {status.error}
        </div>
      ) : null}

      <div className="space-y-2">
        <div className="flex items-center justify-between text-xs text-base-content/60">
          <span>
            Step {Math.min(completed + (done || failed ? 0 : 1), total)} of {total}
          </span>
          <span className="font-mono">Elapsed {overallElapsed}</span>
        </div>
        <progress
          className={`progress h-2 w-full ${failed ? "progress-error" : done ? "progress-success" : "progress-primary"}`}
          value={done ? 100 : percent}
          max={100}
        />
        {runningStep && !done && !failed ? (
          <div className="flex items-center gap-2 text-sm text-base-content/80">
            <span className="loading loading-spinner loading-sm text-primary" />
            <span>
              {runningStep.name}
              {currentElapsed ? (
                <span className="ml-1 font-mono text-base-content/60">({currentElapsed})</span>
              ) : null}
            </span>
          </div>
        ) : null}
        {runningStep?.slow && !done && !failed ? (
          <div role="alert" className="alert alert-warning text-xs">
            This step rewrites large tables and may take a long time on big databases. This is
            expected.
          </div>
        ) : null}
      </div>

      <ul className="steps steps-vertical w-full">
        {status.steps.map((step) => (
          <li
            key={step.id}
            className={`step ${
              step.status === "completed"
                ? "step-success"
                : step.status === "running"
                  ? "step-primary"
                  : step.status === "failed"
                    ? "step-error"
                    : ""
            }`}
            aria-current={step.status === "running" ? "step" : undefined}
          >
            <span className="text-left text-sm">
              {step.name}
              {step.slow && step.status === "pending" ? (
                <span className="ml-2 badge badge-warning badge-xs">may be slow</span>
              ) : null}
            </span>
          </li>
        ))}
      </ul>

      {failed ? (
        <button
          type="button"
          className="btn btn-primary btn-sm"
          onClick={() => window.location.reload()}
        >
          Reload
        </button>
      ) : null}
    </MigrationShell>
  );
}

export function MigrationShell({
  title,
  subtitle,
  children,
  wide,
}: {
  title: string;
  subtitle?: string;
  children?: React.ReactNode;
  wide?: boolean;
}) {
  return (
    <main className="hero min-h-dvh bg-base-300">
      <div className="hero-content w-full px-4 py-8">
        <div
          className={`card w-full ${wide ? "max-w-xl" : "max-w-lg"} border border-base-content/10 bg-base-100 shadow-xl`}
        >
          <div className="card-body gap-4">
            <div className="flex items-center gap-3">
              <img
                className="h-12 w-12 rounded-2xl bg-gradient-to-br from-primary via-info to-success p-0.5 shadow-md shadow-primary/20"
                src={withUrlBase("/logo.png")}
                alt="InfiniDysk"
              />
              <div className="space-y-1">
                <h1 className="text-xl font-bold tracking-tight">{title}</h1>
                {subtitle ? (
                  <p className="text-sm leading-relaxed text-base-content/70">{subtitle}</p>
                ) : null}
              </div>
            </div>
            {children}
          </div>
        </div>
      </div>
    </main>
  );
}
