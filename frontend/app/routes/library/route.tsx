import type { Route } from "./+types/route";
import { useCallback, useEffect, useState } from "react";
import { Form, Link, redirect, useFetcher, useRevalidator, useSearchParams } from "react-router";
import {
  backendClient,
  type LibraryBrowseResponse,
  type LibraryCatalogItem,
  type LibraryFileDetails,
} from "~/clients/backend-client.server";
import { getDownloadKey } from "~/auth/downloads.server";
import { getFrontendRuntimeConfig } from "../../../server/runtime-config";
import { formatFileSize } from "~/utils/file-size";
import { withUrlBase } from "~/utils/url-base";
import { Alert, Badge, Button, Input, PageHeader } from "~/components/ui";
import { LibraryFileModal, type LibraryModalFeedback } from "./file-modal";
import { MediaPreview } from "~/components/media-preview";
import { plexRequest } from "~/utils/plex-request";

type Category = "all" | "shows" | "movies" | "unmatched";
type View = "files" | "groups";
type MappingFilter = "all" | "internal" | "external" | "broken";
type QualityFilter = "all" | "4k" | "1080p" | "720p" | "sd" | "unknown";
type CacheFilter = "all" | "any" | "complete" | "empty" | "unavailable";

export type LibraryPageData = {
  query: {
    q: string;
    category: Category;
    view: View;
    match: "all" | "matched" | "unmatched";
    season: number | null;
    type: MappingFilter;
    quality: QualityFilter;
    cache: CacheFilter;
    page: number;
    group: string | null;
    groupPage: number;
  };
  browse: LibraryBrowseResponse;
  previewUrls: Record<string, string>;
  nativeCacheActive: boolean;
};

function parseCategory(value: string | null): Category {
  return value === "shows" || value === "movies" || value === "unmatched" ? value : "all";
}

function parseView(value: string | null): View {
  return value === "groups" ? "groups" : "files";
}

function parseType(value: string | null): MappingFilter {
  return value === "internal" || value === "external" || value === "broken" ? value : "all";
}

function parseQuality(value: string | null): QualityFilter {
  return value === "4k" ||
    value === "1080p" ||
    value === "720p" ||
    value === "sd" ||
    value === "unknown"
    ? value
    : "all";
}

function parseCache(value: string | null): CacheFilter {
  return value === "any" || value === "complete" || value === "empty" || value === "unavailable"
    ? value
    : "all";
}

function parsePage(value: string | null): number {
  const page = Number(value);
  return Number.isSafeInteger(page) && page > 0 ? page : 1;
}

export async function loader({ request }: Route.LoaderArgs): Promise<LibraryPageData | Response> {
  const settings = await backendClient.getConfig(["media.library-enabled"]);
  if (
    settings.some(
      (item) =>
        item.configName === "media.library-enabled" && item.configValue.toLowerCase() === "false",
    )
  )
    return redirect("/settings?tab=library");
  const url = new URL(request.url);
  const query = {
    q: url.searchParams.get("q")?.trim() ?? "",
    category:
      parseView(url.searchParams.get("view")) === "groups" &&
      parseCategory(url.searchParams.get("category")) === "all"
        ? "shows"
        : parseCategory(url.searchParams.get("category")),
    view: parseView(url.searchParams.get("view")),
    match:
      url.searchParams.get("match") === "matched" || url.searchParams.get("match") === "unmatched"
        ? (url.searchParams.get("match") as "matched" | "unmatched")
        : ("all" as const),
    season: (() => {
      const value = Number(url.searchParams.get("season"));
      return url.searchParams.has("season") &&
        Number.isInteger(value) &&
        value >= 0 &&
        value <= 1000
        ? value
        : null;
    })(),
    type: parseType(url.searchParams.get("type")),
    quality: parseQuality(url.searchParams.get("quality")),
    cache: parseCache(url.searchParams.get("cache")),
    page: parsePage(url.searchParams.get("page")),
    group: url.searchParams.get("group"),
    groupPage: parsePage(url.searchParams.get("groupPage")),
  };
  const browse = await backendClient.getLibraryBrowse({
    ...(query.q ? { q: query.q } : {}),
    category: query.category,
    view: query.view,
    match: query.match,
    ...(query.season !== null ? { season: query.season } : {}),
    type: query.type,
    quality: query.quality,
    cache: query.cache,
    page: query.page,
    ...(query.group ? { group: query.group } : {}),
    groupPage: query.groupPage,
  });
  const { frontendBackendApiKey } = getFrontendRuntimeConfig();
  const previewUrls: Record<string, string> = {};
  for (const { item } of [...(browse.expandedGroup?.items ?? []), ...(browse.files ?? [])]) {
    if (item.kind === "internal" && item.contentPath && item.davItemId) {
      const relative = item.contentPath.startsWith("/")
        ? item.contentPath.slice(1)
        : item.contentPath;
      const key = getDownloadKey(relative, frontendBackendApiKey);
      previewUrls[item.davItemId] = `/view${item.contentPath}?downloadKey=${key}`;
    }
  }
  let nativeCacheActive = false;
  try {
    const status = await backendClient.getNativeCacheStatus();
    nativeCacheActive = status.activeMode === "native";
  } catch {
    // The catalog remains usable if the optional cache status cannot be loaded.
  }
  return { query, browse, previewUrls, nativeCacheActive };
}

export async function action({ request }: Route.ActionArgs) {
  const form = await request.formData();
  const operation = form.get("operation");
  const davItemId = form.get("davItemId");
  if (operation === "prewarm" && typeof davItemId === "string" && davItemId) {
    try {
      await backendClient.warmPrefetch([davItemId]);
      return Response.json({ status: true });
    } catch (error) {
      const message = error instanceof Error ? error.message : "Could not prewarm this file.";
      return Response.json({ status: false, error: message }, { status: 502 });
    }
  }
  return Response.json({ status: false, error: "Unknown library action." }, { status: 400 });
}

function withParams(params: URLSearchParams, changes: Record<string, string | null>): string {
  const next = new URLSearchParams(params);
  for (const [key, value] of Object.entries(changes)) {
    if (value === null) next.delete(key);
    else next.set(key, value);
  }
  return `?${next.toString()}`;
}

function indexAge(value: string | null | undefined): string {
  if (!value) return "Index scan pending";
  const time = new Date(value);
  return Number.isNaN(time.valueOf())
    ? "Index time unavailable"
    : `Scanned ${time.toLocaleString()}`;
}

const categories: { value: Category; label: string }[] = [
  { value: "all", label: "All media" },
  { value: "shows", label: "TV shows" },
  { value: "movies", label: "Movies" },
  { value: "unmatched", label: "Unmatched" },
];

export default function Library({ loaderData }: Route.ComponentProps) {
  const [searchParams] = useSearchParams();
  const revalidator = useRevalidator();
  const fetcher = useFetcher<{ status: boolean; error?: string }>();
  const groupFetcher = useFetcher<LibraryPageData>();
  const { query, browse, previewUrls, nativeCacheActive } = loaderData;
  const [expandedKey, setExpandedKey] = useState<string | null>(query.group);
  const [selected, setSelected] = useState<LibraryCatalogItem | null>(null);
  const [details, setDetails] = useState<LibraryFileDetails | null>(null);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [detailsError, setDetailsError] = useState<string | null>(null);
  const [showPreview, setShowPreview] = useState(false);
  const [actionPending, setActionPending] = useState(false);
  const [feedback, setFeedback] = useState<LibraryModalFeedback>(null);
  const [plexSyncing, setPlexSyncing] = useState(false);
  const [plexSyncError, setPlexSyncError] = useState<string | null>(null);

  useEffect(() => {
    setExpandedKey(query.group);
  }, [query.group, query.category, query.type, query.quality, query.cache, query.q, query.page]);

  const expandedGroup =
    groupFetcher.data?.browse.expandedGroup?.key === expandedKey
      ? groupFetcher.data.browse.expandedGroup
      : browse.expandedGroup?.key === expandedKey
        ? browse.expandedGroup
        : null;
  const expandedPreviewUrls =
    groupFetcher.data?.browse.expandedGroup?.key === expandedKey
      ? groupFetcher.data.previewUrls
      : previewUrls;

  const loadGroup = useCallback(
    (key: string, page = 1) => {
      setExpandedKey(key);
      void groupFetcher.load(
        withUrlBase(`/library${withParams(searchParams, { group: key, groupPage: String(page) })}`),
      );
    },
    [groupFetcher, searchParams],
  );

  useEffect(() => {
    if (
      plexSyncing ||
      (!browse.plexStatus.syncing && (browse.plexStatus.ready || browse.plexStatus.warning))
    )
      return;
    const timer = window.setTimeout(() => {
      void revalidator.revalidate();
    }, 3000);
    return () => window.clearTimeout(timer);
  }, [browse.plexStatus, plexSyncing, revalidator]);

  const syncPlex = useCallback(async () => {
    setPlexSyncing(true);
    setPlexSyncError(null);
    try {
      await plexRequest("library/sync");
      let status: { syncing: boolean; warning: string | null };
      do {
        await new Promise((resolve) => setTimeout(resolve, 3000));
        status = await plexRequest<{ syncing: boolean; warning: string | null }>("library/status");
      } while (status.syncing);
      if (status.warning) setPlexSyncError(status.warning);
      void revalidator.revalidate();
    } catch (error) {
      setPlexSyncError(error instanceof Error ? error.message : "Plex sync failed.");
    } finally {
      setPlexSyncing(false);
    }
  }, [revalidator]);

  const openModal = useCallback((item: LibraryCatalogItem) => {
    setSelected(item);
    setDetails(null);
    setDetailsError(null);
    setShowPreview(false);
    setFeedback(null);
    if (item.kind === "internal" && item.davItemId) {
      setDetailsLoading(true);
      void fetch(
        withUrlBase(
          `/api/get-library-file-details?davItemId=${encodeURIComponent(item.davItemId)}`,
        ),
      )
        .then(async (response) => {
          if (!response.ok) throw new Error("Could not load file details.");
          setDetails((await response.json()) as LibraryFileDetails);
        })
        .catch(() => setDetailsError("Could not load file details."))
        .finally(() => setDetailsLoading(false));
    }
  }, []);

  const closeModal = useCallback(() => {
    setSelected(null);
    setDetails(null);
    setDetailsError(null);
    setShowPreview(false);
    setFeedback(null);
  }, []);

  const runAction = useCallback(async (fn: () => Promise<{ ok: boolean; message: string }>) => {
    setActionPending(true);
    setFeedback(null);
    try {
      const result = await fn();
      setFeedback({ variant: result.ok ? "success" : "danger", message: result.message });
    } catch (error) {
      setFeedback({
        variant: "danger",
        message: error instanceof Error ? error.message : "Action failed.",
      });
    } finally {
      setActionPending(false);
    }
  }, []);

  const onRunHealthCheck = useCallback(() => {
    void runAction(async () => {
      const response = await fetch(withUrlBase("/api/trigger-health-check"), { method: "POST" });
      if (response.status === 409) {
        const body = (await response.json().catch(() => null)) as { error?: string } | null;
        return { ok: false, message: body?.error || "Background repairs are disabled." };
      }
      if (!response.ok) return { ok: false, message: "Could not start health checks." };
      return { ok: true, message: "Health checks queued." };
    });
  }, [runAction]);

  const onRequeue = useCallback(() => {
    const id = selected?.davItemId;
    if (!id) return;
    void runAction(async () => {
      const response = await fetch(
        withUrlBase(`/api/requeue-action-needed-health-checks?davItemId=${encodeURIComponent(id)}`),
        { method: "POST" },
      );
      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as { error?: string } | null;
        return { ok: false, message: body?.error || "Could not queue this file for re-check." };
      }
      const body = (await response.json()) as { requeuedCount?: number };
      const count = body.requeuedCount ?? 0;
      return {
        ok: true,
        message: count === 0 ? "No action-needed result to re-check." : "File queued for re-check.",
      };
    });
  }, [runAction, selected]);

  const onPrewarm = useCallback(() => {
    if (!selected?.davItemId) return;
    const form = new FormData();
    form.set("operation", "prewarm");
    form.set("davItemId", selected.davItemId);
    void fetcher.submit(form, { method: "post" });
  }, [fetcher, selected]);

  const selectedPreviewUrl =
    selected?.davItemId != null ? (expandedPreviewUrls[selected.davItemId] ?? null) : null;
  const totalPages = Math.max(1, Math.ceil(browse.totalGroups / browse.pageSize));
  const filePages = Math.max(1, Math.ceil((browse.totalFiles ?? 0) / browse.pageSize));

  return (
    <div className="mx-auto flex w-full max-w-7xl flex-col gap-6 px-4 py-8 md:px-6">
      <PageHeader
        title="Media Library"
        subtitle="Browse your indexed shows and movies, with every mapped file in one place."
        actions={
          <span className="rounded-lg border border-base-content/10 bg-base-200 px-3 py-2 text-xs text-base-content/65">
            {indexAge(browse.indexScannedAt)}
          </span>
        }
      />
      {browse.indexWarning ? (
        <Alert variant="warning" role="alert">
          Library index may be stale: {browse.indexWarning}
        </Alert>
      ) : null}
      <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-base-content/10 bg-base-200 px-4 py-3 text-sm">
        <span>
          {browse.plexStatus.ready
            ? `Plex matched index: ${browse.plexStatus.entryCount.toLocaleString()} items · synced ${new Date(browse.plexStatus.syncedAt!).toLocaleString()}`
            : "Plex matching is pending. Sync Plex to classify your library."}
        </span>
        <Button
          type="button"
          onClick={() => void syncPlex()}
          disabled={plexSyncing || browse.plexStatus.syncing}
        >
          {plexSyncing || browse.plexStatus.syncing ? "Syncing Plex…" : "Sync Plex now"}
        </Button>
      </div>
      {plexSyncError || browse.plexStatus.warning ? (
        <Alert variant="warning" role="alert">
          {plexSyncError ?? browse.plexStatus.warning}
        </Alert>
      ) : null}

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Metric label="Indexed files" value={browse.totalItems} hint="Matching current filters" />
        <Metric
          label="Valid mappings"
          value={browse.healthyItems}
          hint="Internal files"
          tone="success"
        />
        <Metric
          label="Need attention"
          value={browse.attentionItems}
          hint="Unmapped or broken"
          tone="warning"
        />
        <Metric
          label="Unmatched"
          value={browse.unmatchedItems}
          hint={browse.plexStatus.ready ? "No Plex filename match" : "Plex sync pending"}
          tone="info"
        />
      </div>

      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-base-content/15">
        <nav className="flex gap-1 overflow-x-auto" aria-label="Media type">
          {categories
            .filter(({ value }) => query.view === "files" || value !== "all")
            .map(({ value, label }) => (
              <Link
                key={value}
                to={withParams(searchParams, {
                  category: value,
                  page: null,
                  group: null,
                  groupPage: null,
                })}
                aria-current={query.category === value ? "page" : undefined}
                className={`border-b-2 px-4 py-3 text-sm font-semibold transition-colors ${
                  query.category === value
                    ? "border-primary text-primary"
                    : "border-transparent text-base-content/60 hover:text-base-content"
                }`}
              >
                {label}
                {value === "unmatched" && browse.plexStatus.ready
                  ? ` (${browse.unmatchedItems})`
                  : ""}
              </Link>
            ))}
        </nav>
        <nav className="flex gap-2 pb-2 text-sm" aria-label="Library view">
          <Link
            to={withParams(searchParams, {
              view: "files",
              page: null,
              group: null,
              groupPage: null,
            })}
            aria-current={query.view === "files" ? "page" : undefined}
            className={query.view === "files" ? "btn btn-sm btn-primary" : "btn btn-sm btn-outline"}
          >
            File table
          </Link>
          <Link
            to={withParams(searchParams, {
              view: "groups",
              category: query.category === "all" ? "shows" : query.category,
              page: null,
              group: null,
              groupPage: null,
            })}
            aria-current={query.view === "groups" ? "page" : undefined}
            className={
              query.view === "groups" ? "btn btn-sm btn-primary" : "btn btn-sm btn-outline"
            }
          >
            Grouped browse
          </Link>
        </nav>
      </div>

      <Form
        method="get"
        className="flex flex-wrap items-end gap-3 rounded-xl border border-base-content/10 bg-base-200 p-4"
      >
        <input type="hidden" name="category" value={query.category} />
        <input type="hidden" name="view" value={query.view} />
        <label className="min-w-48 flex-1 text-xs font-semibold text-base-content/70">
          Search media and paths
          <Input
            name="q"
            defaultValue={query.q}
            placeholder="Show, movie, file, or link path…"
            className="mt-1 w-full"
          />
        </label>
        <label className="text-xs font-semibold text-base-content/70">
          Mapping
          <select
            name="type"
            defaultValue={query.type}
            className="select mt-1 block select-bordered"
          >
            <option value="all">All mappings</option>
            <option value="internal">Internal</option>
            <option value="external">External</option>
            <option value="broken">Broken links</option>
          </select>
        </label>
        {query.view === "files" && (
          <>
            <label className="text-xs font-semibold text-base-content/70">
              Match
              <select
                name="match"
                defaultValue={query.match}
                className="select mt-1 block select-bordered"
              >
                <option value="all">All matches</option>
                <option value="matched">Matched</option>
                <option value="unmatched">Unmatched</option>
              </select>
            </label>
            <label className="text-xs font-semibold text-base-content/70">
              Season
              <Input
                name="season"
                type="number"
                min={0}
                max={1000}
                defaultValue={query.season ?? ""}
                placeholder="Any"
                className="mt-1 w-24"
              />
            </label>
          </>
        )}
        <label className="text-xs font-semibold text-base-content/70">
          Quality
          <select
            name="quality"
            defaultValue={query.quality}
            className="select mt-1 block select-bordered"
          >
            <option value="all">All qualities</option>
            <option value="4k">4K</option>
            <option value="1080p">1080p</option>
            <option value="720p">720p</option>
            <option value="sd">SD</option>
            <option value="unknown">Unknown</option>
          </select>
        </label>
        <label className="text-xs font-semibold text-base-content/70">
          Native Cache
          <select
            name="cache"
            defaultValue={query.cache}
            className="select mt-1 block select-bordered"
          >
            <option value="all">All cache states</option>
            <option value="any">Has cached data</option>
            <option value="complete">Fully cached</option>
            <option value="empty">Not cached</option>
            <option value="unavailable">Unavailable</option>
          </select>
        </label>
        <Button type="submit">Apply filters</Button>
      </Form>
      <p className="-mt-3 text-xs text-base-content/55">
        Quality is inferred from the filename. Native Cache shows verified coverage for the current
        file revision{nativeCacheActive ? "." : "; Native Cache is inactive."}
      </p>

      {query.view === "files" ? (
        <>
          <div className="flex items-center justify-between gap-4">
            <h2 className="text-lg font-semibold">
              {categories.find((category) => category.value === query.category)?.label} files
            </h2>
            <span className="text-xs text-base-content/55">
              {(browse.totalFiles ?? 0).toLocaleString()} files · page {browse.page} of {filePages}
            </span>
          </div>
          <div className="overflow-x-auto rounded-xl border border-base-content/10 bg-base-200">
            <table className="table table-sm w-full min-w-[960px]">
              <thead>
                <tr>
                  <th>File / path</th>
                  <th>Show / movie title</th>
                  <th>Season</th>
                  <th>Episode</th>
                  <th>Type</th>
                  <th>Size</th>
                  <th>Mapping</th>
                  <th>Details</th>
                </tr>
              </thead>
              <tbody>
                {(browse.files ?? []).map((row) => (
                  <tr
                    key={
                      row.item.davItemId ?? row.item.mappings[0]?.linkPath ?? row.item.displayName
                    }
                  >
                    <td className="max-w-64">
                      <button
                        type="button"
                        className="link block max-w-64 truncate text-left font-medium"
                        title={row.item.displayName}
                        onClick={() => openModal(row.item)}
                      >
                        {row.item.displayName}
                      </button>
                      <span
                        className="block max-w-64 truncate text-xs text-base-content/50"
                        title={row.item.contentPath ?? row.item.mappings[0]?.linkPath ?? ""}
                      >
                        {row.item.contentPath ?? row.item.mappings[0]?.linkPath ?? "—"}
                      </span>
                    </td>
                    <td>{row.title ?? <span className="text-base-content/45">Unmatched</span>}</td>
                    <td>{row.season == null ? "—" : row.season}</td>
                    <td>{row.episode == null ? "—" : row.episode}</td>
                    <td>
                      {row.category === "shows"
                        ? "TV"
                        : row.category === "movies"
                          ? "Movie"
                          : "Unmatched"}
                    </td>
                    <td>{row.item.size == null ? "—" : formatFileSize(row.item.size)}</td>
                    <td>
                      <Badge>{row.item.health}</Badge>
                    </td>
                    <td>
                      <button
                        type="button"
                        className="btn btn-xs btn-outline"
                        onClick={() => openModal(row.item)}
                      >
                        Details
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!browse.files?.length && (
              <p className="p-8 text-center text-sm text-base-content/60">
                {browse.plexStatus.ready
                  ? "No files match these filters."
                  : "Plex matching is pending; files without a match remain visible under All media."}
              </p>
            )}
          </div>
          {(browse.totalFiles ?? 0) > browse.pageSize && (
            <Pagination
              page={browse.page}
              total={filePages}
              previous={withParams(searchParams, { page: String(browse.page - 1) })}
              next={withParams(searchParams, { page: String(browse.page + 1) })}
              label="Files"
            />
          )}
        </>
      ) : (
        <>
          <div className="flex items-center justify-between gap-4">
            <h2 className="text-lg font-semibold">
              {categories.find((category) => category.value === query.category)?.label}
            </h2>
            <span className="text-xs text-base-content/55">
              {browse.totalGroups.toLocaleString()} groups · page {browse.page} of {totalPages}
            </span>
          </div>
          {browse.groups.length === 0 ? (
            <div className="rounded-xl border border-dashed border-base-content/20 bg-base-200 p-10 text-center">
              <p className="font-semibold">
                {browse.plexStatus.ready
                  ? "No media matches these filters."
                  : "Plex matching has not completed yet."}
              </p>
              <p className="mt-1 text-sm text-base-content/60">
                {browse.plexStatus.ready
                  ? "Try a different search or mapping filter."
                  : "Use Sync Plex now to match your library files."}
              </p>
            </div>
          ) : (
            <div className="flex flex-col gap-3">
              {browse.groups.map((group) => {
                const open = expandedKey === group.key;
                return (
                  <section
                    key={group.key}
                    className="overflow-hidden rounded-xl border border-base-content/10 bg-base-200"
                  >
                    <button
                      type="button"
                      onClick={() => (open ? setExpandedKey(null) : loadGroup(group.key))}
                      aria-expanded={open}
                      aria-controls={`library-group-${browse.groups.indexOf(group)}`}
                      className="flex w-full items-center gap-4 p-4 text-left transition-colors hover:bg-base-content/5"
                    >
                      <span className="flex h-14 w-12 shrink-0 items-center justify-center rounded-lg bg-primary/15 text-xl font-bold text-primary">
                        {group.category === "shows"
                          ? "TV"
                          : group.category === "movies"
                            ? "▶"
                            : "?"}
                      </span>
                      <span className="min-w-0 flex-1">
                        <strong className="block truncate text-base">{group.title}</strong>
                        <span className="mt-1 block text-xs text-base-content/60">
                          {group.itemCount.toLocaleString()}{" "}
                          {group.itemCount === 1 ? "file" : "files"}
                        </span>
                      </span>
                      <span className="hidden items-center gap-2 text-xs sm:flex">
                        {group.quality ? (
                          <Badge>
                            {group.quality === "4k"
                              ? "4K"
                              : group.quality === "unknown"
                                ? "Quality unknown"
                                : group.quality}
                          </Badge>
                        ) : null}
                        {group.itemCount === 1 ? (
                          <span className="text-base-content/65">
                            Cache{" "}
                            {group.cachePercentage == null
                              ? "unavailable"
                              : `${group.cachePercentage}%`}
                          </span>
                        ) : null}
                        {group.healthyCount > 0 && <Badge>{group.healthyCount} valid</Badge>}
                        {group.attentionCount > 0 && (
                          <Badge>{group.attentionCount} need attention</Badge>
                        )}
                      </span>
                      <span className="text-xl text-base-content/50" aria-hidden="true">
                        {open ? "⌄" : "›"}
                      </span>
                    </button>
                    {open ? (
                      <div
                        id={`library-group-${browse.groups.indexOf(group)}`}
                        className="border-t border-base-content/10 bg-base-300/45 px-4 py-2"
                      >
                        {!expandedGroup ? (
                          <p role="status" className="py-4 text-sm text-base-content/60">
                            Loading files…
                          </p>
                        ) : null}
                        {expandedGroup?.items.map(
                          ({ item, season, episode, quality, cachePercentage }) => (
                            <div
                              key={item.davItemId ?? item.mappings[0]?.linkPath ?? item.displayName}
                              className="border-b border-base-content/10 py-3 last:border-b-0"
                            >
                              <div className="flex flex-wrap items-center gap-3">
                                {episode ? (
                                  <span className="w-16 shrink-0 font-mono text-xs font-bold text-primary">
                                    {episode}
                                  </span>
                                ) : null}
                                <div className="min-w-48 flex-1">
                                  <button
                                    type="button"
                                    className="link text-left font-semibold"
                                    onClick={() => openModal(item)}
                                  >
                                    {item.displayName}
                                  </button>
                                  <p className="truncate text-xs text-base-content/50">
                                    {season ? `${season} · ` : ""}
                                    {item.contentPath ??
                                      item.mappings[0]?.linkPath ??
                                      "External link"}
                                  </p>
                                </div>
                                <span className="text-xs text-base-content/70">
                                  {item.size != null ? formatFileSize(item.size) : "—"}
                                </span>
                                <Badge>
                                  {quality === "4k"
                                    ? "4K"
                                    : quality === "unknown"
                                      ? "Quality unknown"
                                      : quality}
                                </Badge>
                                <span className="text-xs text-base-content/65">
                                  Cache{" "}
                                  {cachePercentage == null ? "unavailable" : `${cachePercentage}%`}
                                </span>
                                <Badge>{item.health}</Badge>
                                <button
                                  type="button"
                                  className="btn btn-sm btn-outline"
                                  onClick={() => openModal(item)}
                                >
                                  Details
                                </button>
                              </div>
                              <details className="mt-2 text-xs text-base-content/65">
                                <summary className="cursor-pointer">
                                  {item.mappingCount}{" "}
                                  {item.mappingCount === 1 ? "mapping" : "mappings"}
                                </summary>
                                <ul className="mt-2 space-y-1 pl-4">
                                  {item.mappings.map((mapping) => (
                                    <li key={mapping.linkPath} className="break-all">
                                      <Badge>{mapping.status}</Badge> {mapping.linkPath} →{" "}
                                      {mapping.targetText}
                                    </li>
                                  ))}
                                </ul>
                              </details>
                            </div>
                          ),
                        )}
                        {expandedGroup && expandedGroup.totalItems > expandedGroup.pageSize ? (
                          <div className="flex items-center justify-between py-3 text-sm">
                            <button
                              type="button"
                              className="btn btn-sm"
                              disabled={expandedGroup.page <= 1 || groupFetcher.state !== "idle"}
                              onClick={() => loadGroup(group.key, expandedGroup.page - 1)}
                            >
                              Previous files
                            </button>
                            <span>
                              Page {expandedGroup.page} of{" "}
                              {Math.ceil(expandedGroup.totalItems / expandedGroup.pageSize)}
                            </span>
                            <button
                              type="button"
                              className="btn btn-sm"
                              disabled={
                                expandedGroup.page * expandedGroup.pageSize >=
                                  expandedGroup.totalItems || groupFetcher.state !== "idle"
                              }
                              onClick={() => loadGroup(group.key, expandedGroup.page + 1)}
                            >
                              Next files
                            </button>
                          </div>
                        ) : null}
                      </div>
                    ) : null}
                  </section>
                );
              })}
            </div>
          )}
          {browse.totalGroups > browse.pageSize ? (
            <Pagination
              page={browse.page}
              total={totalPages}
              previous={withParams(searchParams, {
                page: String(browse.page - 1),
                group: null,
                groupPage: null,
              })}
              next={withParams(searchParams, {
                page: String(browse.page + 1),
                group: null,
                groupPage: null,
              })}
              label="Groups"
            />
          ) : null}
        </>
      )}

      {fetcher.data?.status === false ? (
        <Alert variant="danger" role="alert">
          {fetcher.data.error ?? "Could not prewarm this file."}
        </Alert>
      ) : null}
      {selected ? (
        <LibraryFileModal
          item={selected}
          quality={
            (browse.files ?? []).find(
              ({ item }) =>
                item.davItemId === selected.davItemId && item.displayName === selected.displayName,
            )?.quality ??
            expandedGroup?.items.find(
              ({ item }) =>
                item.davItemId === selected.davItemId && item.displayName === selected.displayName,
            )?.quality ??
            "unknown"
          }
          cachePercentage={
            (browse.files ?? []).find(
              ({ item }) =>
                item.davItemId === selected.davItemId && item.displayName === selected.displayName,
            )?.cachePercentage ??
            expandedGroup?.items.find(
              ({ item }) =>
                item.davItemId === selected.davItemId && item.displayName === selected.displayName,
            )?.cachePercentage ??
            null
          }
          details={details}
          detailsLoading={detailsLoading}
          detailsError={detailsError}
          previewUrl={selectedPreviewUrl}
          canPrewarm={nativeCacheActive}
          actionState={actionPending || fetcher.state !== "idle" ? "pending" : "idle"}
          feedback={
            fetcher.data?.status === true
              ? { variant: "success", message: "Prewarm requested." }
              : feedback
          }
          onClose={closeModal}
          onPreview={() => setShowPreview(true)}
          onRunHealthCheck={onRunHealthCheck}
          onRequeue={onRequeue}
          onPrewarm={onPrewarm}
        />
      ) : null}
      {showPreview && selected && selectedPreviewUrl ? (
        <MediaPreview
          fileName={selected.displayName}
          filePath={selected.contentPath ?? selected.displayName}
          mimeType={mimeTypeFor(selected.displayName)}
          sizeBytes={selected.size ?? null}
          previewUrl={selectedPreviewUrl}
          onClose={() => setShowPreview(false)}
        />
      ) : null}
    </div>
  );
}

function Metric({
  label,
  value,
  hint,
  tone = "default",
}: {
  label: string;
  value: number;
  hint: string;
  tone?: "default" | "success" | "warning" | "info";
}) {
  const color = {
    default: "text-base-content",
    success: "text-success",
    warning: "text-warning",
    info: "text-info",
  }[tone];
  return (
    <div className="rounded-xl border border-base-content/10 bg-base-200 p-4">
      <p className="text-xs font-semibold text-base-content/65">{label}</p>
      <p className={`mt-2 text-3xl font-bold tracking-tight ${color}`}>{value.toLocaleString()}</p>
      <p className="mt-1 text-xs text-base-content/50">{hint}</p>
    </div>
  );
}

function Pagination({
  page,
  total,
  previous,
  next,
  label,
}: {
  page: number;
  total: number;
  previous: string;
  next: string;
  label: string;
}) {
  return (
    <nav
      className="mt-4 flex items-center justify-between gap-3 text-xs"
      aria-label={`${label} pagination`}
    >
      <span className="text-base-content/60">
        {label} page {page} of {total}
      </span>
      <div className="join">
        {page > 1 ? (
          <Link className="btn btn-sm join-item" to={previous}>
            Previous
          </Link>
        ) : null}
        {page < total ? (
          <Link className="btn btn-sm join-item" to={next}>
            Next
          </Link>
        ) : null}
      </div>
    </nav>
  );
}

function mimeTypeFor(name: string): string {
  const lower = name.toLowerCase();
  if (lower.endsWith(".mp4")) return "video/mp4";
  if (lower.endsWith(".mkv")) return "video/x-matroska";
  if (lower.endsWith(".avi")) return "video/x-msvideo";
  if (lower.endsWith(".mov")) return "video/quicktime";
  if (lower.endsWith(".webm")) return "video/webm";
  if (lower.endsWith(".mp3")) return "audio/mpeg";
  if (lower.endsWith(".flac")) return "audio/flac";
  if (lower.endsWith(".ogg") || lower.endsWith(".oga")) return "audio/ogg";
  if (lower.endsWith(".m4a")) return "audio/mp4";
  if (lower.endsWith(".wav")) return "audio/wav";
  return "application/octet-stream";
}
