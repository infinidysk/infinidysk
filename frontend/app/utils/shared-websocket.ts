import { useEffect, useRef } from "react";
import { URL_BASE } from "./url-base";
import { receiveMessage } from "~/utils/websocket-util";

export type TopicKind = "state" | "stream" | "event";

type MessageListener = (topic: string, message: string) => void;

type Subscriber = {
    topics: Record<string, TopicKind>;
    onMessage: MessageListener;
    onOpen?: (() => void) | undefined;
    onClose?: ((event: CloseEvent) => void) | undefined;
};

const RECONNECT_MS = 1000;

function websocketUrl(): string {
    return `${globalThis.location.origin.replace(/^http/, "ws")}${URL_BASE}/ws`;
}

/**
 * One multiplexed WebSocket per browser tab. Components subscribe by topic;
 * the hub already fans out by topic — this client merges subscription maps
 * and shares a single connection.
 */
class SharedWebsocketClient {
    private ws: WebSocket | null = null;
    private subscribers = new Set<Subscriber>();
    private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    private intentionalClose = false;

    subscribe(subscriber: Subscriber): () => void {
        this.subscribers.add(subscriber);
        this.ensureConnected();
        if (this.ws?.readyState === WebSocket.OPEN) {
            this.sendMergedSubscriptions();
            subscriber.onOpen?.();
        }
        return () => {
            this.subscribers.delete(subscriber);
            if (this.subscribers.size === 0) {
                this.teardown();
            } else if (this.ws?.readyState === WebSocket.OPEN) {
                this.sendMergedSubscriptions();
            }
        };
    }

    private mergedTopics(): Record<string, TopicKind> {
        const merged: Record<string, TopicKind> = {};
        for (const subscriber of this.subscribers) {
            for (const [topic, kind] of Object.entries(subscriber.topics)) {
                // Prefer "state" so late subscribers still get lastMessage replay.
                if (merged[topic] === "state") continue;
                merged[topic] = kind === "state" ? "state" : (merged[topic] ?? kind);
            }
        }
        return merged;
    }

    private sendMergedSubscriptions() {
        if (!this.ws || this.ws.readyState !== WebSocket.OPEN) return;
        this.ws.send(JSON.stringify(this.mergedTopics()));
    }

    private ensureConnected() {
        if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) {
            return;
        }
        this.intentionalClose = false;
        const ws = new WebSocket(websocketUrl());
        this.ws = ws;

        ws.onopen = () => {
            this.sendMergedSubscriptions();
            for (const subscriber of this.subscribers) {
                subscriber.onOpen?.();
            }
        };

        ws.onmessage = receiveMessage((topic, message) => {
            for (const subscriber of this.subscribers) {
                if (topic in subscriber.topics) {
                    subscriber.onMessage(topic, message);
                }
            }
        });

        ws.onerror = () => {
            ws.close();
        };

        ws.onclose = (event) => {
            if (this.ws === ws) this.ws = null;
            for (const subscriber of this.subscribers) {
                subscriber.onClose?.(event);
            }
            if (event.code === 1008) {
                globalThis.location.assign("/login");
                return;
            }
            if (!this.intentionalClose && this.subscribers.size > 0) {
                this.reconnectTimer = setTimeout(() => this.ensureConnected(), RECONNECT_MS);
            }
        };
    }

    private teardown() {
        this.intentionalClose = true;
        if (this.reconnectTimer) {
            clearTimeout(this.reconnectTimer);
            this.reconnectTimer = null;
        }
        this.ws?.close();
        this.ws = null;
    }
}

const sharedClient = new SharedWebsocketClient();

export type UseWebsocketTopicsOptions = {
    enabled?: boolean;
    onOpen?: () => void;
    onClose?: (event: CloseEvent) => void;
};

/** Subscribe to one or more topics on the shared per-tab WebSocket. */
export function useWebsocketTopics(
    topics: Record<string, TopicKind>,
    onMessage: MessageListener,
    options: UseWebsocketTopicsOptions = {},
) {
    const { enabled = true, onOpen, onClose } = options;
    const onMessageRef = useRef(onMessage);
    const onOpenRef = useRef(onOpen);
    const onCloseRef = useRef(onClose);
    onMessageRef.current = onMessage;
    onOpenRef.current = onOpen;
    onCloseRef.current = onClose;

    const topicsKey = JSON.stringify(topics);

    useEffect(() => {
        if (!enabled) return;

        const parsedTopics = JSON.parse(topicsKey) as Record<string, TopicKind>;
        return sharedClient.subscribe({
            topics: parsedTopics,
            onMessage: (topic, message) => onMessageRef.current(topic, message),
            onOpen: () => onOpenRef.current?.(),
            onClose: (event) => onCloseRef.current?.(event),
        });
    }, [enabled, topicsKey]);
}

/** Convenience for a single topic. */
export function useWebsocketTopic(
    topic: string,
    kind: TopicKind,
    onMessage: (message: string) => void,
    options: UseWebsocketTopicsOptions = {},
) {
    useWebsocketTopics(
        { [topic]: kind },
        (_topic, message) => onMessage(message),
        options,
    );
}

/** Imperative subscribe for non-hook contexts (e.g. one-shot benchmark progress). */
export function subscribeWebsocketTopics(
    topics: Record<string, TopicKind>,
    onMessage: MessageListener,
    options: Omit<UseWebsocketTopicsOptions, "enabled"> = {},
): () => void {
    return sharedClient.subscribe({
        topics,
        onMessage,
        onOpen: options.onOpen,
        onClose: options.onClose,
    });
}
