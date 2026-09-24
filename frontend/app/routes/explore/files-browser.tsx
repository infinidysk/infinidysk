import {
  Fragment,
  useEffect,
  useEffectEvent,
  useReducer,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
} from "react";
import { Link, useSearchParams } from "react-router";
import { z } from "zod";
import { Button, Checkbox, Icon } from "~/components/ui";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { useIsReadOnly } from "~/auth/authorization";
import { withUrlBase } from "~/utils/url-base";
import { formatFileSize } from "~/utils/file-size";
import { useWebsocketTopics } from "~/utils/shared-websocket";
import {
  parseHealthItemProgressMessage,
  parseHealthItemStatusMessage,
} from "~/routes/health/health-queue-state";
import {
  defaultFilesFilters,
  filesFiltersSchema,
  filesResourcePageSchema,
  deletePreviewResponseSchema,
  recheckFileResponseSchema,
  searchFileInArrResponseSchema,
  parseFilesFilters,
  getFilesQueryKey,
  serializeFilesFilters,
  type FilesFilters,
  type FileResourceRow,
  type FilesResourcePage,
  type DeletePreviewResponse,
} from "~/clients/files-contract";
import {
  FILES_PAGE_SIZE,
  FILES_REFRESH_MS,
  FILES_SEARCH_DEBOUNCE_MS,
  FilesReadCoordinator,
  filesReducer,
  initialFilesState,
  flattenFilesTree,
  topLevelRemovalTargets,
  type BranchPage,
} from "./files-state";
import { isPlayableMedia } from "./file-kind/file-kind";
import { MediaPreview } from "./media-preview/media-preview";
import { appendQueryParam } from "./media-preview/media-utils";
import styles from "./files-browser.module.css";

type Props = {
  scopePath: string;
  initialPage: FilesResourcePage;
  initialFilters: FilesFilters;
  initialMode: string;
  initialSort: string;
  initialDirection: string;
};
const labels: Record<string, string> = {
  healthy: "Healthy",
  degraded: "Degraded",
  "needs-attention": "Needs attention",
  unknown: "Unknown",
  "not-configured": "Not configured",
  "in-library": "In library",
  "not-in-library": "Not in library",
  "not-applicable": "Not applicable",
  disabled: "Disabled",
  checking: "Checking",
  "repair-pending": "Repair pending",
  queued: "Queued",
  due: "Due",
  scheduled: "Scheduled",
};
async function readJson(response: Response): Promise<unknown> {
  if (response.redirected || !response.headers.get("content-type")?.includes("json"))
    throw new Error("Session expired. Sign in again.");
  const json: unknown = await response.json();
  if (
    !response.ok ||
    (typeof json === "object" && json !== null && "ok" in json && json.ok === false)
  ) {
    const error = z.object({ error: z.string() }).safeParse(json);
    throw new Error(
      error.success ? error.data.error : "Files request failed. Refresh and try again.",
    );
  }
  return json;
}
async function readPage(
  parameters: URLSearchParams,
  signal: AbortSignal,
): Promise<FilesResourcePage> {
  const json = await readJson(
    await fetch(withUrlBase(`/resources/files?${parameters}`), { signal }),
  );
  const page = filesResourcePageSchema.safeParse(json);
  if (!page.success) throw new Error("The server returned an invalid Files page.");
  return page.data;
}
const scopeHref = (path: string) =>
  withUrlBase(`/explore/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`);
const age = (value: string | null, now: number) =>
  value === null
    ? "Unknown"
    : `${Math.max(0, Math.floor((now - Date.parse(value)) / 86_400_000))}d`;
const date = (value: string | null) => (value ? new Date(value).toLocaleString() : "Unknown");

export function FilesBrowser(props: Props) {
  const readonly = useIsReadOnly();
  const [parameters, setParameters] = useSearchParams();
  let filters: FilesFilters;
  let filterError: string | null = null;
  try {
    filters = parseFilesFilters(parameters);
  } catch {
    filters = props.initialFilters;
    filterError = "Invalid filters. Clear filters to continue.";
  }
  const mode = parameters.get("mode") === "list" ? "list" : "tree";
  const sort = parameters.get("sort") ?? "name";
  const direction = parameters.get("direction") ?? "asc";
  const queryKey = getFilesQueryKey(props.scopePath, filters, sort, direction);
  const [state, dispatch] = useReducer(filesReducer, undefined, () =>
    initialFilesState(queryKey, props.initialPage),
  );
  const [summary, setSummary] = useState(props.initialPage);
  const [player, setPlayer] = useState<FileResourceRow | null>(null);
  const [searchTarget, setSearchTarget] = useState<FileResourceRow | null>(null);
  const [removals, setRemovals] = useState<FileResourceRow[] | null>(null);
  const [previews, setPreviews] = useState<Record<string, DeletePreviewResponse>>({});
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [previewAttempt, setPreviewAttempt] = useState(0);
  const [pending, setPending] = useState<Set<string>>(new Set());
  const pendingRef = useRef(new Set<string>());
  const [messages, setMessages] = useState<Record<string, string>>({});
  const coordinator = useRef<FilesReadCoordinator | null>(null);
  const scopeRef = useRef(props.scopePath);
  const rowsRef = useRef(new Map<string, HTMLButtonElement>());
  const refreshTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  function enqueue(key: string, parentPath: string, offset = 0) {
    const query = serializeFilesFilters(filters);
    query.set("scopePath", props.scopePath);
    query.set("parentPath", parentPath);
    query.set("mode", key === "list" ? "list" : "tree");
    query.set("sort", sort);
    query.set("direction", direction);
    query.set("offset", String(offset));
    query.set("limit", String(FILES_PAGE_SIZE));
    coordinator.current?.enqueue(key, parentPath, offset, query);
  }
  useEffect(() => {
    const instance = new FilesReadCoordinator(readPage, {
      started: (job) => dispatch({ type: "request", job }),
      completed: (job, page) => {
        dispatch({ type: "success", job, page, loadedAt: Date.now() });
        setSummary((previous) =>
          Date.parse(page.observedAt) >= Date.parse(previous.observedAt) ? page : previous,
        );
      },
      failed: (job, error) => dispatch({ type: "failure", job, error }),
    });
    coordinator.current = instance;
    return () => {
      instance.dispose();
      if (coordinator.current === instance) coordinator.current = null;
    };
  }, []);
  useEffect(() => {
    const generation = coordinator.current!.beginGeneration();
    dispatch({
      type: "reset",
      generation,
      queryKey,
      scopeChanged: scopeRef.current !== props.scopePath,
    });
    scopeRef.current = props.scopePath;
  }, [queryKey, props.scopePath]);
  const visible =
    mode === "tree"
      ? flattenFilesTree(state)
      : (state.branches["list"]?.keys ?? []).map((key) => ({ key, depth: 0 }));
  const ensurePages = useEffectEvent(() => {
    if (state.queryKey !== queryKey || filterError || document.visibilityState === "hidden") return;
    const key = mode === "tree" ? "root" : "list";
    const current = state.branches[key];
    if (
      !current ||
      (current.status === "ready" && Date.now() - current.loadedAt >= FILES_REFRESH_MS)
    )
      enqueue(key, props.scopePath, current?.offset ?? 0);
    if (mode === "tree")
      for (const entry of visible) {
        const row = state.rows[entry.key]!;
        const branch = state.branches[row.key];
        if (
          row.isDirectory &&
          state.expanded.has(row.key) &&
          (!branch ||
            (branch.status === "ready" && Date.now() - branch.loadedAt >= FILES_REFRESH_MS))
        )
          enqueue(row.key, row.path, branch?.offset ?? 0);
      }
    for (const [branchKey, branch] of Object.entries(state.branches)) {
      if (branch.status === "ready" && branch.offset > 0 && branch.offset >= branch.totalRows)
        enqueue(
          branchKey,
          branch.parentPath,
          Math.max(0, Math.ceil(branch.totalRows / FILES_PAGE_SIZE) - 1) * FILES_PAGE_SIZE,
        );
    }
  });
  useEffect(() => {
    ensurePages();
  }, [state, mode, queryKey, filterError]);
  function refresh(staleOnly = false) {
    const rootKey = mode === "tree" ? "root" : "list";
    const keys = [
      rootKey,
      ...(mode === "tree"
        ? visible
            .filter((entry) => state.rows[entry.key]?.isDirectory && state.expanded.has(entry.key))
            .map((entry) => entry.key)
        : []),
    ];
    for (const key of keys) {
      const branch = state.branches[key];
      if (
        branch?.status !== "loading" &&
        (!staleOnly || !branch || Date.now() - branch.loadedAt >= FILES_REFRESH_MS)
      )
        enqueue(
          key,
          branch?.parentPath ?? state.rows[key]?.path ?? props.scopePath,
          branch?.offset ?? 0,
        );
    }
  }
  const periodicRefresh = useEffectEvent(() => {
    if (document.visibilityState === "visible") refresh(true);
  });
  useEffect(() => {
    const interval = setInterval(periodicRefresh, FILES_REFRESH_MS);
    document.addEventListener("visibilitychange", periodicRefresh);
    return () => {
      clearInterval(interval);
      document.removeEventListener("visibilitychange", periodicRefresh);
      if (refreshTimer.current) clearTimeout(refreshTimer.current);
    };
  }, []);
  const coalescedRefresh = useRef(() => {});
  coalescedRefresh.current = () => {
    if (document.visibilityState === "visible") refresh();
  };
  useWebsocketTopics(
    { hs: "event", hp: "event", hsched: "event", ha: "event", hr: "event" },
    (topic, message) => {
      if (topic === "hp") {
        const progress = parseHealthItemProgressMessage(message);
        if (progress)
          dispatch({ type: "progress", id: progress.davItemId, progress: progress.progress });
        return;
      }
      if (topic === "hs" && !parseHealthItemStatusMessage(message)) return;
      if (!refreshTimer.current)
        refreshTimer.current = setTimeout(() => {
          refreshTimer.current = null;
          coalescedRefresh.current();
        }, 250);
    },
  );
  function update(next: FilesFilters, nextMode = mode, nextSort = sort, nextDirection = direction) {
    const query = serializeFilesFilters(next);
    if (nextMode !== "tree") query.set("mode", nextMode);
    if (nextSort !== "name") query.set("sort", nextSort);
    if (nextDirection !== "asc") query.set("direction", nextDirection);
    setParameters(query, { replace: true, preventScrollReset: true });
  }
  async function mutate(
    row: FileResourceRow,
    intent: "recheck" | "arr-search" | "remove",
  ): Promise<boolean> {
    if (readonly || !row.id || pendingRef.current.has(row.key)) return false;
    pendingRef.current.add(row.key);
    setPending(new Set(pendingRef.current));
    try {
      const body = new FormData();
      body.set("intent", intent);
      if (intent === "remove") {
        body.set("path", row.path);
        body.set("expectedDavItemId", row.id);
      } else body.set("davItemId", row.id);
      if (intent !== "recheck") body.set("confirmed", "true");
      const json = await readJson(
        await fetch(withUrlBase("/resources/files"), { method: "POST", body }),
      );
      const envelope = z.object({ ok: z.literal(true), result: z.unknown() }).parse(json);
      let message: string;
      if (intent === "arr-search") {
        const result = searchFileInArrResponseSchema.parse(envelope.result);
        message = `Arr: ${result.outcome}. ${result.results.map((target) => `${target.instanceName}: ${target.state}${target.error ? ` (${target.error})` : ""}`).join("; ")}`;
      } else if (intent === "recheck")
        message = `Recheck ${recheckFileResponseSchema.parse(envelope.result).state}.`;
      else {
        z.object({ status: z.literal(true) }).parse(envelope.result);
        message = "Removed.";
      }
      setMessages((previous) => ({ ...previous, [row.key]: message }));
      if (intent === "remove") {
        dispatch({ type: "removed", targets: [row] });
        setPlayer((current) =>
          current &&
          (current.path === row.path ||
            (row.isDirectory && current.path.startsWith(row.path + "/")))
            ? null
            : current,
        );
      }
      return true;
    } catch (error) {
      setMessages((previous) => ({
        ...previous,
        [row.key]: error instanceof Error ? error.message : "Action failed.",
      }));
      return false;
    } finally {
      pendingRef.current.delete(row.key);
      setPending(new Set(pendingRef.current));
      coalescedRefresh.current();
    }
  }
  useEffect(() => {
    setPreviews({});
    setPreviewError(null);
    if (!removals?.length) return;
    const controller = new AbortController();
    void (async () => {
      try {
        const ready: Record<string, DeletePreviewResponse> = {};
        for (const row of removals) {
          const query = new URLSearchParams({
            operation: "delete-preview",
            path: row.path,
            expectedDavItemId: row.id!,
          });
          ready[row.key] = deletePreviewResponseSchema.parse(
            await readJson(
              await fetch(withUrlBase(`/resources/files?${query}`), { signal: controller.signal }),
            ),
          );
        }
        if (!controller.signal.aborted) setPreviews(ready);
      } catch (error) {
        if (!controller.signal.aborted)
          setPreviewError(error instanceof Error ? error.message : "Could not preview removal.");
      }
    })();
    return () => controller.abort();
  }, [removals, previewAttempt]);
  async function removeConfirmed() {
    if (!removals || pending.size || removals.some((row) => !previews[row.key]) || previewError)
      return;
    const failed: FileResourceRow[] = [];
    for (const row of removals) if (!(await mutate(row, "remove"))) failed.push(row);
    setRemovals(failed.length ? failed : null);
  }
  function focus(key: string | null) {
    dispatch({ type: "focus", key });
    if (key) rowsRef.current.get(key)?.focus();
  }
  function keyDown(event: KeyboardEvent, row: FileResourceRow, index: number) {
    if (event.target !== event.currentTarget) return;
    const keys = visible.map((entry) => entry.key);
    switch (event.key) {
      case "ArrowDown":
        focus(keys[Math.min(keys.length - 1, index + 1)] ?? null);
        break;
      case "ArrowUp":
        focus(keys[Math.max(0, index - 1)] ?? null);
        break;
      case "Home":
        focus(keys[0] ?? null);
        break;
      case "End":
        focus(keys.at(-1) ?? null);
        break;
      case "ArrowRight":
        if (mode === "tree" && row.isDirectory) {
          if (!state.expanded.has(row.key)) dispatch({ type: "toggle", key: row.key });
          else focus(state.branches[row.key]?.keys[0] ?? row.key);
        }
        break;
      case "ArrowLeft":
        if (state.expanded.has(row.key)) dispatch({ type: "toggle", key: row.key });
        else if (row.parentId && state.rows[row.parentId]) focus(row.parentId);
        break;
      case " ":
        if (!readonly && row.canDelete) dispatch({ type: "select", key: row.key });
        break;
      default:
        return;
    }
    event.preventDefault();
  }
  const focused = state.focusedKey ? state.rows[state.focusedKey] : null;
  const rootKey = mode === "tree" ? "root" : "list";
  function branchControls(key: string, depth: number) {
    const branch = state.branches[key];
    if (!branch) return null;
    return (
      <BranchControls
        key={`branch:${key}`}
        branch={branch}
        depth={depth}
        retry={() => enqueue(key, branch.parentPath, branch.offset)}
        page={(offset) => enqueue(key, branch.parentPath, offset)}
      />
    );
  }
  return (
    <section className={styles.browser}>
      <header className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">Files</h1>
        <nav aria-label="File scope" className="flex min-w-0 flex-wrap gap-2 text-xs">
          {props.scopePath
            .split("/")
            .filter(Boolean)
            .map((part, index, parts) => (
              <Fragment key={index}>
                <span aria-hidden="true">/</span>
                <Link to={scopeHref("/" + parts.slice(0, index + 1).join("/"))}>{part}</Link>
              </Fragment>
            ))}
        </nav>
        <details className="ml-auto text-xs">
          <summary>WebDAV system views</summary>
          <nav className="flex flex-wrap gap-3 py-2">
            {["nzbs", "completed-symlinks", ".ids"].map((path) => (
              <Link key={path} to={withUrlBase(`/explore/${path}`)}>
                {path}
              </Link>
            ))}
          </nav>
        </details>
      </header>
      <FilesToolbar
        filters={filters}
        mode={mode}
        sort={sort}
        direction={direction}
        update={update}
        refresh={() => refresh()}
      />
      {focused?.isDirectory && (
        <Link className="text-xs underline" to={scopeHref(focused.path)}>
          Use {focused.name} as scope
        </Link>
      )}
      {filterError && (
        <p role="alert" className="text-error">
          {filterError}
          <Button onClick={() => update(defaultFilesFilters)}>Clear filters</Button>
        </p>
      )}
      <div className="flex flex-wrap items-center gap-3 py-2 text-xs" aria-live="polite">
        <span>{state.matchingFileCount} matching files</span>
        <span>{visible.length} visible</span>
        <span>{state.selected.size} selected</span>
        {!readonly && (
          <Button
            size="xsmall"
            disabled={!state.selected.size}
            onClick={() =>
              setRemovals(
                topLevelRemovalTargets(
                  [...state.selected]
                    .map((key) => state.rows[key])
                    .filter((row): row is FileResourceRow => Boolean(row)),
                ),
              )
            }
          >
            <Icon name="delete" />
            Remove selected
          </Button>
        )}
        <span>
          Library:{" "}
          {summary.libraryScanState === "ready"
            ? date(summary.libraryScannedAt)
            : labels[summary.libraryScanState]}
        </span>
        {summary.libraryError && <span className="text-warning">{summary.libraryError}</span>}
        {(!summary.schedule.checksOpen || !summary.schedule.repairsOpen) && (
          <span>Health work window closed ({summary.schedule.timeZoneId})</span>
        )}
      </div>
      <div className={styles.viewport}>
        <div
          className={styles.grid}
          role={mode === "tree" ? "treegrid" : "table"}
          aria-label="Files"
          aria-multiselectable={mode === "tree" ? true : undefined}
        >
          <div role="row" className={`${styles.row} ${styles.header}`}>
            {["Name", "Health", "Next scan", "Added age", "Size", "Library", "Actions"].map(
              (label) => (
                <div role="columnheader" key={label}>
                  {label}
                </div>
              ),
            )}
          </div>
          {visible.map((entry, index) => {
            const row = state.rows[entry.key];
            if (!row) return null;
            const branchKey =
              mode === "list"
                ? "list"
                : row.parentId && state.branches[row.parentId]
                  ? row.parentId
                  : "root";
            const branch = state.branches[branchKey];
            const endings: Array<{ key: string; depth: number }> = [];
            if (mode === "tree") {
              const nextDepth = visible[index + 1]?.depth ?? 0;
              if (row.isDirectory && state.expanded.has(row.key) && nextDepth <= entry.depth)
                endings.push({ key: row.key, depth: entry.depth + 1 });
              if (nextDepth < entry.depth) {
                let parentId = row.parentId;
                let depth = entry.depth - 1;
                while (parentId && depth >= nextDepth && state.rows[parentId]) {
                  if (state.expanded.has(parentId))
                    endings.push({ key: parentId, depth: depth + 1 });
                  parentId = state.rows[parentId]!.parentId;
                  depth--;
                }
              }
            }
            return (
              <Fragment key={row.key}>
                <FilesRow
                  row={row}
                  depth={entry.depth}
                  mode={mode}
                  selected={state.selected.has(row.key)}
                  expanded={state.expanded.has(row.key)}
                  position={(branch?.offset ?? 0) + (branch?.keys.indexOf(row.key) ?? index) + 1}
                  setSize={branch?.totalRows ?? visible.length}
                  tabIndex={
                    state.focusedKey === row.key ||
                    (!visible.some((item) => item.key === state.focusedKey) && index === 0)
                      ? 0
                      : -1
                  }
                  setRef={(element) => {
                    if (element) rowsRef.current.set(row.key, element);
                    else rowsRef.current.delete(row.key);
                  }}
                  onFocus={() => dispatch({ type: "focus", key: row.key })}
                  onKeyDown={(event) => keyDown(event, row, index)}
                  activate={() => {
                    dispatch({ type: "focus", key: row.key });
                    if (row.isDirectory && mode === "tree")
                      dispatch({ type: "toggle", key: row.key });
                  }}
                  select={() => dispatch({ type: "select", key: row.key })}
                  readonly={readonly}
                  pending={pending.has(row.key)}
                  now={state.observedAt}
                  action={(intent) => {
                    if (intent === "play") setPlayer(row);
                    else if (intent === "remove") setRemovals([row]);
                    else if (intent === "arr-search") setSearchTarget(row);
                    else void mutate(row, "recheck");
                  }}
                />
                {messages[row.key] && (
                  <div role="status" className="py-1 text-xs">
                    {row.name}: {messages[row.key]}
                  </div>
                )}
                {endings.map((ending) => branchControls(ending.key, ending.depth))}
              </Fragment>
            );
          })}
          {branchControls(rootKey, 0)}
        </div>
      </div>
      {focused && <FilesDetails row={focused} now={state.observedAt} />}
      {player?.previewUrl && (
        <MediaPreview
          fileName={player.name}
          filePath={player.path.replace(/^\//, "")}
          mimeType={player.mimeType}
          sizeBytes={player.size}
          previewUrl={player.previewUrl}
          onClose={() => setPlayer(null)}
        />
      )}
      {searchTarget && (
        <ConfirmModal
          show
          title="Search in Arr"
          confirmText="Search in Arr"
          confirmVariant={false}
          isConfirmDisabled={pending.has(searchTarget.key)}
          message={
            <>
              <p>{searchTarget.path}</p>
              <p>
                Enabled Arr instances owning this file will be searched. InfiniDysk will not remove
                or blocklist the current file. Arr quality, cutoff, and monitoring rules still
                apply; a replacement download is not guaranteed.
              </p>
            </>
          }
          onCancel={() => {
            if (!pending.has(searchTarget.key)) setSearchTarget(null);
          }}
          onConfirm={() => {
            void mutate(searchTarget, "arr-search").then(() => setSearchTarget(null));
          }}
        />
      )}
      {removals && (
        <ConfirmModal
          show
          title="Remove selected items"
          confirmText="Remove"
          isConfirmDisabled={
            pending.size > 0 || Boolean(previewError) || removals.some((row) => !previews[row.key])
          }
          message={
            <>
              <ul>
                {removals.map((row) => (
                  <li key={row.key}>
                    {row.path}
                    {messages[row.key] && <p>{messages[row.key]}</p>}
                  </li>
                ))}
              </ul>
              <p>
                Directory removal includes collapsed, hidden, and filtered-out descendants. Counts
                are a preview; the current subtree is removed at execution. Separate removals are
                not atomic.
              </p>
              {Object.keys(previews).length === removals.length ? (
                <p>
                  {Object.values(previews).reduce((sum, preview) => sum + preview.fileCount, 0)}{" "}
                  files,{" "}
                  {Object.values(previews).reduce((sum, preview) => sum + preview.dirCount, 0)}{" "}
                  directories,{" "}
                  {formatFileSize(
                    Object.values(previews).reduce((sum, preview) => sum + preview.totalBytes, 0),
                  )}
                  ,{" "}
                  {Object.values(previews).reduce(
                    (sum, preview) => sum + preview.linkedHistoryCount,
                    0,
                  )}{" "}
                  linked history entries
                </p>
              ) : (
                <p>Loading removal preview...</p>
              )}
              {previewError && (
                <p role="alert">
                  {previewError}
                  <Button onClick={() => setPreviewAttempt((value) => value + 1)}>
                    Retry preview
                  </Button>
                </p>
              )}
            </>
          }
          onCancel={() => {
            if (!pending.size) setRemovals(null);
          }}
          onConfirm={() => {
            void removeConfirmed();
          }}
        />
      )}
    </section>
  );
}

function BranchControls({
  branch,
  depth,
  retry,
  page,
}: {
  branch: BranchPage;
  depth: number;
  retry: () => void;
  page: (offset: number) => void;
}) {
  return (
    <div className={styles.branch} style={{ "--depth": depth } as CSSProperties}>
      {branch.status === "loading" && <span role="status">Loading...</span>}
      {branch.status === "error" && (
        <>
          <span role="alert">{branch.error}</span>
          {branch.error?.includes("Sign in") ? (
            <Link to={withUrlBase("/login")}>Sign in</Link>
          ) : (
            <Button size="xsmall" onClick={retry}>
              Retry
            </Button>
          )}
        </>
      )}
      {branch.status === "ready" && branch.totalRows === 0 && <span>No matching items</span>}
      {(branch.offset > 0 || branch.hasMore) && (
        <>
          <Button
            aria-label={`Previous page in ${branch.parentPath}`}
            size="xsmall"
            disabled={branch.offset === 0 || branch.status === "loading"}
            onClick={() => page(Math.max(0, branch.offset - FILES_PAGE_SIZE))}
          >
            <Icon name="chevron_left" />
          </Button>
          <span>
            {branch.offset + 1}-{Math.min(branch.offset + branch.keys.length, branch.totalRows)} of{" "}
            {branch.totalRows}
          </span>
          <Button
            aria-label={`Next page in ${branch.parentPath}`}
            size="xsmall"
            disabled={!branch.hasMore || branch.status === "loading"}
            onClick={() => page(branch.offset + FILES_PAGE_SIZE)}
          >
            <Icon name="chevron_right" />
          </Button>
        </>
      )}
    </div>
  );
}

type RowAction = "recheck" | "play" | "remove" | "arr-search";
function FilesRow(props: {
  row: FileResourceRow;
  depth: number;
  mode: string;
  selected: boolean;
  expanded: boolean;
  position: number;
  setSize: number;
  tabIndex: number;
  setRef: (element: HTMLButtonElement | null) => void;
  onFocus: () => void;
  onKeyDown: (event: KeyboardEvent) => void;
  activate: () => void;
  select: () => void;
  readonly: boolean;
  pending: boolean;
  now: number;
  action: (intent: RowAction) => void;
}) {
  const { row } = props;
  const cellRole = props.mode === "tree" ? "gridcell" : "cell";
  const healthIcon =
    row.health === "healthy" ? "check_circle" : row.health === "unknown" ? "help" : "warning";
  return (
    <div
      role="row"
      className={`${styles.row} ${props.mode === "list" ? styles.listRow : ""}`}
      aria-selected={props.selected}
      aria-level={props.mode === "tree" ? props.depth : undefined}
      aria-expanded={props.mode === "tree" && row.isDirectory ? props.expanded : undefined}
      aria-posinset={props.mode === "tree" ? props.position : undefined}
      aria-setsize={props.mode === "tree" ? props.setSize : undefined}
    >
      <div
        role={cellRole}
        className={styles.name}
        style={{ "--depth": props.depth - 1 } as CSSProperties}
      >
        <Checkbox
          aria-label={`Select ${row.name}`}
          checked={props.selected}
          disabled={props.readonly || !row.canDelete}
          onChange={props.select}
        />
        {row.isDirectory ? (
          <Button
            variant="ghost"
            size="medium"
            tabIndex={-1}
            className={styles.action}
            aria-label={`${props.expanded ? "Collapse" : "Expand"} ${row.name}`}
            onClick={props.activate}
          >
            <Icon name={props.expanded ? "expand_more" : "chevron_right"} />
          </Button>
        ) : (
          <Icon name="draft" className="!text-[18px]" />
        )}
        <button
          ref={props.setRef}
          type="button"
          className={styles.nameText}
          tabIndex={props.tabIndex}
          onFocus={props.onFocus}
          onKeyDown={props.onKeyDown}
          onClick={props.activate}
          title={row.path}
        >
          {row.name}
          {props.mode === "list" && <span className={styles.path}>{row.path}</span>}
        </button>
      </div>
      <div
        role={cellRole}
        title={row.healthMessage ?? ""}
        className={
          row.health === "healthy"
            ? "text-success"
            : row.health === "degraded" || row.health === "needs-attention"
              ? "text-warning"
              : ""
        }
      >
        {row.health && (
          <>
            <Icon name={healthIcon} className="!text-[14px]" /> {labels[row.health]}
          </>
        )}
      </div>
      <div role={cellRole} title={row.nextCheckAt ? date(row.nextCheckAt) : ""}>
        {row.scanState &&
          (row.scanState === "scheduled" || row.scanState === "due"
            ? `${labels[row.scanState]} ${row.nextCheckAt ? new Date(row.nextCheckAt).toLocaleDateString() : ""}`
            : labels[row.scanState])}
        {row.scanState === "checking" && row.progress !== null ? ` ${row.progress}%` : ""}
      </div>
      <div role={cellRole} title={date(row.addedAt)}>
        {age(row.addedAt, props.now)}
      </div>
      <div role={cellRole}>
        {row.isDirectory ? "" : row.size === null ? "Unknown" : formatFileSize(row.size)}
      </div>
      <div role={cellRole}>{row.libraryState && labels[row.libraryState]}</div>
      <div role={cellRole}>
        <FileActionStrip
          row={row}
          readonly={props.readonly}
          pending={props.pending}
          action={props.action}
        />
      </div>
    </div>
  );
}
function FileActionStrip({
  row,
  readonly,
  pending,
  action,
}: {
  row: FileResourceRow;
  readonly: boolean;
  pending: boolean;
  action: (intent: RowAction) => void;
}) {
  const mutationReason = readonly
    ? "Administrator access required."
    : pending
      ? "An action is in progress."
      : null;
  const actions = [
    {
      label: "Recheck",
      icon: "health_and_safety",
      reason: mutationReason ?? (row.canRecheck ? null : row.recheckDisabledReason),
      run: () => action("recheck"),
    },
    {
      label: "Play",
      icon: "play_arrow",
      reason:
        !row.isDirectory && row.previewUrl && isPlayableMedia(row)
          ? null
          : "Playback is unavailable for this file type.",
      run: () => action("play"),
    },
    {
      label: "Download",
      icon: "download",
      reason: row.previewUrl ? null : "Select a file.",
      run: () => {
        if (row.previewUrl)
          window.location.assign(appendQueryParam(row.previewUrl, "download", "true"));
      },
    },
    {
      label: "Export NZB",
      icon: "file_export",
      reason: row.nzbBlobId ? null : "Original NZB is unavailable.",
      run: () => {
        if (row.nzbBlobId)
          window.location.assign(
            withUrlBase(`/api/download-nzb?${new URLSearchParams({ nzbBlobId: row.nzbBlobId })}`),
          );
      },
    },
    {
      label: "Remove",
      icon: "delete",
      reason: mutationReason ?? (row.canDelete ? null : row.deleteDisabledReason),
      run: () => action("remove"),
    },
    {
      label: "Search in Arr",
      icon: "search",
      reason: mutationReason ?? (row.canSearchArr ? null : row.searchArrDisabledReason),
      run: () => action("arr-search"),
    },
  ];
  return (
    <div className={styles.actions}>
      {actions.map((item) => (
        <span
          key={item.label}
          title={item.reason ?? item.label}
          tabIndex={item.reason ? 0 : undefined}
          aria-label={item.reason ? `${item.label} ${row.name}: ${item.reason}` : undefined}
        >
          <Button
            size="medium"
            variant="ghost"
            className={styles.action}
            aria-label={`${item.label} ${row.name}`}
            disabled={Boolean(item.reason)}
            onClick={item.run}
          >
            <Icon name={item.icon} />
          </Button>
        </span>
      ))}
    </div>
  );
}
function FilesDetails({ row, now }: { row: FileResourceRow; now: number }) {
  const values: Array<[string, string]> = [
    ["Path", row.path],
    ["ID", row.id ?? "Synthetic category"],
    [
      "Storage kind",
      ({ 201: "NZB", 202: "RAR", 203: "Multipart" } as Record<number, string>)[row.subType ?? 0] ??
        "Directory",
    ],
    ["Added", date(row.addedAt)],
    ["Posted", `${date(row.releaseDate)} (${age(row.releaseDate, now)})`],
    ["Last health check", date(row.lastHealthCheck)],
    ["Next due", date(row.nextCheckAt)],
    ["Observed health", row.health ? labels[row.health]! : "Not applicable"],
    ["Health result", date(row.healthResultAt)],
    [
      "Repair outcome",
      row.repairAction === null
        ? "Unknown"
        : ["None", "Repaired", "Deleted", "Action needed", "Repaired via PAR2"][row.repairAction]!,
    ],
    ["Health detail", row.healthMessage ?? "None"],
    ["Job", row.jobName ?? "Unknown"],
    ["Original NZB", row.nzbFileName ?? "Unknown"],
    ["Category", row.category ?? "Unknown"],
    ["Indexer", row.indexerName ?? "Unknown"],
    ["Last played (release)", date(row.lastPlayedAt)],
    ["Library links", row.libraryPaths.join("; ") || labels[row.libraryState ?? "unknown"]!],
  ];
  return (
    <section className={styles.details} aria-label={`Details for ${row.name}`}>
      <dl>
        {values.map(([label, value]) => (
          <div key={label}>
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}
function FilesToolbar({
  filters,
  mode,
  sort,
  direction,
  update,
  refresh,
}: {
  filters: FilesFilters;
  mode: string;
  sort: string;
  direction: string;
  update: (filters: FilesFilters, mode?: string, sort?: string, direction?: string) => void;
  refresh: () => void;
}) {
  const [draft, setDraft] = useState(filters.q);
  const [expanded, setExpanded] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const commitSearch = useRef((value: string) => change({ q: value }));
  commitSearch.current = (value: string) => change({ q: value });
  useEffect(() => {
    setDraft(filters.q);
  }, [filters.q]);
  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
    },
    [],
  );
  function change(patch: Partial<FilesFilters>) {
    const result = filesFiltersSchema.safeParse({ ...filters, ...patch });
    if (!result.success) {
      setError(result.error.issues[0]?.message ?? "Invalid filter.");
      return;
    }
    setError(null);
    update(result.data);
  }
  function search(value: string, immediate = false) {
    setDraft(value);
    if (timer.current) clearTimeout(timer.current);
    if (immediate) change({ q: value });
    else timer.current = setTimeout(() => commitSearch.current(value), FILES_SEARCH_DEBOUNCE_MS);
  }
  function select(
    label: string,
    value: string,
    options: Array<[string, string]>,
    changed: (value: string) => void,
  ) {
    return (
      <label>
        {label}
        <select
          aria-label={label}
          className="select select-xs"
          value={value}
          onChange={(event) => changed(event.target.value)}
        >
          {options.map(([key, text]) => (
            <option key={key} value={key}>
              {text}
            </option>
          ))}
        </select>
      </label>
    );
  }
  const schedules = [
    "all",
    "due",
    "scheduled",
    "recheck-queued",
    "repair-pending",
    "never-scheduled",
    "checking",
  ];
  return (
    <>
      <div className={styles.toolbar}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            search(draft, true);
          }}
        >
          <label>
            Search name or path
            <input
              aria-label="Search name or path"
              className="input input-xs"
              value={draft}
              maxLength={256}
              onChange={(event) => search(event.target.value)}
            />
          </label>
        </form>
        <Button size="xsmall" aria-label="Clear search" onClick={() => search("", true)}>
          <Icon name="close" />
        </Button>
        <details className={styles.healthMenu}>
          <summary>Health{filters.health.length ? ` (${filters.health.length})` : ""}</summary>
          <div className={styles.healthOptions}>
            {(["healthy", "degraded", "needs-attention", "unknown"] as const).map((health) => (
              <label key={health}>
                <Checkbox
                  checked={filters.health.includes(health)}
                  onChange={(event) =>
                    change({
                      health: event.target.checked
                        ? [...filters.health, health]
                        : filters.health.filter((value) => value !== health),
                    })
                  }
                />
                {labels[health]}
              </label>
            ))}
          </div>
        </details>
        {select(
          "Scan state",
          filters.schedule,
          schedules.map((value) => [
            value,
            value === "all"
              ? "All"
              : value === "never-scheduled"
                ? "No scheduled date"
                : value === "recheck-queued"
                  ? "Recheck queued"
                  : (labels[value] ?? value),
          ]),
          (value) => change({ schedule: value as FilesFilters["schedule"] }),
        )}
        {select(
          "Library",
          filters.library,
          ["all", "in-library", "not-in-library", "unknown", "not-configured"].map((value) => [
            value,
            value === "all" ? "All" : labels[value]!,
          ]),
          (value) => change({ library: value as FilesFilters["library"] }),
        )}
        <div role="group" aria-label="View mode" className="flex gap-1">
          {["tree", "list"].map((value) => (
            <Button
              key={value}
              size="xsmall"
              aria-pressed={mode === value}
              onClick={() => update(filters, value)}
            >
              <Icon name={value === "tree" ? "account_tree" : "view_list"} />
              {value === "tree" ? "Tree" : "List"}
            </Button>
          ))}
        </div>
        {select(
          "Sort",
          sort,
          ["name", "size", "added", "posted", "last-check", "next-check", "type", "health"].map(
            (value) => [value, value],
          ),
          (value) => update(filters, mode, value, direction),
        )}
        <Button
          size="xsmall"
          aria-label="Reverse sort direction"
          onClick={() => update(filters, mode, sort, direction === "asc" ? "desc" : "asc")}
        >
          <Icon name={direction === "asc" ? "arrow_upward" : "arrow_downward"} />
        </Button>
        <Button
          size="xsmall"
          aria-expanded={expanded}
          onClick={() => setExpanded((value) => !value)}
        >
          <Icon name="filter_list" />
          Filters
        </Button>
        <Button size="xsmall" aria-label="Refresh Files" title="Refresh Files" onClick={refresh}>
          <Icon name="refresh" />
        </Button>
      </div>
      {expanded && (
        <div className={styles.filters}>
          {(
            [
              ["addedAfter", "Added from"],
              ["addedBefore", "Added before"],
              ["postedAfter", "Posted from"],
              ["postedBefore", "Posted before"],
              ["checkedAfter", "Checked from"],
              ["checkedBefore", "Checked before"],
              ["playedAfter", "Release played from"],
              ["playedBefore", "Release played before"],
            ] as const
          ).map(([key, label]) => (
            <label key={key}>
              {label}
              <input
                className="input input-xs"
                aria-label={label}
                type="datetime-local"
                value={filters[key] === null ? "" : localInput(filters[key])}
                onChange={(event) =>
                  change({
                    [key]: event.target.value
                      ? Math.floor(new Date(event.target.value).getTime() / 1000)
                      : null,
                  })
                }
              />
            </label>
          ))}
          {(
            [
              ["minSize", "Min MiB"],
              ["maxSize", "Max MiB"],
            ] as const
          ).map(([key, label]) => (
            <label key={key}>
              {label}
              <input
                className="input input-xs"
                aria-label={label}
                type="number"
                min="0"
                step="any"
                value={filters[key] === null ? "" : filters[key] / 1048576}
                onChange={(event) =>
                  change({
                    [key]:
                      event.target.value === ""
                        ? null
                        : Math.round(Number(event.target.value) * 1048576),
                  })
                }
              />
            </label>
          ))}
          {(
            [
              ["category", "Category"],
              ["indexer", "Indexer"],
            ] as const
          ).map(([key, label]) => (
            <label key={key}>
              {label}
              <input
                className="input input-xs"
                aria-label={label}
                value={filters[key]}
                maxLength={255}
                onChange={(event) => change({ [key]: event.target.value })}
              />
            </label>
          ))}
          {select(
            "Storage kind",
            String(filters.subType ?? ""),
            [
              ["", "All"],
              ["201", "NZB"],
              ["202", "RAR"],
              ["203", "Multipart"],
            ],
            (value) =>
              change({ subType: value === "" ? null : (Number(value) as FilesFilters["subType"]) }),
          )}
          {select(
            "Repair outcome",
            String(filters.repairAction ?? ""),
            [
              ["", "All"],
              ["0", "None"],
              ["1", "Repaired"],
              ["2", "Deleted"],
              ["3", "Action needed"],
              ["4", "PAR2 repaired"],
            ],
            (value) => change({ repairAction: value === "" ? null : Number(value) }),
          )}
          {select(
            "Original NZB",
            String(filters.hasNzb ?? ""),
            [
              ["", "All"],
              ["true", "Available"],
              ["false", "Unavailable"],
            ],
            (value) => change({ hasNzb: value === "" ? null : value === "true" }),
          )}
          <Button
            size="xsmall"
            onClick={() => {
              search("", true);
              update(defaultFilesFilters);
            }}
          >
            Clear filters
          </Button>
        </div>
      )}
      {error && (
        <p role="alert" className="text-error text-xs">
          {error}
        </p>
      )}
    </>
  );
}
function localInput(seconds: number): string {
  const value = new Date(seconds * 1000);
  return new Date(value.getTime() - value.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}
