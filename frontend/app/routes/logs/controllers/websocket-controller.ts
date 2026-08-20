import { useWebsocketTopics } from "~/utils/shared-websocket";
import type { LogBroadcastMessage, LogEntry } from "~/clients/backend-client.server";

const LOG_TOPIC = "log";
const topicSubscriptions = { [LOG_TOPIC]: "event" } as const;

export type ConnectionStatus = "connecting" | "live" | "reconnecting" | "disconnected";

export function useLogsWebsocket(
  onBatch: (entries: LogEntry[]) => void,
  onStatus: (status: ConnectionStatus) => void,
) {
  useWebsocketTopics(
    topicSubscriptions,
    (topic, message) => {
      if (topic !== LOG_TOPIC) return;
      try {
        const parsed = JSON.parse(message) as LogBroadcastMessage;
        if (parsed?.entries?.length) onBatch(parsed.entries);
      } catch {
        // ignore malformed payloads
      }
    },
    {
      onOpen: () => onStatus("live"),
      onClose: () => onStatus("reconnecting"),
    },
  );
}
