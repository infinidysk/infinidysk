import { afterEach, describe, expect, it, vi } from "vitest";
import http from "node:http";
import type { AddressInfo } from "node:net";
import WebSocket, { WebSocketServer } from "ws";
import {
  BACKEND_RECONNECT_INITIAL_MS,
  BACKEND_RECONNECT_MAX_MS,
  cacheStateMessage,
  initializeWebsocketClient,
  MAX_CLIENT_BUFFERED_AMOUNT,
  MAX_TOPICS_PER_SOCKET,
  nextBackendReconnectDelayMs,
  parseSubscriptionTopics,
  replayStateMessages,
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

describe("connection state replay", () => {
  it("replays the latest state for every provider with newest totals last", () => {
    const lastMessage = new Map<string, string>();
    const providerZero = JSON.stringify({ Topic: "cxs", Message: "0|8|8|13|90|13" });
    const providerOne = JSON.stringify({ Topic: "cxs", Message: "1|5|5|13|90|13" });
    const providerZeroUpdated = JSON.stringify({ Topic: "cxs", Message: "0|7|7|12|90|12" });

    cacheStateMessage(lastMessage, "cxs", "0|8|8|13|90|13", providerZero);
    cacheStateMessage(lastMessage, "cxs", "1|5|5|13|90|13", providerOne);
    cacheStateMessage(lastMessage, "cxs", "0|7|7|12|90|12", providerZeroUpdated);

    expect(replayStateMessages(lastMessage, "cxs")).toEqual([providerOne, providerZeroUpdated]);
  });

  it("keeps ordinary state topics as a single latest value", () => {
    const lastMessage = new Map<string, string>();
    const message = JSON.stringify({ Topic: "ls", Message: "latest" });

    cacheStateMessage(lastMessage, "ls", "latest", message);

    expect(replayStateMessages(lastMessage, "ls")).toEqual([message]);
  });

  it("drops retired provider state when the backend resets connection replay", () => {
    const lastMessage = new Map<string, string>();
    const providerZero = JSON.stringify({ Topic: "cxs", Message: "0|8|8|8|60|8" });

    cacheStateMessage(lastMessage, "cxs", "0|8|8|8|60|8", providerZero);
    const shouldRelay = cacheStateMessage(
      lastMessage,
      "cxs",
      "reset",
      JSON.stringify({ Topic: "cxs", Message: "reset" }),
    );

    expect(shouldRelay).toBe(false);
    expect(replayStateMessages(lastMessage, "cxs")).toEqual([]);
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
    expect(JSON.parse(sent[0]!)).toEqual({ sub: ["ls"] });
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
    expect(JSON.parse(sent[0]!)).toEqual({ unsub: ["ls"] });
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
    const parsed = JSON.parse(sent[0]!) as { sub: string[] };
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

describe("initializeWebsocketClient relay authentication", () => {
  const resources: Array<() => Promise<void>> = [];

  afterEach(async () => {
    vi.unstubAllEnvs();
    const closers = resources.splice(0);
    await Promise.all(closers.map((close) => close()));
  });

  it("sends the runtime backend API key instead of reading process.env", async () => {
    vi.stubEnv("FRONTEND_BACKEND_API_KEY", "poisoned-env-key");

    const messages: string[] = [];
    const server = http.createServer();
    server.on("error", () => undefined);
    const wss = new WebSocketServer({ server, path: "/ws" });
    const firstMessage = new Promise<string>((resolve, reject) => {
      wss.on("connection", (socket) => {
        socket.on("message", (data) => {
          const text = Buffer.isBuffer(data)
            ? data.toString("utf8")
            : Array.isArray(data)
              ? Buffer.concat(data).toString("utf8")
              : Buffer.from(data).toString("utf8");
          messages.push(text);
          if (messages.length === 1) resolve(text);
        });
        socket.on("error", reject);
      });
    });

    await new Promise<void>((resolve, reject) => {
      server.listen(0, "127.0.0.1", () => resolve());
      server.once("error", reject);
    });
    const address = server.address() as AddressInfo;
    vi.stubEnv("BACKEND_URL", `http://127.0.0.1:${address.port}`);

    const relay = initializeWebsocketClient(new Map(), new Map(), undefined, {
      backendApiKey: "relay-test-key",
    });
    resources.push(() => {
      relay.stop();
      return Promise.resolve();
    });
    resources.push(
      () =>
        new Promise<void>((resolve, reject) => {
          wss.close((error) => {
            if (error) reject(error);
            else resolve();
          });
        }),
    );
    resources.push(
      () =>
        new Promise<void>((resolve, reject) => {
          server.close((error) => {
            if (error) reject(error);
            else resolve();
          });
        }),
    );

    await expect(firstMessage).resolves.toBe("relay-test-key");
    expect(messages).toHaveLength(1);
  });
});
