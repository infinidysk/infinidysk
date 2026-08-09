import { useCallback } from "react";
import type { HistoryEvents, QueueEvents } from "./events-controller";
import { adjustTotalCount } from "./events-controller";
import type { HistorySlot, QueueSlot } from "~/clients/backend-client.server";
import { useWebsocketTopics } from "~/utils/shared-websocket";

const topicNames = {
    queueItemStatus: 'qs',
    queueItemPercentage: 'qp',
    queueItemProviders: 'qpv',
    queueItemAdded: 'qa',
    queueItemRemoved: 'qr',
    queueItemMoved: 'qm',
    historyItemAdded: 'ha',
    historyItemRemoved: 'hr',
};

const topicSubscriptions = {
    [topicNames.queueItemStatus]: 'state',
    [topicNames.queueItemPercentage]: 'state',
    [topicNames.queueItemProviders]: 'state',
    [topicNames.queueItemAdded]: 'event',
    [topicNames.queueItemRemoved]: 'event',
    [topicNames.queueItemMoved]: 'event',
    [topicNames.historyItemAdded]: 'event',
    [topicNames.historyItemRemoved]: 'event',
} as const;

export function useQueueHistoryWebsocket(
    queueEvents: QueueEvents,
    historyEvents: HistoryEvents,
    isQueueLive: boolean,
    isHistoryLive: boolean,
    setTotalQueueCount: (value: React.SetStateAction<number>) => void,
    setTotalHistoryCount: (value: React.SetStateAction<number>) => void,
) {
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic == topicNames.queueItemAdded) {
            // Totals always; slot window only on live page 1.
            // Count updates live here (not in UI handlers) so optimistic UI remove + qr
            // do not double-decrement.
            setTotalQueueCount(count => adjustTotalCount(count, 1));
            // 'qa' websocket payload carries a JSON-serialized QueueSlot (backend contract)
            if (isQueueLive) queueEvents.onAddQueueSlot(JSON.parse(message) as QueueSlot);
        }
        else if (topic == topicNames.queueItemRemoved) {
            const ids = message.split(',').filter(Boolean);
            setTotalQueueCount(count => adjustTotalCount(count, -ids.length));
            if (isQueueLive) queueEvents.onRemoveQueueSlots(new Set<string>(ids));
        }
        else if (topic == topicNames.queueItemMoved) {
            queueEvents.onMoveQueueSlotsToTop(new Set<string>(message.split(',').filter(Boolean)));
        }
        else if (topic == topicNames.queueItemStatus)
            queueEvents.onChangeQueueSlotStatus(message);
        else if (topic == topicNames.queueItemPercentage)
            queueEvents.onChangeQueueSlotPercentage(message);
        else if (topic == topicNames.queueItemProviders)
            queueEvents.onChangeQueueSlotProviders(message);
        else if (topic == topicNames.historyItemAdded) {
            setTotalHistoryCount(count => adjustTotalCount(count, 1));
            // 'ha' websocket payload carries a JSON-serialized HistorySlot (backend contract)
            if (isHistoryLive) historyEvents.onAddHistorySlot(JSON.parse(message) as HistorySlot);
        }
        else if (topic == topicNames.historyItemRemoved) {
            const ids = message.split(',').filter(Boolean);
            setTotalHistoryCount(count => adjustTotalCount(count, -ids.length));
            if (isHistoryLive) historyEvents.onRemoveHistorySlots(new Set<string>(ids));
        }
    }, [
        queueEvents,
        historyEvents,
        isQueueLive,
        isHistoryLive,
        setTotalQueueCount,
        setTotalHistoryCount,
    ]);

    useWebsocketTopics(topicSubscriptions, onWebsocketMessage);
}
