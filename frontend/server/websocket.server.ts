import WebSocket, { WebSocketServer } from 'ws';
import { Buffer } from "node:buffer";
import { isAuthenticated } from "../app/auth/authentication.server";
import type { IncomingMessage } from 'http';
import { logger } from "./logger";

// ws types MessageEvent.data as any; decode explicitly.
function messageEventDataToString(data: unknown): string {
    if (typeof data === "string") return data;
    if (data instanceof Buffer) return data.toString("utf8");
    if (data instanceof ArrayBuffer) return Buffer.from(data).toString("utf8");
    if (Array.isArray(data)) return Buffer.concat(data as Buffer[]).toString("utf8");
    return String(data);
}

export const MAX_WEBSOCKET_PAYLOAD_BYTES = 64 * 1024;
export const MAX_TOPICS_PER_SOCKET = 100;
export const WEBSOCKET_HEARTBEAT_INTERVAL_MS = 30_000;
export const MAX_CLIENT_BUFFERED_AMOUNT = 1024 * 1024;
export const WEBSOCKET_BROWSER_PATH = "/ws";

export const BACKEND_RECONNECT_INITIAL_MS = 1_000;
export const BACKEND_RECONNECT_MAX_MS = 30_000;

export type TopicKind = "state" | "stream" | "event";

const TOPIC_KINDS = new Set<TopicKind>(["state", "stream", "event"]);

type TrackedSocket = WebSocket & { isAlive?: boolean };

/** Validate browser subscription payloads: flat Record<string, TopicKind>. */
export function parseSubscriptionTopics(raw: string): Record<string, TopicKind> | null {
    let parsed: unknown;
    try {
        parsed = JSON.parse(raw);
    } catch {
        return null;
    }
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null;

    const topics: Record<string, TopicKind> = {};
    for (const [topic, kind] of Object.entries(parsed as Record<string, unknown>)) {
        if (typeof topic !== "string" || topic.length === 0) return null;
        if (typeof kind !== "string" || !TOPIC_KINDS.has(kind as TopicKind)) return null;
        topics[topic] = kind as TopicKind;
    }
    if (Object.keys(topics).length > MAX_TOPICS_PER_SOCKET) return null;
    return topics;
}

export function sendToBrowserClient(client: WebSocket, rawMessage: string): void {
    if (client.readyState !== WebSocket.OPEN) return;
    if (client.bufferedAmount > MAX_CLIENT_BUFFERED_AMOUNT) return;
    client.send(rawMessage);
}

/**
 * Exponential backoff with full jitter for the frontend→backend relay.
 * Attempt 0 → ~1s, then doubles up to BACKEND_RECONNECT_MAX_MS.
 */
export function nextBackendReconnectDelayMs(
    attempt: number,
    random: () => number = Math.random,
): number {
    const exp = Math.min(
        BACKEND_RECONNECT_MAX_MS,
        BACKEND_RECONNECT_INITIAL_MS * 2 ** Math.max(0, attempt),
    );
    return Math.floor(random() * (exp + 1));
}

function initializeWebsocketServer(wss: WebSocketServer) {
    // keep track of socket subscriptions
    const websockets = new Map<TrackedSocket, Record<string, TopicKind>>();
    const subscriptions = new Map<string, Set<TrackedSocket>>();
    const lastMessage = new Map<string, string>();

    // Tracks which topics have at least one browser subscriber, and forwards
    // aggregate changes upstream to the backend so it can skip serialization
    // for topics with zero listeners.
    const upstreamSubscriptions = new UpstreamSubscriptionForwarder(subscriptions);
    initializeWebsocketClient(subscriptions, lastMessage, upstreamSubscriptions);

    const heartbeat = setInterval(() => {
        for (const client of wss.clients) {
            const tracked = client as TrackedSocket;
            if (tracked.isAlive === false) {
                tracked.terminate();
                continue;
            }
            tracked.isAlive = false;
            tracked.ping();
        }
    }, WEBSOCKET_HEARTBEAT_INTERVAL_MS);
    heartbeat.unref?.();
    wss.on("close", () => clearInterval(heartbeat));

    // authenticate new websocket sessions
    wss.on("connection", (ws: TrackedSocket, request: IncomingMessage) => {
        // Buffer early frames and attach handlers before awaiting auth so the
        // browser's immediate onopen subscription is not dropped.
        let authenticated = false;
        let closed = false;
        const remote = request.socket.remoteAddress ?? "unknown IP";
        const pendingMessages: WebSocket.MessageEvent[] = [];
        ws.isAlive = true;
        ws.on("pong", () => {
            ws.isAlive = true;
        });

        const applySubscription = (event: WebSocket.MessageEvent) => {
            const topics = parseSubscriptionTopics(messageEventDataToString(event.data));
            if (!topics) {
                ws.close(1003, "Could not process topic subscription. If recently updated, try refreshing the page.");
                return;
            }

            const previous = websockets.get(ws);
            if (previous) {
                for (const topic of Object.keys(previous)) {
                    subscriptions.get(topic)?.delete(ws);
                }
            }

            websockets.set(ws, topics);
            for (const topic of Object.keys(topics)) {
                const topicSubscriptions = subscriptions.get(topic);
                if (topicSubscriptions) topicSubscriptions.add(ws);
                else subscriptions.set(topic, new Set<TrackedSocket>([ws]));
                if (topics[topic] === 'state') {
                    const messageToSend = lastMessage.get(topic);
                    if (messageToSend) sendToBrowserClient(ws, messageToSend);
                }
            }

            upstreamSubscriptions.syncAfterBrowserChange();
        };

        ws.onmessage = (event: WebSocket.MessageEvent) => {
            if (!authenticated) {
                pendingMessages.push(event);
                return;
            }
            applySubscription(event);
        };

        ws.onclose = (event: WebSocket.CloseEvent) => {
            closed = true;
            pendingMessages.length = 0;
            const topics = websockets.get(ws);
            if (topics) {
                websockets.delete(ws);
                for (const topic of Object.keys(topics)) {
                    const topicSubscriptions = subscriptions.get(topic);
                    if (topicSubscriptions) topicSubscriptions.delete(ws);
                }
                upstreamSubscriptions.syncAfterBrowserChange();
            }
            if (authenticated) {
                logger.info(
                    `Browser websocket closed from ${remote} (code ${event.code}, reason: ${event.reason || "none"})`,
                );
            }
        };

        void (async () => {
            try {
                if (!await isAuthenticated(request)) {
                    logger.warn(`Rejected unauthenticated websocket connection from ${remote}`);
                    ws.close(1008, "Unauthorized");
                    return;
                }
                if (closed) return;
                authenticated = true;
                logger.info(`Browser websocket connected from ${remote}`);
                for (const event of pendingMessages.splice(0)) {
                    applySubscription(event);
                }
            } catch (error) {
                logger.error("Error authenticating websocket session", error);
                ws.close(1011, "Internal server error");
            }
        })();
    });
}

export function initializeWebsocketClient(
    subscriptions: Map<string, Set<WebSocket>>,
    lastMessage: Map<string, string>,
    upstreamForwarder?: UpstreamSubscriptionForwarder,
) {
    let reconnectTimeout: NodeJS.Timeout | null = null;
    let connected = false;
    let connectionFailures = 0;
    let lastFailureLogAt = 0;
    let loggedStartupWait = false;
    const startedAt = Date.now();
    const startupGraceMs = 30_000;
    const url = getBackendWebsocketUrl();

    function logConnectionFailure(message: string, retryDelayMs: number, error?: unknown) {
        const now = Date.now();
        connectionFailures += 1;

        // During the first ~30s after frontend start (e.g. Docker starts the
        // frontend before --db-migration binds the backend port), connection
        // refusals are expected. Log once at info without the error stack.
        if (now - startedAt < startupGraceMs) {
            if (!loggedStartupWait) {
                logger.info("Waiting for backend to start...");
                loggedStartupWait = true;
                lastFailureLogAt = now;
            }
            return;
        }

        if (connectionFailures === 1 || now - lastFailureLogAt >= 60_000) {
            logger.warn(`${message}; retrying in ${retryDelayMs} ms`, error);
            lastFailureLogAt = now;
        }
    }

    function connect() {
        const socket = new WebSocket(url);

        socket.on('error', (error: Error) => {
            // Failed-connect errors are logged from onclose to avoid double-counting.
            if (connected) {
                logger.warn("Backend websocket error", error);
            }
        });

        socket.onopen = () => {
            const reconnected = connectionFailures > 0;
            connected = true;
            connectionFailures = 0;
            lastFailureLogAt = 0;
            logger.info(reconnected ? "Backend websocket reconnected" : "Backend websocket connected");
            if (reconnectTimeout) {
                clearTimeout(reconnectTimeout);
                reconnectTimeout = null;
            }

            socket.send(Buffer.from(process.env.FRONTEND_BACKEND_API_KEY!, "utf-8"), { binary: false });

            if (upstreamForwarder) {
                upstreamForwarder.setBackendSocket(socket);
                upstreamForwarder.sendFullSubscriptionSet();
            }
        };

        socket.onmessage = (event: WebSocket.MessageEvent) => {
            try {
                const rawMessage = messageEventDataToString(event.data);
                const topicMessage: unknown = JSON.parse(rawMessage);
                if (!topicMessage || typeof topicMessage !== "object") return;

                const { Topic: topic, Message: message } = topicMessage as Record<string, unknown>;
                if (typeof topic !== "string" || typeof message !== "string") return;

                lastMessage.set(topic, rawMessage);
                const subscribed = subscriptions.get(topic) || [];
                subscribed.forEach(client => {
                    sendToBrowserClient(client, rawMessage);
                });
            } catch (error) {
                logger.error("Ignoring malformed backend websocket message", error);
            }
        };

        socket.onclose = (event: WebSocket.CloseEvent) => {
            // Keep browser sockets open and preserve lastMessage. Overview uses
            // live-stats age for the soft stale banner; when the relay returns,
            // the backend replays state topics and fan-out resumes without a
            // mass browser reconnect (see #515).
            const wasConnected = connected;
            connected = false;
            if (upstreamForwarder) upstreamForwarder.setBackendSocket(null);
            const retryDelayMs = nextBackendReconnectDelayMs(connectionFailures);
            if (wasConnected) {
                logConnectionFailure(
                    `Backend websocket closed (code ${event.code}, reason: ${event.reason || "none"})`,
                    retryDelayMs,
                );
            } else {
                logConnectionFailure(`Could not connect to backend websocket at ${url}`, retryDelayMs);
            }
            scheduleReconnect(retryDelayMs);
        };
    }

    function scheduleReconnect(delayMs: number) {
        if (reconnectTimeout) clearTimeout(reconnectTimeout);

        reconnectTimeout = setTimeout(() => {
            connect();
        }, delayMs);
    }

    connect();
}

/**
 * Forwards the aggregate set of browser-subscribed topics upstream to the backend
 * so the backend can skip serialization for topics nobody is listening to. Only sends
 * a diff when the set of topics with >0 subscribers actually changes.
 */
export class UpstreamSubscriptionForwarder {
    private _backendSocket: WebSocket | null = null;
    private _lastSentTopics = new Set<string>();
    private readonly _subscriptions: Map<string, Set<WebSocket>>;

    constructor(subscriptions: Map<string, Set<WebSocket>>) {
        this._subscriptions = subscriptions;
    }

    setBackendSocket(socket: WebSocket | null): void {
        this._backendSocket = socket;
        if (!socket) this._lastSentTopics.clear();
    }

    /** Called after any browser subscribe/unsubscribe to diff and forward changes. */
    syncAfterBrowserChange(): void {
        if (!this._backendSocket || this._backendSocket.readyState !== WebSocket.OPEN) return;

        const currentActive = this._getActiveTopics();
        const toSub: string[] = [];
        const toUnsub: string[] = [];

        for (const topic of currentActive) {
            if (!this._lastSentTopics.has(topic)) toSub.push(topic);
        }
        for (const topic of this._lastSentTopics) {
            if (!currentActive.has(topic)) toUnsub.push(topic);
        }

        if (toSub.length > 0) {
            this._backendSocket.send(JSON.stringify({ sub: toSub }));
        }
        if (toUnsub.length > 0) {
            this._backendSocket.send(JSON.stringify({ unsub: toUnsub }));
        }

        this._lastSentTopics = currentActive;
    }

    /** Resend the full subscription set on reconnect. */
    sendFullSubscriptionSet(): void {
        if (!this._backendSocket || this._backendSocket.readyState !== WebSocket.OPEN) return;

        const active = this._getActiveTopics();
        if (active.size > 0) {
            this._backendSocket.send(JSON.stringify({ sub: [...active] }));
        }
        this._lastSentTopics = active;
    }

    private _getActiveTopics(): Set<string> {
        const active = new Set<string>();
        for (const [topic, subs] of this._subscriptions) {
            if (subs.size > 0) active.add(topic);
        }
        return active;
    }
}

function getBackendWebsocketUrl() {
    const host = process.env.BACKEND_URL!;
    return `${host.replace(/\/$/, '')}/ws`.replace(/^http/, 'ws');
}

export const websocketServer = {
    initialize: initializeWebsocketServer
}
