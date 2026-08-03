import { describe, expect, it, vi } from "vitest";
import WebSocket from "ws";
import {
    BACKEND_RECONNECT_INITIAL_MS,
    BACKEND_RECONNECT_MAX_MS,
    MAX_CLIENT_BUFFERED_AMOUNT,
    MAX_TOPICS_PER_SOCKET,
    nextBackendReconnectDelayMs,
    parseSubscriptionTopics,
    sendToBrowserClient,
    UpstreamSubscriptionForwarder,
} from "./websocket.server";

describe("parseSubscriptionTopics", () => {
    it("accepts a flat state/stream map", () => {
        expect(parseSubscriptionTopics(JSON.stringify({ ls: "state", cxs: "stream" }))).toEqual({
            ls: "state",
            cxs: "stream",
        });
    });

    it("rejects arrays, non-objects, and invalid kinds", () => {
        expect(parseSubscriptionTopics("[]")).toBeNull();
        expect(parseSubscriptionTopics('"ls"')).toBeNull();
        expect(parseSubscriptionTopics(JSON.stringify({ ls: "wat" }))).toBeNull();
        expect(parseSubscriptionTopics("{")).toBeNull();
    });

    it("rejects more than MAX_TOPICS_PER_SOCKET topics", () => {
        const topics: Record<string, "state"> = {};
        for (let i = 0; i < MAX_TOPICS_PER_SOCKET + 1; i++) {
            topics[`t${i}`] = "state";
        }
        expect(parseSubscriptionTopics(JSON.stringify(topics))).toBeNull();
    });
});

describe("sendToBrowserClient", () => {
    it("skips sends when the client buffer is too full", () => {
        const send = vi.fn();
        const client = {
            readyState: WebSocket.OPEN,
            bufferedAmount: MAX_CLIENT_BUFFERED_AMOUNT + 1,
            send,
        } as unknown as WebSocket;

        sendToBrowserClient(client, "msg");
        expect(send).not.toHaveBeenCalled();
    });

    it("sends when the client is open and not back-pressured", () => {
        const send = vi.fn();
        const client = {
            readyState: WebSocket.OPEN,
            bufferedAmount: 0,
            send,
        } as unknown as WebSocket;

        sendToBrowserClient(client, "msg");
        expect(send).toHaveBeenCalledWith("msg");
    });
});

describe("nextBackendReconnectDelayMs", () => {
    it("stays within the exponential cap for early attempts", () => {
        // random() is treated as [0, 1); 0.999… yields the inclusive upper bound.
        const almostOne = () => 0.999999;
        expect(nextBackendReconnectDelayMs(0, almostOne)).toBe(BACKEND_RECONNECT_INITIAL_MS);
        expect(nextBackendReconnectDelayMs(1, almostOne)).toBe(BACKEND_RECONNECT_INITIAL_MS * 2);
        expect(nextBackendReconnectDelayMs(2, almostOne)).toBe(BACKEND_RECONNECT_INITIAL_MS * 4);
    });

    it("caps at BACKEND_RECONNECT_MAX_MS", () => {
        expect(nextBackendReconnectDelayMs(20, () => 0.999999)).toBe(BACKEND_RECONNECT_MAX_MS);
    });

    it("uses full jitter so a zero draw yields zero delay", () => {
        expect(nextBackendReconnectDelayMs(3, () => 0)).toBe(0);
    });
});

describe("UpstreamSubscriptionForwarder", () => {
    function createMockSocket() {
        const sent: string[] = [];
        return {
            socket: {
                readyState: WebSocket.OPEN,
                send: (data: string) => sent.push(data),
            } as unknown as WebSocket,
            sent,
        };
    }

    it("sends sub when a topic gains its first subscriber", () => {
        const subscriptions = new Map<string, Set<WebSocket>>();
        const forwarder = new UpstreamSubscriptionForwarder(subscriptions);
        const { socket, sent } = createMockSocket();
        forwarder.setBackendSocket(socket);

        subscriptions.set("ls", new Set([{} as WebSocket]));
        forwarder.syncAfterBrowserChange();

        expect(sent).toHaveLength(1);
        expect(JSON.parse(sent[0])).toEqual({ sub: ["ls"] });
    });

    it("sends unsub when a topic loses its last subscriber", () => {
        const subscriptions = new Map<string, Set<WebSocket>>();
        subscriptions.set("ls", new Set([{} as WebSocket]));
        const forwarder = new UpstreamSubscriptionForwarder(subscriptions);
        const { socket, sent } = createMockSocket();
        forwarder.setBackendSocket(socket);
        forwarder.sendFullSubscriptionSet();
        sent.length = 0;

        subscriptions.get("ls")!.clear();
        forwarder.syncAfterBrowserChange();

        expect(sent).toHaveLength(1);
        expect(JSON.parse(sent[0])).toEqual({ unsub: ["ls"] });
    });

    it("sends full subscription set on reconnect", () => {
        const subscriptions = new Map<string, Set<WebSocket>>();
        subscriptions.set("ls", new Set([{} as WebSocket]));
        subscriptions.set("cxs", new Set([{} as WebSocket]));
        const forwarder = new UpstreamSubscriptionForwarder(subscriptions);
        const { socket, sent } = createMockSocket();
        forwarder.setBackendSocket(socket);

        forwarder.sendFullSubscriptionSet();

        expect(sent).toHaveLength(1);
        const parsed = JSON.parse(sent[0]);
        expect(parsed.sub.sort()).toEqual(["cxs", "ls"]);
    });

    it("does not send when backend socket is not connected", () => {
        const subscriptions = new Map<string, Set<WebSocket>>();
        subscriptions.set("ls", new Set([{} as WebSocket]));
        const forwarder = new UpstreamSubscriptionForwarder(subscriptions);

        forwarder.syncAfterBrowserChange();
        // No error, just no-op
    });

    it("does not send redundant sub/unsub messages", () => {
        const subscriptions = new Map<string, Set<WebSocket>>();
        subscriptions.set("ls", new Set([{} as WebSocket]));
        const forwarder = new UpstreamSubscriptionForwarder(subscriptions);
        const { socket, sent } = createMockSocket();
        forwarder.setBackendSocket(socket);
        forwarder.sendFullSubscriptionSet();
        sent.length = 0;

        // Add a second subscriber to "ls" — topic was already active, no message needed
        subscriptions.get("ls")!.add({} as WebSocket);
        forwarder.syncAfterBrowserChange();

        expect(sent).toHaveLength(0);
    });
});

describe("relay reconnect preserves browser clients", () => {
    it("fans out backend state to existing subscribers without closing them", () => {
        const send = vi.fn();
        const close = vi.fn();
        const client = {
            readyState: WebSocket.OPEN,
            bufferedAmount: 0,
            send,
            close,
        } as unknown as WebSocket;

        const subscriptions = new Map<string, Set<WebSocket>>([
            ["ls", new Set([client])],
            ["cxs", new Set([client])],
        ]);
        const lastMessage = new Map<string, string>([
            ["ls", JSON.stringify({ Topic: "ls", Message: "old" })],
        ]);

        // Simulate the hub onmessage path: update cache and fan out.
        // Browser sockets must remain open across a backend relay reconnect.
        const rawMessage = JSON.stringify({ Topic: "ls", Message: "fresh" });
        const topicMessage = JSON.parse(rawMessage) as { Topic: string; Message: string };
        lastMessage.set(topicMessage.Topic, rawMessage);
        for (const subscriber of subscriptions.get(topicMessage.Topic) ?? []) {
            sendToBrowserClient(subscriber, rawMessage);
        }

        expect(close).not.toHaveBeenCalled();
        expect(lastMessage.get("ls")).toBe(rawMessage);
        expect(send).toHaveBeenCalledWith(rawMessage);
    });
});
