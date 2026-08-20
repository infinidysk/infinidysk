import { useRevalidator, useSearchParams } from "react-router";
import type { Route } from "./+types/route";
import { backendClient, type HistorySlot, type QueueSlot } from "~/clients/backend-client.server";
import { HistoryTable } from "./components/history-table/history-table";
import { QueueTable } from "./components/queue-table/queue-table";
import { useState, useRef, useEffect, useCallback } from "react";
import { useHistoryEvents, useQueueEvents } from "./controllers/events-controller";
import { useQueueHistoryWebsocket } from "./controllers/websocket-controller";
import { useUploadController } from "./controllers/nzb-upload-controller";
import { useQueueDropzone } from "./controllers/dropzone-controller";
import { Alert, Button } from "~/components/ui";
import { SimpleDropdown } from "./components/simple-dropdown/simple-dropdown";
import { useIsReadOnly } from "~/auth/authorization";
import { isDefaultList, parseHistoryListParams, parseQueueListParams } from "./list-params";

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
    const queuePage = parsePage(url.searchParams.get("qp"));
    const historyPage = parsePage(url.searchParams.get("hp"));
    const queuePageSize = parsePageSize(url.searchParams.get("qps"));
    const historyPageSize = parsePageSize(url.searchParams.get("hps"));
    const queueParams = parseQueueListParams(url.searchParams);
    const historyParams = parseHistoryListParams(url.searchParams);
    const queueStart = (queuePage - 1) * queuePageSize;
    const queueFetchStart = Math.max(0, queueStart - 1);
    const queueFetchLimit = queuePageSize + (queuePage > 1 ? 2 : 1);
    const [queue, history, config] = await Promise.all([
        backendClient.getQueue(queueFetchLimit, queueFetchStart, {
            search: queueParams.query, category: queueParams.category, status: queueParams.status,
            sort: queueParams.sort ?? undefined, direction: queueParams.direction ?? undefined,
        }),
        backendClient.getHistory(historyPageSize, (historyPage - 1) * historyPageSize, {
            search: historyParams.query, category: historyParams.category, status: historyParams.status,
            sort: historyParams.sort ?? undefined, direction: historyParams.direction ?? undefined,
        }),
        backendClient.getConfig(["api.categories", "api.manual-category"]),
    ]);
    const categoriesValue = config
        .find(x => x.configName === "api.categories")
        ?.configValue ?? "uncategorized,audio,software,tv,movies";
    const manualCategory = config
        .find(x => x.configName === "api.manual-category")
        ?.configValue?.trim() || "uncategorized";
    let categories = categoriesValue.split(',').map(x => x.trim()).filter(Boolean);
    if (!categories.includes(manualCategory)) {
        categories = [manualCategory, ...categories];
    }

    return {
        queueSlots: (queue?.slots || []).slice(
            queuePage > 1 ? 1 : 0,
            (queuePage > 1 ? 1 : 0) + queuePageSize,
        ),
        previousQueueSlot: queuePage > 1 ? queue?.slots?.[0] : undefined,
        nextQueueSlot: queue?.slots?.[(queuePage > 1 ? 1 : 0) + queuePageSize],
        historySlots: history?.slots || [],
        totalQueueCount: queue?.noofslots || 0,
        totalHistoryCount: history?.noofslots || 0,
        categories: categories,
        manualCategory: manualCategory,
        queuePage: queuePage,
        historyPage: historyPage,
        queuePageSize,
        historyPageSize,
        queueParams,
        historyParams,
    };
}

export default function Queue(props: Route.ComponentProps) {
    const isReadOnly = useIsReadOnly();
    const { queuePageSize, historyPageSize, queuePage, historyPage, queueParams, historyParams } = props.loaderData;
    const [queueSlots, setQueueSlots] = useState<PresentationQueueSlot[]>(props.loaderData.queueSlots);
    const [historySlots, setHistorySlots] = useState<PresentationHistorySlot[]>(props.loaderData.historySlots);
    const [totalQueueCount, setTotalQueueCount] = useState(props.loaderData.totalQueueCount);
    const [totalHistoryCount, setTotalHistoryCount] = useState(props.loaderData.totalHistoryCount);
    const [uploadingFiles, setUploadingFiles] = useState<UploadingFile[]>([]);
    const uploadQueueRef = useRef<UploadingFile[]>([]);
    const manualCategoryRef = useRef<string>(props.loaderData.manualCategory);
    const isUploadingRef = useRef(false);
    const [, setSearchParams] = useSearchParams();
    const revalidator = useRevalidator();
    const [queueQueryDraft, setQueueQueryDraft] = useState(queueParams.query);
    const [historyQueryDraft, setHistoryQueryDraft] = useState(historyParams.query);

    useEffect(() => { setQueueSlots(previous => mergePresentationSlots(props.loaderData.queueSlots, previous)); }, [props.loaderData.queueSlots]);
    useEffect(() => { setHistorySlots(previous => mergePresentationSlots(props.loaderData.historySlots, previous)); }, [props.loaderData.historySlots]);
    useEffect(() => { setTotalQueueCount(props.loaderData.totalQueueCount); }, [props.loaderData.totalQueueCount]);
    useEffect(() => { setTotalHistoryCount(props.loaderData.totalHistoryCount); }, [props.loaderData.totalHistoryCount]);

    const queueTotalPages = Math.max(1, Math.ceil(totalQueueCount / queuePageSize));
    const historyTotalPages = Math.max(1, Math.ceil(totalHistoryCount / historyPageSize));
    useEffect(() => { setQueueQueryDraft(queueParams.query); }, [queueParams.query]);
    useEffect(() => { setHistoryQueryDraft(historyParams.query); }, [historyParams.query]);
    const isQueueLive = queuePage === 1 && isDefaultList(queueParams);
    const isHistoryLive = historyPage === 1 && isDefaultList(historyParams);

    const updateListParams = useCallback((kind: "queue" | "history", mutator: (params: URLSearchParams) => void) => {
        const pageKey = kind === "queue" ? "qp" : "hp";
        setSearchParams(previous => {
            const next = new URLSearchParams(previous);
            mutator(next);
            next.set(pageKey, "1");
            return next;
        }, { preventScrollReset: true });
    }, [setSearchParams]);

    const setQueueParam = useCallback((key: string, value: string) => updateListParams("queue", params => {
        if (value) params.set(key, value); else params.delete(key);
    }), [updateListParams]);
    const setHistoryParam = useCallback((key: string, value: string) => updateListParams("history", params => {
        if (value) params.set(key, value); else params.delete(key);
    }), [updateListParams]);

    useEffect(() => {
        if (queueQueryDraft.trim() === queueParams.query) return;
        const timer = window.setTimeout(() => setQueueParam("qq", queueQueryDraft.trim()), 300);
        return () => window.clearTimeout(timer);
    }, [queueQueryDraft, queueParams.query, setQueueParam]);
    useEffect(() => {
        if (historyQueryDraft.trim() === historyParams.query) return;
        const timer = window.setTimeout(() => setHistoryParam("hq", historyQueryDraft.trim()), 300);
        return () => window.clearTimeout(timer);
    }, [historyQueryDraft, historyParams.query, setHistoryParam]);

    const setPageParam = useCallback((key: string, page: number) => {
        setSearchParams(prev => {
            const next = new URLSearchParams(prev);
            next.set(key, String(page));
            return next;
        }, { preventScrollReset: true });
    }, [setSearchParams]);
    const onQueuePageSelected = useCallback((page: number) => setPageParam("qp", page), [setPageParam]);
    const onHistoryPageSelected = useCallback((page: number) => setPageParam("hp", page), [setPageParam]);

    useEffect(() => {
        if (queuePage > queueTotalPages) onQueuePageSelected(queueTotalPages);
    }, [queuePage, queueTotalPages, onQueuePageSelected]);
    useEffect(() => {
        if (historyPage > historyTotalPages) onHistoryPageSelected(historyTotalPages);
    }, [historyPage, historyTotalPages, onHistoryPageSelected]);

    const setPageSizeParam = useCallback((sizeKey: string, pageKey: string, size: number) => {
        setSearchParams(prev => {
            const next = new URLSearchParams(prev);
            next.set(sizeKey, String(size));
            next.set(pageKey, "1");
            return next;
        }, { preventScrollReset: true });
    }, [setSearchParams]);
    const onQueuePageSizeSelected = useCallback(
        (size: number) => setPageSizeParam("qps", "qp", size),
        [setPageSizeParam],
    );
    const onHistoryPageSizeSelected = useCallback(
        (size: number) => setPageSizeParam("hps", "hp", size),
        [setPageSizeParam],
    );

    const combinedQueueSlots = [...uploadingFiles.map(file => file.queueSlot), ...queueSlots];

    // queue/history events
    const queueEvents = useQueueEvents(setUploadingFiles, setQueueSlots, uploadQueueRef, queuePageSize, isQueueLive);
    const historyEvents = useHistoryEvents(setHistorySlots, historyPageSize);

    // websocket
    const revalidate = useCallback((): void => {
        void revalidator.revalidate();
    }, [revalidator]);
    useQueueHistoryWebsocket(
        queueEvents,
        historyEvents,
        isQueueLive,
        isHistoryLive,
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

            {dropzone.rejectMessage && (
                <Alert variant="warning">
                    {dropzone.rejectMessage}
                </Alert>
            )}

            {/* queue */}
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
                    {dropzone.isDragActive && <div className="pointer-events-none absolute inset-0 z-20 flex items-center justify-center rounded border-2 border-dashed border-primary bg-primary/10" />}
                    {!isReadOnly && <input {...dropzone.getInputProps()} />}
                    <QueueTable
                        queueSlots={combinedQueueSlots}
                        totalQueueCount={totalQueueCount + uploadingFiles.length}
                        pageNumber={queuePage}
                        pageSize={queuePageSize}
                        pageSizeOptions={PAGE_SIZE_OPTIONS}
                        totalPages={queueTotalPages}
                        isLive={isQueueLive}
                        listParams={queueParams}
                        searchDraft={queueQueryDraft}
                        onSearchDraftChange={setQueueQueryDraft}
                        onFilterChange={(key, value) => setQueueParam(key, value)}
                        onClearFilters={() => updateListParams("queue", params => ["qq", "qcat", "qstatus", "qsort"].forEach(key => params.delete(key)))}
                        onSort={(field) => setQueueParam("qsort", nextSortValue(queueParams, field))}
                        onPageSelected={onQueuePageSelected}
                        onPageSizeSelected={onQueuePageSizeSelected}
                        categories={props.loaderData.categories}
                        onIsSelectedChanged={queueEvents.onSelectQueueSlots}
                        onIsRemovingChanged={queueEvents.onRemovingQueueSlots}
                        onRemoved={queueEvents.onRemoveQueueSlots}
                        onMovedToTop={queueEvents.onMoveQueueSlotsToTop}
                        previousQueueSlot={props.loaderData.previousQueueSlot}
                        nextQueueSlot={props.loaderData.nextQueueSlot}
                    />
                </div>
            </div>

            {/* history */}
            {(totalHistoryCount > 0 || historySlots.length > 0 || !isDefaultList(historyParams)) &&
                <HistoryTable
                    historySlots={historySlots}
                    totalHistoryCount={totalHistoryCount}
                    pageNumber={historyPage}
                    pageSize={historyPageSize}
                    pageSizeOptions={PAGE_SIZE_OPTIONS}
                    totalPages={historyTotalPages}
                    isLive={isHistoryLive}
                    categories={props.loaderData.categories}
                    listParams={historyParams}
                    searchDraft={historyQueryDraft}
                    onSearchDraftChange={setHistoryQueryDraft}
                    onFilterChange={(key, value) => setHistoryParam(key, value)}
                    onClearFilters={() => updateListParams("history", params => ["hq", "hcat", "hstatus", "hsort"].forEach(key => params.delete(key)))}
                    onSort={(field) => setHistoryParam("hsort", nextSortValue(historyParams, field))}
                    onPageSelected={onHistoryPageSelected}
                    onPageSizeSelected={onHistoryPageSizeSelected}
                    onIsSelectedChanged={historyEvents.onSelectHistorySlots}
                    onIsRemovingChanged={historyEvents.onRemovingHistorySlots}
                    onRemoved={historyEvents.onRemoveHistorySlots}
                />
            }
        </div >
    );
}

function nextSortValue(params: { sort: string | null, direction: "asc" | "desc" | null }, field: string): string {
    if (params.sort !== field) return `${field}:asc`;
    return params.direction === "asc" ? `${field}:desc` : "";
}

function mergePresentationSlots<T extends { nzo_id: string, isSelected?: boolean, isRemoving?: boolean }>(
    loaded: T[],
    previous: T[],
): T[] {
    const flags = new Map(previous.map(row => [row.nzo_id, {
        isSelected: row.isSelected,
        isRemoving: row.isRemoving,
    }]));
    return loaded.map(row => ({ ...row, ...flags.get(row.nzo_id) }));
}

export type PresentationHistorySlot = HistorySlot & {
    isSelected?: boolean,
    isRemoving?: boolean,
}

export type PresentationQueueSlot = QueueSlot & {
    isUploading?: boolean,
    isSelected?: boolean,
    isRemoving?: boolean,
    error?: string,
}

export type UploadingFile = {
    file: File,
    queueSlot: PresentationQueueSlot,
}
