import { useRevalidator, useSearchParams } from "react-router";
import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { QueueTable } from "./components/queue-table/queue-table";
import { useState, useRef, useEffect, useCallback } from "react";
import { useHistoryEvents, useQueueEvents } from "./controllers/events-controller";
import { useQueueHistoryWebsocket } from "./controllers/websocket-controller";
import { useUploadController } from "./controllers/nzb-upload-controller";
import { useQueueDropzone } from "./controllers/dropzone-controller";
import { Alert, Button, PageHeader } from "~/components/ui";
import { SimpleDropdown } from "~/components/simple-dropdown/simple-dropdown";
import { useIsReadOnly } from "~/auth/authorization";
import {
  isDefaultList,
  isQueueSortField,
  parseJobsListParams,
  type JobsListParams,
} from "./list-params";
import {
  combinedListWindow,
  statusAppliesToHistory,
  statusAppliesToQueue,
} from "./combined-window";
import { PREVIEW_HISTORY_SLOTS, PREVIEW_QUEUE_SLOTS } from "./queue-preview";

export const PAGE_SIZE_OPTIONS = [25, 50, 100, 250] as const;
const DEFAULT_PAGE_SIZE = 100;

function parsePage(value: string | null): number {
  const page = parseInt(value ?? "1", 10);
  return Number.isFinite(page) && page > 0 ? page : 1;
}

function parsePageSize(value: string | null): number {
  const size = parseInt(value ?? String(DEFAULT_PAGE_SIZE), 10);
  return (PAGE_SIZE_OPTIONS as readonly number[]).includes(size) ? size : DEFAULT_PAGE_SIZE;
}

export async function loader({ request }: Route.LoaderArgs) {
  const url = new URL(request.url);
  if (import.meta.env.DEV && url.searchParams.get("preview") === "1") {
    return previewLoaderData();
  }

  const page = parsePage(url.searchParams.get("qp"));
  const pageSize = parsePageSize(url.searchParams.get("qps"));
  const listParams = parseJobsListParams(url.searchParams);
  const includeQueue = statusAppliesToQueue(listParams.status);
  const includeHistory = statusAppliesToHistory(listParams.status);
  const combinedStart = (page - 1) * pageSize;
  const queueFetchStart = Math.max(0, combinedStart - (combinedStart > 0 ? 1 : 0));
  const queueFetchLimit = pageSize + (combinedStart > 0 ? 2 : 1);
  const queueSort = isQueueSortField(listParams.sort) ? listParams.sort : undefined;
  const queueOptions = {
    search: listParams.query,
    category: listParams.category,
    status: includeQueue ? listParams.status : "",
    sort: queueSort,
    direction: queueSort ? (listParams.direction ?? undefined) : undefined,
  };

  const [queue, config] = await Promise.all([
    backendClient.getQueue(includeQueue ? queueFetchLimit : 1, includeQueue ? queueFetchStart : 0, {
      ...queueOptions,
      status: includeQueue ? listParams.status : "",
    }),
    backendClient.getConfig(["api.categories", "api.manual-category"]),
  ]);

  const totalQueueCount = includeQueue ? queue?.noofslots || 0 : 0;
  const listWindow = combinedListWindow(totalQueueCount, page, pageSize);
  const historyOptions = {
    search: listParams.query,
    category: listParams.category,
    status: includeHistory ? listParams.status : "",
    sort: listParams.sort ?? undefined,
    direction: listParams.direction ?? undefined,
  };
  const history = includeHistory
    ? await backendClient.getHistory(
        listWindow.historyLimit > 0 ? listWindow.historyLimit : 1,
        listWindow.historyLimit > 0 ? listWindow.historyStart : 0,
        historyOptions,
      )
    : undefined;

  const categoriesValue =
    config.find((x) => x.configName === "api.categories")?.configValue ??
    "uncategorized,audio,software,tv,movies";
  const manualCategory =
    config.find((x) => x.configName === "api.manual-category")?.configValue?.trim() ||
    "uncategorized";
  let categories = categoriesValue
    .split(",")
    .map((x) => x.trim())
    .filter(Boolean);
  if (!categories.includes(manualCategory)) {
    categories = [manualCategory, ...categories];
  }

  const queueOffset = includeQueue && combinedStart > 0 ? 1 : 0;
  const fetchedQueueSlots = includeQueue ? queue?.slots || [] : [];

  return {
    queueSlots: fetchedQueueSlots.slice(queueOffset, queueOffset + listWindow.queueLimit),
    previousQueueSlot: includeQueue && combinedStart > 0 ? fetchedQueueSlots[0] : undefined,
    nextQueueSlot: includeQueue
      ? fetchedQueueSlots[queueOffset + listWindow.queueLimit]
      : undefined,
    historySlots: listWindow.historyLimit > 0 ? history?.slots || [] : [],
    totalQueueCount,
    totalHistoryCount: includeHistory ? history?.noofslots || 0 : 0,
    categories: categories,
    manualCategory: manualCategory,
    page,
    pageSize,
    listParams,
    paused: queue?.paused ?? false,
    pauseInt: queue?.pause_int ?? "0",
  };
}

function previewLoaderData() {
  const listParams: JobsListParams = {
    query: "",
    category: "",
    status: "",
    sort: null,
    direction: null,
  };
  return {
    queueSlots: PREVIEW_QUEUE_SLOTS,
    previousQueueSlot: undefined,
    nextQueueSlot: undefined,
    historySlots: PREVIEW_HISTORY_SLOTS,
    totalQueueCount: PREVIEW_QUEUE_SLOTS.length,
    totalHistoryCount: PREVIEW_HISTORY_SLOTS.length,
    categories: ["uncategorized", "tv", "movies", "anime"],
    manualCategory: "uncategorized",
    page: 1,
    pageSize: DEFAULT_PAGE_SIZE,
    listParams,
    paused: false,
    pauseInt: "0",
  };
}

export default function Queue(props: Route.ComponentProps) {
  const isReadOnly = useIsReadOnly();
  const { pageSize, page, listParams } = props.loaderData;
  const [queueSlots, setQueueSlots] = useState<PresentationQueueSlot[]>(
    props.loaderData.queueSlots,
  );
  const [historySlots, setHistorySlots] = useState<PresentationHistorySlot[]>(
    props.loaderData.historySlots,
  );
  const [totalQueueCount, setTotalQueueCount] = useState(props.loaderData.totalQueueCount);
  const [totalHistoryCount, setTotalHistoryCount] = useState(props.loaderData.totalHistoryCount);
  const [uploadingFiles, setUploadingFiles] = useState<UploadingFile[]>([]);
  const uploadQueueRef = useRef<UploadingFile[]>([]);
  const manualCategoryRef = useRef<string>(props.loaderData.manualCategory);
  const isUploadingRef = useRef(false);
  const [searchParams, setSearchParams] = useSearchParams();
  const revalidator = useRevalidator();
  const [queryDraft, setQueryDraft] = useState(listParams.query);

  useEffect(() => {
    setQueueSlots((previous) => mergePresentationSlots(props.loaderData.queueSlots, previous));
  }, [props.loaderData.queueSlots]);
  useEffect(() => {
    setHistorySlots((previous) => mergePresentationSlots(props.loaderData.historySlots, previous));
  }, [props.loaderData.historySlots]);
  useEffect(() => {
    setTotalQueueCount(props.loaderData.totalQueueCount);
  }, [props.loaderData.totalQueueCount]);
  useEffect(() => {
    const seconds = Number(props.loaderData.pauseInt);
    if (!props.loaderData.paused || !Number.isFinite(seconds) || seconds <= 0) return;
    const handle = window.setTimeout(
      () => {
        void revalidator.revalidate();
      },
      Math.min(seconds, 86_400) * 1000,
    );
    return () => window.clearTimeout(handle);
  }, [props.loaderData.paused, props.loaderData.pauseInt, revalidator]);

  useEffect(() => {
    setTotalHistoryCount(props.loaderData.totalHistoryCount);
  }, [props.loaderData.totalHistoryCount]);

  const combinedTotal = totalQueueCount + totalHistoryCount;
  const totalPages = Math.max(1, Math.ceil(combinedTotal / pageSize));
  useEffect(() => {
    setQueryDraft(listParams.query);
  }, [listParams.query]);
  const isLive = page === 1 && isDefaultList(listParams);

  const updateListParams = useCallback(
    (mutator: (params: URLSearchParams) => void) => {
      setSearchParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          mutator(next);
          next.set("qp", "1");
          return next;
        },
        { preventScrollReset: true },
      );
    },
    [setSearchParams],
  );

  const setListParam = useCallback(
    (key: string, value: string) =>
      updateListParams((params) => {
        if (value) params.set(key, value);
        else params.delete(key);
      }),
    [updateListParams],
  );

  useEffect(() => {
    if (queryDraft.trim() === listParams.query) return;
    const timer = window.setTimeout(() => setListParam("qq", queryDraft.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [queryDraft, listParams.query, setListParam]);

  const setPageParam = useCallback(
    (nextPage: number) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          next.set("qp", String(nextPage));
          return next;
        },
        { preventScrollReset: true },
      );
    },
    [setSearchParams],
  );

  useEffect(() => {
    if (page > totalPages) setPageParam(totalPages);
  }, [page, totalPages, setPageParam]);

  const onPageSizeSelected = useCallback(
    (size: number) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          next.set("qps", String(size));
          next.set("qp", "1");
          return next;
        },
        { preventScrollReset: true },
      );
    },
    [setSearchParams],
  );

  const combinedQueueSlots = [...uploadingFiles.map((file) => file.queueSlot), ...queueSlots];
  const visibleHistoryLimit = combinedListWindow(totalQueueCount, page, pageSize).historyLimit;

  // queue/history events
  const queueEvents = useQueueEvents(
    setUploadingFiles,
    setQueueSlots,
    uploadQueueRef,
    pageSize,
    isLive,
  );
  const historyEvents = useHistoryEvents(setHistorySlots, Math.max(visibleHistoryLimit, 1));

  // websocket
  const revalidate = useCallback((): void => {
    void revalidator.revalidate();
  }, [revalidator]);
  useQueueHistoryWebsocket(
    queueEvents,
    historyEvents,
    isLive,
    isLive,
    setTotalQueueCount,
    setTotalHistoryCount,
    revalidate,
  );

  // uploads
  const dropzone = useQueueDropzone(setUploadingFiles, uploadQueueRef, manualCategoryRef);
  useUploadController(isUploadingRef, uploadQueueRef, uploadingFiles, setUploadingFiles);

  // view
  return (
    <div className="flex min-h-full min-w-full flex-col gap-8 px-4 py-4 text-sm text-base-content/70 md:px-8">
      <PageHeader
        title="Queue"
        subtitle="Jobs from Sonarr, Radarr, or a manual NZB upload. Active items stay at the top; finished jobs remain in this list as history."
      />
      {searchParams.get("preview") === "1" && (
        <Alert className="alert-soft" variant="info">
          Preview with sample jobs — this is not your real queue.
        </Alert>
      )}
      {dropzone.rejectMessage && <Alert variant="warning">{dropzone.rejectMessage}</Alert>}
      {props.loaderData.paused && (
        <Alert className="alert-soft" variant="info">
          {Number(props.loaderData.pauseInt) > 0
            ? `Queue downloads are paused by schedule until ${new Date(Date.now() + Number(props.loaderData.pauseInt) * 1000).toLocaleString()}. Active imports keep running.`
            : "The queue is paused. Resume it from SABnzbd-compatible clients or wait for a manual resume."}
        </Alert>
      )}

      <div className="min-h-[413.9px] min-[450px]:min-h-[382.9px]">
        {!isReadOnly && (
          <div className="mb-3 flex flex-wrap items-center justify-end gap-2">
            <label className="flex items-center gap-2 text-xs text-base-content/60">
              Category
              <SimpleDropdown
                type="bordered"
                options={props.loaderData.categories}
                valueRef={manualCategoryRef}
                ariaLabel="Upload category"
              />
            </label>
            <Button variant="primary" size="small" onClick={dropzone.open}>
              Upload NZB
            </Button>
          </div>
        )}
        <div className="relative" {...(isReadOnly ? {} : dropzone.getRootProps())}>
          {dropzone.isDragActive && (
            <div className="pointer-events-none absolute inset-0 z-20 flex items-center justify-center rounded border-2 border-dashed border-primary bg-primary/10" />
          )}
          {!isReadOnly && <input {...dropzone.getInputProps()} />}
          <QueueTable
            queueSlots={combinedQueueSlots}
            historySlots={historySlots}
            totalQueueCount={totalQueueCount + uploadingFiles.length}
            totalHistoryCount={totalHistoryCount}
            pageNumber={page}
            pageSize={pageSize}
            pageSizeOptions={PAGE_SIZE_OPTIONS}
            totalPages={totalPages}
            isLive={isLive}
            listParams={listParams}
            searchDraft={queryDraft}
            onSearchDraftChange={setQueryDraft}
            onFilterChange={(key, value) => setListParam(key, value)}
            onClearFilters={() =>
              updateListParams((params) =>
                ["qq", "qcat", "qstatus", "qsort"].forEach((key) => params.delete(key)),
              )
            }
            onSort={(field) => setListParam("qsort", nextSortValue(listParams, field))}
            onPageSelected={setPageParam}
            onPageSizeSelected={onPageSizeSelected}
            categories={props.loaderData.categories}
            onIsSelectedChanged={queueEvents.onSelectQueueSlots}
            onIsRemovingChanged={queueEvents.onRemovingQueueSlots}
            onRemoved={queueEvents.onRemoveQueueSlots}
            onMovedToTop={queueEvents.onMoveQueueSlotsToTop}
            onHistoryIsSelectedChanged={historyEvents.onSelectHistorySlots}
            onHistoryIsRemovingChanged={historyEvents.onRemovingHistorySlots}
            onHistoryRemoved={historyEvents.onRemoveHistorySlots}
            previousQueueSlot={props.loaderData.previousQueueSlot}
            nextQueueSlot={props.loaderData.nextQueueSlot}
          />
        </div>
      </div>
    </div>
  );
}

function nextSortValue(
  params: { sort: string | null; direction: "asc" | "desc" | null },
  field: string,
): string {
  if (params.sort !== field) return `${field}:asc`;
  return params.direction === "asc" ? `${field}:desc` : "";
}

function mergePresentationSlots<
  T extends { nzo_id: string; isSelected?: boolean; isRemoving?: boolean },
>(loaded: T[], previous: T[]): T[] {
  const flags = new Map(
    previous.map((row) => [
      row.nzo_id,
      {
        isSelected: row.isSelected,
        isRemoving: row.isRemoving,
      },
    ]),
  );
  return loaded.map((row) => ({ ...row, ...flags.get(row.nzo_id) }));
}

export type PresentationHistorySlot = HistorySlot & {
  isSelected?: boolean;
  isRemoving?: boolean;
};

export type PresentationQueueSlot = QueueSlot & {
  isUploading?: boolean;
  isSelected?: boolean;
  isRemoving?: boolean;
  error?: string;
};

export type UploadingFile = {
  file: File;
  queueSlot: PresentationQueueSlot;
};
