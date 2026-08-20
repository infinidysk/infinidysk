import { useCallback, useEffect, useRef } from "react";
import type { HistoryEvents, QueueEvents } from "./events-controller";
import { adjustTotalCount } from "./events-controller";
import type { HistorySlot, QueueSlot } from "~/clients/backend-client.server";
import { useWebsocketTopics } from "~/utils/shared-websocket";

const topicNames = {
  queueItemStatus: "qs",
  queueItemPercentage: "qp",
  queueItemProviders: "qpv",
  queueItemAdded: "qa",
  queueItemRemoved: "qr",
  queueItemMoved: "qm",
  queueOrderChanged: "qo",
  historyItemAdded: "ha",
  historyItemRemoved: "hr",
};

const topicSubscriptions = {
  [topicNames.queueItemStatus]: "state",
  [topicNames.queueItemPercentage]: "state",
  [topicNames.queueItemProviders]: "state",
  [topicNames.queueItemAdded]: "event",
  [topicNames.queueItemRemoved]: "event",
  [topicNames.queueItemMoved]: "event",
  [topicNames.queueOrderChanged]: "event",
  [topicNames.historyItemAdded]: "event",
  [topicNames.historyItemRemoved]: "event",
} as const;

export function useQueueHistoryWebsocket(
  queueEvents: QueueEvents,
  historyEvents: HistoryEvents,
  isQueueLive: boolean,
  isHistoryLive: boolean,
  setTotalQueueCount: (value: React.SetStateAction<number>) => void,
  setTotalHistoryCount: (value: React.SetStateAction<number>) => void,
  onDeferredRefresh: () => void,
) {
  const refreshTimer = useRef<number | undefined>(undefined);
  const scheduleRefresh = useCallback(() => {
    if (refreshTimer.current !== undefined) window.clearTimeout(refreshTimer.current);
    refreshTimer.current = window.setTimeout(() => {
      refreshTimer.current = undefined;
      onDeferredRefresh();
    }, 350);
  }, [onDeferredRefresh]);
  useEffect(
    () => () => {
      if (refreshTimer.current !== undefined) window.clearTimeout(refreshTimer.current);
    },
    [],
  );

  const onWebsocketMessage = useCallback(
    (topic: string, message: string) => {
      if (topic == topicNames.queueItemAdded) {
        // The immediate page-1 view updates its count and slot window locally.
        // Other views refresh from the server so their filtered count stays correct.
        if (isQueueLive) setTotalQueueCount((count) => adjustTotalCount(count, 1));
        // 'qa' websocket payload carries a JSON-serialized QueueSlot (backend contract)
        if (isQueueLive) queueEvents.onAddQueueSlot(JSON.parse(message) as QueueSlot);
        scheduleRefresh();
      } else if (topic == topicNames.queueItemRemoved) {
        const ids = message.split(",").filter(Boolean);
        if (isQueueLive) setTotalQueueCount((count) => adjustTotalCount(count, -ids.length));
        if (isQueueLive) queueEvents.onRemoveQueueSlots(new Set<string>(ids));
        scheduleRefresh();
      } else if (topic == topicNames.queueItemMoved) {
        queueEvents.onMoveQueueSlotsToTop(new Set<string>(message.split(",").filter(Boolean)));
        scheduleRefresh();
      } else if (topic == topicNames.queueItemStatus) {
        queueEvents.onChangeQueueSlotStatus(message);
        scheduleRefresh();
      } else if (topic == topicNames.queueOrderChanged) scheduleRefresh();
      else if (topic == topicNames.queueItemPercentage)
        queueEvents.onChangeQueueSlotPercentage(message);
      else if (topic == topicNames.queueItemProviders)
        queueEvents.onChangeQueueSlotProviders(message);
      else if (topic == topicNames.historyItemAdded) {
        if (isHistoryLive) setTotalHistoryCount((count) => adjustTotalCount(count, 1));
        // 'ha' websocket payload carries a JSON-serialized HistorySlot (backend contract)
        if (isHistoryLive) historyEvents.onAddHistorySlot(JSON.parse(message) as HistorySlot);
        else scheduleRefresh();
      } else if (topic == topicNames.historyItemRemoved) {
        const ids = message.split(",").filter(Boolean);
        if (isHistoryLive) setTotalHistoryCount((count) => adjustTotalCount(count, -ids.length));
        if (isHistoryLive) historyEvents.onRemoveHistorySlots(new Set<string>(ids));
        else scheduleRefresh();
      }
    },
    [
      queueEvents,
      historyEvents,
      isQueueLive,
      isHistoryLive,
      setTotalQueueCount,
      setTotalHistoryCount,
      scheduleRefresh,
    ],
  );

  useWebsocketTopics(topicSubscriptions, onWebsocketMessage, { onOpen: scheduleRefresh });
}
