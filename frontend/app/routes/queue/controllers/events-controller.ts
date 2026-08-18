import { useCallback, useMemo } from "react";
import type { HistorySlot, ProviderUsage, QueueSlot } from "~/clients/backend-client.server";
import type { PresentationHistorySlot, PresentationQueueSlot, UploadingFile } from "../route";

export type QueueEvents = {
    onAddQueueSlot: (queueSlot: QueueSlot) => void,
    onSelectQueueSlots: (ids: Set<string>, isSelected: boolean) => void,
    onRemovingQueueSlots: (ids: Set<string>, isRemoving: boolean) => void,
    onRemoveQueueSlots: (ids: Set<string>) => void,
    onMoveQueueSlotsToTop: (ids: Set<string>) => void,
    onChangeQueueSlotStatus: (message: string) => void,
    onChangeQueueSlotPercentage: (message: string) => void,
    onChangeQueueSlotProviders: (message: string) => void
};

export type HistoryEvents = {
    onAddHistorySlot: (historySlot: HistorySlot) => void,
    onSelectHistorySlots: (ids: Set<string>, isSelected: boolean) => void,
    onRemovingHistorySlots: (ids: Set<string>, isRemoving: boolean) => void,
    onRemoveHistorySlots: (ids: Set<string>) => void
};

/** Apply a delta to a live total, never going below zero. */
export function adjustTotalCount(current: number, delta: number): number {
    return Math.max(0, current + delta);
}

/** Parse the compact `qpv` websocket payload (`host=segments,...`). */
export function parseQueueProvidersPayload(payload: string): ProviderUsage[] {
    if (!payload) return [];
    return payload
        .split(',')
        .map(part => {
            const eq = part.indexOf('=');
            const host = eq < 0 ? part : part.slice(0, eq);
            const segments = eq < 0 ? 0 : Number(part.slice(eq + 1));
            return { host, segments: Number.isFinite(segments) ? segments : 0 };
        })
        .sort((a, b) => b.segments - a.segments);
}

export function parseQueueProvidersMessage(message: string): { nzo_id: string, payload: string } | null {
    const sep = message.indexOf('|');
    if (sep < 0) return null;
    return { nzo_id: message.slice(0, sep), payload: message.slice(sep + 1) };
}

export function preserveProviderNicknames(
    providers: ProviderUsage[],
    previousProviders?: ProviderUsage[] | null,
): ProviderUsage[] {
    const nicknamesByHost = new Map<string, string>();
    for (const provider of previousProviders ?? []) {
        const nickname = provider.nickname?.trim();
        if (!nickname) continue;
        nicknamesByHost.set(provider.host.toLowerCase(), nickname);
    }
    return providers.map(provider => {
        const nickname = nicknamesByHost.get(provider.host.toLowerCase());
        return nickname ? { ...provider, nickname } : provider;
    });
}

/** Apply a `qpv` message to a slot, preserving nicknames from the previous provider list. */
export function applyQueueProvidersMessage(
    message: string,
    previousProviders?: ProviderUsage[] | null,
): { nzo_id: string, providers: ProviderUsage[] } | null {
    const parsed = parseQueueProvidersMessage(message);
    if (!parsed) return null;
    return {
        nzo_id: parsed.nzo_id,
        providers: preserveProviderNicknames(
            parseQueueProvidersPayload(parsed.payload),
            previousProviders,
        ),
    };
}

export function useQueueEvents(
    setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void,
    setQueueSlots: (value: React.SetStateAction<PresentationQueueSlot[]>) => void,
    uploadQueueRef: React.RefObject<UploadingFile[]>,
    pageSize: number,
    isQueueLive: boolean,
) {
    const onAddQueueSlot = useCallback((queueSlot: QueueSlot) => {
        uploadQueueRef.current = uploadQueueRef.current.filter(x => x.queueSlot.status === "uploading" || x.queueSlot.filename !== queueSlot.filename);
        setUploadingFiles(files => files.filter(f => f.queueSlot.filename !== queueSlot.filename));
        setQueueSlots(slots => slots.length >= pageSize ? slots : [...slots, queueSlot]);
    }, [setQueueSlots, setUploadingFiles, uploadQueueRef, pageSize]);

    const onSelectQueueSlots = useCallback((ids: Set<string>, isSelected: boolean) => {
        setUploadingFiles(files => files.map(x => ids.has(x.queueSlot.nzo_id) ? { ...x, queueSlot: { ...x.queueSlot, isSelected } } : x));
        setQueueSlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isSelected } : x));
    }, [setQueueSlots, setUploadingFiles]);

    const onRemovingQueueSlots = useCallback((ids: Set<string>, isRemoving: boolean) => {
        setQueueSlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isRemoving } : x));
    }, [setQueueSlots]);

    const onRemoveQueueSlots = useCallback((ids: Set<string>) => {
        uploadQueueRef.current = uploadQueueRef.current.filter(x => x.queueSlot.status === "uploading" || !ids.has(x.queueSlot.nzo_id));
        setUploadingFiles(files => files.filter(x => x.queueSlot.status === "uploading" || !ids.has(x.queueSlot.nzo_id)));
        setQueueSlots(slots => slots.filter(x => !ids.has(x.nzo_id)));
    }, [setQueueSlots, setUploadingFiles, uploadQueueRef]);

    const onMoveQueueSlotsToTop = useCallback((ids: Set<string>) => {
        if (ids.size === 0) return;

        // Older pages only show a window of the queue; moved items leave that window.
        if (!isQueueLive) {
            setQueueSlots(slots => slots.filter(x => !ids.has(x.nzo_id)));
            return;
        }

        setQueueSlots(slots => {
            const moved: PresentationQueueSlot[] = [];
            const remaining: PresentationQueueSlot[] = [];
            for (const slot of slots) {
                if (ids.has(slot.nzo_id)) moved.push({ ...slot, isSelected: false });
                else remaining.push(slot);
            }
            if (moved.length === 0) return slots;

            // Keep every in-progress download pinned at the front (matches GetQueueController).
            const insertAt = remaining.findIndex(s => s.status !== "Downloading");
            const index = insertAt < 0 ? remaining.length : insertAt;
            return [
                ...remaining.slice(0, index),
                ...moved,
                ...remaining.slice(index),
            ].slice(0, pageSize);
        });
    }, [setQueueSlots, isQueueLive, pageSize]);

    const onChangeQueueSlotStatus = useCallback((message: string) => {
        const [nzo_id = "", status = ""] = message.split('|');
        setQueueSlots(slots => slots.map(x => x.nzo_id === nzo_id ? { ...x, status } : x));
    }, [setQueueSlots]);

    const onChangeQueueSlotPercentage = useCallback((message: string) => {
        const [nzo_id = "", true_percentage = ""] = message.split('|');
        setQueueSlots(slots => slots.map(x => x.nzo_id === nzo_id ? { ...x, true_percentage } : x));
    }, [setQueueSlots]);

    const onChangeQueueSlotProviders = useCallback((message: string) => {
        const parsed = parseQueueProvidersMessage(message);
        if (!parsed) return;
        const providers = parseQueueProvidersPayload(parsed.payload);
        setQueueSlots(slots => slots.map(slot => {
            if (slot.nzo_id !== parsed.nzo_id) return slot;
            return {
                ...slot,
                providers: preserveProviderNicknames(providers, slot.providers),
            };
        }));
    }, [setQueueSlots]);

    return useStableEventHandlers({
        onAddQueueSlot,
        onSelectQueueSlots,
        onRemovingQueueSlots,
        onRemoveQueueSlots,
        onMoveQueueSlotsToTop,
        onChangeQueueSlotStatus,
        onChangeQueueSlotPercentage,
        onChangeQueueSlotProviders
    });
}

export function useHistoryEvents(
    setHistorySlots: (value: React.SetStateAction<PresentationHistorySlot[]>) => void,
    pageSize: number,
) {
    const onAddHistorySlot = useCallback((historySlot: HistorySlot) => {
        setHistorySlots(slots => [historySlot, ...slots].slice(0, pageSize));
    }, [setHistorySlots, pageSize]);

    const onSelectHistorySlots = useCallback((ids: Set<string>, isSelected: boolean) => {
        setHistorySlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isSelected } : x));
    }, [setHistorySlots]);

    const onRemovingHistorySlots = useCallback((ids: Set<string>, isRemoving: boolean) => {
        setHistorySlots(slots => slots.map(x => ids.has(x.nzo_id) ? { ...x, isRemoving } : x));
    }, [setHistorySlots]);

    const onRemoveHistorySlots = useCallback((ids: Set<string>) => {
        setHistorySlots(slots => slots.filter(x => !ids.has(x.nzo_id)));
    }, [setHistorySlots]);

    return useStableEventHandlers({
        onAddHistorySlot,
        onSelectHistorySlots,
        onRemovingHistorySlots,
        onRemoveHistorySlots
    });
}

function useStableEventHandlers<T extends Record<string, unknown>>(object: T): T {
    // eslint-disable-next-line react-hooks/exhaustive-deps
    return useMemo(() => object, Object.values(object));
}
