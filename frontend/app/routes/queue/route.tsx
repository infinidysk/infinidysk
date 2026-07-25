import { useSearchParams } from "react-router";
import type { Route } from "./+types/route";
import { backendClient, type HistorySlot, type QueueSlot } from "~/clients/backend-client.server";
import { HistoryTable } from "./components/history-table/history-table";
import { QueueTable } from "./components/queue-table/queue-table";
import { useState, useRef, useEffect, useCallback } from "react";
import { useHistoryEvents, useQueueEvents } from "./controllers/events-controller";
import { useQueueHistoryWebsocket } from "./controllers/websocket-controller";
import { useUploadController } from "./controllers/nzb-upload-controller";
import { useQueueDropzone } from "./controllers/dropzone-controller";
import { Alert } from "~/components/ui";

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
    const [queue, history, config] = await Promise.all([
        backendClient.getQueue(queuePageSize, (queuePage - 1) * queuePageSize),
        backendClient.getHistory(historyPageSize, (historyPage - 1) * historyPageSize),
        backendClient.getConfig(["api.categories", "api.manual-category"]),
    ]);
    const categoriesValue = config
        .find(x => x.configName === "api.categories")
        ?.configValue ?? "uncategorized,audio,software,tv,movies";
    const manualCategory = config
        .find(x => x.configName === "api.manual-category")
        ?.configValue ?? "uncategorized";
    let categories = categoriesValue.split(',').map(x => x.trim());
    if (!categories.includes(manualCategory)) {
        categories = [manualCategory, ...categories];
    }

    return {
        queueSlots: queue?.slots || [],
        historySlots: history?.slots || [],
        totalQueueCount: queue?.noofslots || 0,
        totalHistoryCount: history?.noofslots || 0,
        categories: categories,
        manualCategory: manualCategory,
        queuePage: queuePage,
        historyPage: historyPage,
        queuePageSize,
        historyPageSize,
    }
}

export default function Queue(props: Route.ComponentProps) {
    const { queuePageSize, historyPageSize, queuePage, historyPage } = props.loaderData;
    const [queueSlots, setQueueSlots] = useState<PresentationQueueSlot[]>(props.loaderData.queueSlots);
    const [historySlots, setHistorySlots] = useState<PresentationHistorySlot[]>(props.loaderData.historySlots);
    const [totalQueueCount, setTotalQueueCount] = useState(props.loaderData.totalQueueCount);
    const [totalHistoryCount, setTotalHistoryCount] = useState(props.loaderData.totalHistoryCount);
    const [uploadingFiles, setUploadingFiles] = useState<UploadingFile[]>([]);
    const uploadQueueRef = useRef<UploadingFile[]>([]);
    const manualCategoryRef = useRef<string>(props.loaderData.manualCategory);
    const isUploadingRef = useRef(false);
    const [, setSearchParams] = useSearchParams();

    useEffect(() => { setQueueSlots(props.loaderData.queueSlots); }, [props.loaderData.queueSlots]);
    useEffect(() => { setHistorySlots(props.loaderData.historySlots); }, [props.loaderData.historySlots]);
    useEffect(() => { setTotalQueueCount(props.loaderData.totalQueueCount); }, [props.loaderData.totalQueueCount]);
    useEffect(() => { setTotalHistoryCount(props.loaderData.totalHistoryCount); }, [props.loaderData.totalHistoryCount]);

    const queueTotalPages = Math.max(1, Math.ceil(totalQueueCount / queuePageSize));
    const historyTotalPages = Math.max(1, Math.ceil(totalHistoryCount / historyPageSize));
    const isQueueLive = queuePage === 1;
    const isHistoryLive = historyPage === 1;

    const setPageParam = useCallback((key: string, page: number) => {
        setSearchParams(prev => {
            const next = new URLSearchParams(prev);
            next.set(key, String(page));
            return next;
        }, { preventScrollReset: true });
    }, [setSearchParams]);
    const onQueuePageSelected = useCallback((page: number) => setPageParam("qp", page), [setPageParam]);
    const onHistoryPageSelected = useCallback((page: number) => setPageParam("hp", page), [setPageParam]);

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

    const combinedQueueSlots = isQueueLive
        ? [...uploadingFiles.map(file => file.queueSlot), ...queueSlots]
        : queueSlots;

    // queue/history events
    const queueEvents = useQueueEvents(setUploadingFiles, setQueueSlots, uploadQueueRef, queuePageSize, isQueueLive);
    const historyEvents = useHistoryEvents(setHistorySlots, historyPageSize);

    // websocket
    useQueueHistoryWebsocket(
        queueEvents,
        historyEvents,
        isQueueLive,
        isHistoryLive,
        setTotalQueueCount,
        setTotalHistoryCount,
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
                <div className="relative" {...dropzone.getRootProps()}>
                    {dropzone.isDragActive && <div className="pointer-events-none absolute inset-0 z-20 flex items-center justify-center rounded border-2 border-dashed border-primary bg-primary/10" />}
                    <input {...dropzone.getInputProps()} />
                    <QueueTable
                        queueSlots={combinedQueueSlots}
                        totalQueueCount={totalQueueCount + uploadingFiles.length}
                        pageNumber={queuePage}
                        pageSize={queuePageSize}
                        pageSizeOptions={PAGE_SIZE_OPTIONS}
                        totalPages={queueTotalPages}
                        isLive={isQueueLive}
                        onPageSelected={onQueuePageSelected}
                        onPageSizeSelected={onQueuePageSizeSelected}
                        categories={props.loaderData.categories}
                        manualCategoryRef={manualCategoryRef}
                        onIsSelectedChanged={queueEvents.onSelectQueueSlots}
                        onIsRemovingChanged={queueEvents.onRemovingQueueSlots}
                        onRemoved={queueEvents.onRemoveQueueSlots}
                        onMovedToTop={queueEvents.onMoveQueueSlotsToTop}
                        onUploadClicked={dropzone.open}
                    />
                </div>
            </div>

            {/* history */}
            {(totalHistoryCount > 0 || historySlots.length > 0) &&
                <HistoryTable
                    historySlots={historySlots}
                    totalHistoryCount={totalHistoryCount}
                    pageNumber={historyPage}
                    pageSize={historyPageSize}
                    pageSizeOptions={PAGE_SIZE_OPTIONS}
                    totalPages={historyTotalPages}
                    isLive={isHistoryLive}
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
