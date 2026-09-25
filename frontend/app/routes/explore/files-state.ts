import type { FileResourceRow, FilesResourcePage } from "~/clients/files-contract";

export const FILES_PAGE_SIZE = 100;
export const FILES_MAX_REQUESTS = 4;
export const FILES_REFRESH_MS = 60_000;
export const FILES_SEARCH_DEBOUNCE_MS = 250;
export type BranchPage = {
  parentPath: string;
  offset: number;
  requestId: number;
  keys: string[];
  totalRows: number;
  hasMore: boolean;
  status: "idle" | "loading" | "ready" | "error";
  error: string | null;
  loadedAt: number;
};
export type FilesState = {
  generation: number;
  queryKey: string;
  rows: Record<string, FileResourceRow>;
  rowObservedAt: Record<string, number>;
  branches: Record<string, BranchPage>;
  expanded: Set<string>;
  selected: Set<string>;
  focusedKey: string | null;
  matchingFileCount: number;
  observedAt: number;
};
export function initialFilesState(queryKey: string, page: FilesResourcePage): FilesState {
  const branchKey = page.mode === "tree" ? "root" : "list";
  return {
    generation: 0,
    queryKey,
    rows: Object.fromEntries(page.rows.map((row) => [row.key, row])),
    rowObservedAt: Object.fromEntries(
      page.rows.map((row) => [row.key, Date.parse(page.observedAt)]),
    ),
    branches: {
      [branchKey]: {
        parentPath: page.parentPath,
        offset: page.offset,
        requestId: 0,
        keys: page.rows.map((row) => row.key),
        totalRows: page.totalRows,
        hasMore: page.hasMore,
        status: "ready",
        error: null,
        loadedAt: Date.now(),
      },
    },
    expanded: new Set(),
    selected: new Set(),
    focusedKey: page.rows[0]?.key ?? null,
    matchingFileCount: page.matchingFileCount,
    observedAt: Date.parse(page.observedAt),
  };
}
export function applyFilesPage(
  state: FilesState,
  branchKey: string,
  generation: number,
  requestId: number,
  requestedOffset: number,
  page: FilesResourcePage,
  loadedAt: number,
): FilesState {
  const branch = state.branches[branchKey];
  if (
    state.generation !== generation ||
    !branch ||
    branch.requestId !== requestId ||
    branch.offset !== requestedOffset
  )
    return state;
  if (branch.parentPath !== page.parentPath || page.offset !== requestedOffset)
    return {
      ...state,
      branches: {
        ...state.branches,
        [branchKey]: { ...branch, status: "error", error: "The server returned a different page." },
      },
    };
  const observedAt = Date.parse(page.observedAt);
  const rows = { ...state.rows };
  const rowObservedAt = { ...state.rowObservedAt };
  for (const row of page.rows) {
    if (observedAt < (rowObservedAt[row.key] ?? -Infinity)) continue;
    rows[row.key] = row;
    rowObservedAt[row.key] = observedAt;
  }
  return {
    ...state,
    rows,
    rowObservedAt,
    matchingFileCount:
      observedAt >= state.observedAt ? page.matchingFileCount : state.matchingFileCount,
    observedAt: Math.max(state.observedAt, observedAt),
    branches: {
      ...state.branches,
      [branchKey]: {
        ...branch,
        keys: page.rows.map((row) => row.key),
        totalRows: page.totalRows,
        hasMore: page.hasMore,
        status: "ready",
        error: null,
        loadedAt,
      },
    },
  };
}
export type FilesAction =
  | { type: "reset"; generation: number; queryKey: string; scopeChanged: boolean }
  | { type: "request"; job: FilesReadJob }
  | { type: "success"; job: FilesReadJob; page: FilesResourcePage; loadedAt: number }
  | { type: "failure"; job: FilesReadJob; error: string }
  | { type: "toggle"; key: string }
  | { type: "select"; key: string }
  | { type: "focus"; key: string | null }
  | { type: "removed"; targets: FileResourceRow[] }
  | { type: "invalidate"; keys?: string[] }
  | { type: "progress"; id: string; progress: number };
function prune(state: FilesState): FilesState {
  const referenced = new Set([
    ...Object.values(state.branches).flatMap((branch) => branch.keys),
    ...state.selected,
  ]);
  const rows = Object.fromEntries(
    Object.entries(state.rows).filter(([key]) => referenced.has(key)),
  );
  return {
    ...state,
    rows,
    rowObservedAt: Object.fromEntries(
      Object.entries(state.rowObservedAt).filter(([key]) => referenced.has(key)),
    ),
    focusedKey:
      state.focusedKey && rows[state.focusedKey]
        ? state.focusedKey
        : (state.branches["root"]?.keys[0] ?? state.branches["list"]?.keys[0] ?? null),
  };
}
export function filesReducer(state: FilesState, action: FilesAction): FilesState {
  switch (action.type) {
    case "reset":
      return {
        ...state,
        generation: action.generation,
        queryKey: action.queryKey,
        rows: {},
        rowObservedAt: {},
        branches: {},
        selected: new Set(),
        expanded: action.scopeChanged ? new Set() : new Set(state.expanded),
        focusedKey: null,
        observedAt: -Infinity,
        matchingFileCount: 0,
      };
    case "request": {
      if (state.generation !== action.job.generation) return state;
      const { branchKey, parentPath, offset, requestId } = action.job;
      const previous = state.branches[branchKey];
      return {
        ...state,
        branches: {
          ...state.branches,
          [branchKey]: {
            parentPath,
            offset,
            requestId,
            keys: previous?.keys ?? [],
            totalRows: previous?.totalRows ?? 0,
            hasMore: previous?.hasMore ?? false,
            status: "loading",
            error: null,
            loadedAt: previous?.loadedAt ?? 0,
          },
        },
      };
    }
    case "success": {
      const { job, page, loadedAt } = action;
      let next = applyFilesPage(
        state,
        job.branchKey,
        job.generation,
        job.requestId,
        job.offset,
        page,
        loadedAt,
      );
      if (next === state) return state;
      if (next.branches[job.branchKey]?.status === "error") return next;
      const branches = { ...next.branches };
      const expanded = new Set(next.expanded);
      let focusedKey = next.focusedKey;
      const removedDirectories = (state.branches[job.branchKey]?.keys ?? []).filter(
        (key) => state.rows[key]?.isDirectory && !page.rows.some((row) => row.key === key),
      );
      for (const key of removedDirectories) {
        const path = state.rows[key]!.path;
        const focusedPath = focusedKey ? state.rows[focusedKey]?.path : null;
        if (focusedPath === path || focusedPath?.startsWith(path + "/")) focusedKey = null;
        for (const [childKey, branch] of Object.entries(branches)) {
          if (
            childKey !== "root" &&
            childKey !== "list" &&
            (branch.parentPath === path || branch.parentPath.startsWith(path + "/"))
          ) {
            delete branches[childKey];
            expanded.delete(childKey);
          }
        }
        expanded.delete(key);
      }
      for (const row of page.rows) {
        if (row.isDirectory && branches[row.key]?.parentPath !== row.path) delete branches[row.key];
      }
      next = { ...next, branches, expanded, focusedKey };
      return prune(next);
    }
    case "failure": {
      const branch = state.branches[action.job.branchKey];
      if (
        state.generation !== action.job.generation ||
        !branch ||
        branch.requestId !== action.job.requestId ||
        branch.offset !== action.job.offset
      )
        return state;
      return {
        ...state,
        branches: {
          ...state.branches,
          [action.job.branchKey]: { ...branch, status: "error", error: action.error },
        },
      };
    }
    case "toggle": {
      const expanded = new Set(state.expanded);
      if (expanded.has(action.key)) expanded.delete(action.key);
      else expanded.add(action.key);
      return { ...state, expanded };
    }
    case "select": {
      const selected = new Set(state.selected);
      if (selected.has(action.key)) selected.delete(action.key);
      else if (state.rows[action.key]?.canDelete) selected.add(action.key);
      return { ...state, selected };
    }
    case "focus":
      return { ...state, focusedKey: action.key };
    case "removed": {
      const removed = (path: string) =>
        action.targets.some(
          (target) =>
            path === target.path || (target.isDirectory && path.startsWith(target.path + "/")),
        );
      const keys = new Set(
        Object.values(state.rows)
          .filter((row) => removed(row.path))
          .map((row) => row.key),
      );
      return prune({
        ...state,
        branches: Object.fromEntries(
          Object.entries(state.branches)
            .filter(
              ([key, branch]) => key === "root" || key === "list" || !removed(branch.parentPath),
            )
            .map(([key, branch]) => [
              key,
              { ...branch, keys: branch.keys.filter((rowKey) => !keys.has(rowKey)), loadedAt: 0 },
            ]),
        ),
        expanded: new Set([...state.expanded].filter((key) => !keys.has(key))),
        selected: new Set([...state.selected].filter((key) => !keys.has(key))),
      });
    }
    case "invalidate":
      return {
        ...state,
        branches: Object.fromEntries(
          Object.entries(state.branches).map(([key, branch]) => [
            key,
            !action.keys || action.keys.includes(key) ? { ...branch, loadedAt: 0 } : branch,
          ]),
        ),
      };
    case "progress": {
      const row = state.rows[action.id];
      return row
        ? {
            ...state,
            rows: {
              ...state.rows,
              [action.id]: { ...row, progress: action.progress, scanState: "checking" },
            },
          }
        : state;
    }
  }
}
export function flattenFilesTree(state: FilesState): Array<{ key: string; depth: number }> {
  const visible: Array<{ key: string; depth: number }> = [];
  const seen = new Set<string>();
  const stack = [...(state.branches["root"]?.keys ?? [])]
    .reverse()
    .map((key) => ({ key, depth: 1 }));
  while (stack.length > 0) {
    const current = stack.pop()!;
    if (seen.has(current.key)) continue;
    const row = state.rows[current.key];
    if (!row) continue;
    seen.add(current.key);
    visible.push(current);
    if (!row.isDirectory || !state.expanded.has(current.key)) continue;
    const children = state.branches[current.key]?.keys ?? [];
    for (let index = children.length - 1; index >= 0; index--) {
      const key = children[index];
      if (key !== undefined) stack.push({ key, depth: current.depth + 1 });
    }
  }
  return visible;
}
export function topLevelRemovalTargets(rows: FileResourceRow[]): FileResourceRow[] {
  const unique = [
    ...new Map(rows.filter((row) => row.id !== null).map((row) => [row.key, row])).values(),
  ];
  unique.sort(
    (left, right) => left.path.length - right.path.length || left.path.localeCompare(right.path),
  );
  const targets: FileResourceRow[] = [];
  for (const row of unique) {
    if (targets.some((parent) => parent.isDirectory && row.path.startsWith(parent.path + "/")))
      continue;
    targets.push(row);
  }
  return targets;
}
export type FilesReadJob = {
  branchKey: string;
  parentPath: string;
  offset: number;
  parameters: URLSearchParams;
  generation: number;
  requestId: number;
};
type FilesReadCallbacks = {
  started: (job: FilesReadJob) => void;
  completed: (job: FilesReadJob, page: FilesResourcePage) => void;
  failed: (job: FilesReadJob, message: string) => void;
};
export class FilesReadCoordinator {
  private generation = 0;
  private nextRequestId = 0;
  private disposed = false;
  private pending: FilesReadJob[] = [];
  private running = new Map<number, { job: FilesReadJob; controller: AbortController }>();
  private readonly readPage: (
    parameters: URLSearchParams,
    signal: AbortSignal,
  ) => Promise<FilesResourcePage>;
  private readonly callbacks: FilesReadCallbacks;
  constructor(
    readPage: (parameters: URLSearchParams, signal: AbortSignal) => Promise<FilesResourcePage>,
    callbacks: FilesReadCallbacks,
  ) {
    this.readPage = readPage;
    this.callbacks = callbacks;
  }
  beginGeneration(): number {
    if (this.disposed) throw new Error("Files read coordinator is disposed.");
    this.generation++;
    this.pending = [];
    for (const current of this.running.values()) current.controller.abort();
    return this.generation;
  }
  enqueue(
    branchKey: string,
    parentPath: string,
    offset: number,
    parameters: URLSearchParams,
  ): number {
    if (this.disposed) throw new Error("Files read coordinator is disposed.");
    this.pending = this.pending.filter((job) => job.branchKey !== branchKey);
    for (const current of this.running.values()) {
      if (current.job.branchKey === branchKey) current.controller.abort();
    }
    const job = {
      branchKey,
      parentPath,
      offset,
      parameters: new URLSearchParams(parameters),
      generation: this.generation,
      requestId: ++this.nextRequestId,
    };
    this.callbacks.started(job);
    this.pending.push(job);
    this.drain();
    return job.requestId;
  }
  dispose(): void {
    this.disposed = true;
    this.pending = [];
    for (const current of this.running.values()) current.controller.abort();
  }
  private drain(): void {
    while (!this.disposed && this.running.size < FILES_MAX_REQUESTS && this.pending.length > 0) {
      const job = this.pending.shift()!;
      if (job.generation !== this.generation) continue;
      const controller = new AbortController();
      this.running.set(job.requestId, { job, controller });
      void this.run(job, controller);
    }
  }
  private async run(job: FilesReadJob, controller: AbortController): Promise<void> {
    try {
      const page = await this.readPage(job.parameters, controller.signal);
      if (!this.disposed && !controller.signal.aborted && job.generation === this.generation)
        this.callbacks.completed(job, page);
    } catch (error) {
      if (!this.disposed && !controller.signal.aborted && job.generation === this.generation)
        this.callbacks.failed(
          job,
          error instanceof Error ? error.message : "Could not load files.",
        );
    } finally {
      this.running.delete(job.requestId);
      this.drain();
    }
  }
}
