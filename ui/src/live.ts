import type { InvalidateQueryFilters, QueryClient } from "@tanstack/react-query";

/** Least time between the end of one refresh of a list and the start of the next. */
export const REFRESH_GAP_MS = 2000;

export type Timer = {
  now: () => number;
  setTimeout: (run: () => void, ms: number) => number;
  clearTimeout: (id: number | undefined) => void;
};

const browserTimer: Timer = {
  now: () => performance.now(),
  setTimeout: (run, ms) => window.setTimeout(run, ms),
  clearTimeout: (id) => window.clearTimeout(id),
};

type Target = "queue" | "history" | "health" | "summary" | "other";

const lists = new Set(["queue", "history", "health"]);

const targets: Record<Target, InvalidateQueryFilters> = {
  queue: { queryKey: ["queue"] },
  history: { queryKey: ["history"] },
  // The summary is its own target: it is the slow query, and both history and health changes move it.
  health: { queryKey: ["health"], predicate: (query) => query.queryKey[1] !== "summary" },
  summary: { queryKey: ["health", "summary"], exact: true },
  // Everything else, refreshed only on resync.
  other: { predicate: (query) => !lists.has(String(query.queryKey[0])) },
};

const marks: Record<string, Target[]> = {
  queue: ["queue"],
  // Deleting a file reports a history change only.
  history: ["history", "summary"],
  health: ["health", "summary"],
  resync: ["queue", "history", "health", "summary", "other"],
  // Only list changes are sent as events, so only lists can have been missed.
  reconnect: ["queue", "history", "health", "summary"],
};

/** Refreshes one target at most once per gap, and once more for changes that arrive while it runs. */
function limiter(client: QueryClient, filters: InvalidateQueryFilters, timer: Timer, gap: number) {
  let pending = false;
  let running = false;
  let disposed = false;
  let scheduled: number | undefined;
  let lastEnd = -Infinity;
  const schedule = () => {
    if (disposed || running || !pending || scheduled !== undefined) return;
    scheduled = timer.setTimeout(() => void run(), Math.max(0, lastEnd + gap - timer.now()));
  };
  const run = async () => {
    scheduled = undefined;
    pending = false;
    running = true;
    // A fetch already in flight may have started before the change, and the invalidation joins it.
    const joined = client
      .getQueryCache()
      .findAll(filters)
      .filter((query) => query.state.fetchStatus === "fetching");
    try {
      // This swallows fetch errors; the next change refreshes again.
      await client.invalidateQueries(filters, { cancelRefetch: false });
    } finally {
      running = false;
      lastEnd = timer.now();
      if (joined.some((query) => query.state.status !== "error")) pending = true;
      schedule();
    }
  };
  return {
    mark() {
      pending = true;
      schedule();
    },
    dispose() {
      disposed = true;
      timer.clearTimeout(scheduled);
      scheduled = undefined;
    },
  };
}

/** Turns server change events into bounded query refreshes for one tab. */
export function liveRefresh(client: QueryClient, timer: Timer = browserTimer, gap = REFRESH_GAP_MS) {
  const limiters = Object.fromEntries(
    Object.entries(targets).map(([name, filters]) => [name, limiter(client, filters, timer, gap)]),
  ) as Record<Target, ReturnType<typeof limiter>>;
  const mark = (event: string) => marks[event]?.forEach((target) => limiters[target].mark());
  let opened = false;
  return {
    /** Handles a `queue`, `history`, `health` or `resync` event. */
    change: mark,
    /** Handles the stream opening; changes made while it was down were never sent. */
    opened() {
      if (opened) mark("reconnect");
      opened = true;
    },
    dispose() {
      Object.values(limiters).forEach((l) => l.dispose());
    },
  };
}
