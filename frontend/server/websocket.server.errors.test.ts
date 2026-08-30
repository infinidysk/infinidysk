import { spawn, type ChildProcess } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, rm } from "node:fs/promises";
import http from "node:http";
import net from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import WebSocket, { WebSocketServer } from "ws";
import { attachWebsocketServerErrorListener } from "./http-server-lifecycle";
import { resetClientErrorLogThrottleForTests } from "./request-log-throttle";
import {
  attachBrowserWebsocketErrorListener,
  errorCode,
  MAX_WEBSOCKET_PAYLOAD_BYTES,
  reportBrowserSocketError,
} from "./websocket-policy";
import { initializeWebsocketServer, type WebsocketServerDependencies } from "./websocket.server";
import {
  closeServer,
  connectClient,
  deferred,
  listenOnLoopback,
  oversizedMaskedBinaryFrame,
  waitForClose,
  waitForOpen,
  waitUntil,
  websocketUpgradeRequest,
} from "./websocket-test-helpers";

vi.hoisted(() => {
  process.env["SESSION_KEY"] ??= "1234-in-process-session-key";
  process.env["FRONTEND_BACKEND_API_KEY"] ??= "1234-dummy-frontend-backend-api-key";
  process.env["BACKEND_URL"] ??= "http://127.0.0.1:9";
});

const FRONTEND_ROOT = fileURLToPath(new URL("..", import.meta.url));
const REPO_ROOT = fileURLToPath(new URL("../..", import.meta.url));
const PROBE_PATH = fileURLToPath(new URL("./__fixtures__/websocket-probe.ts", import.meta.url));
const DUMMY_API_KEY = "1234-dummy-frontend-backend-api-key";
const DUMMY_SESSION_KEY = "1234-dummy-session-key";

function waitForExit(
  child: ChildProcess,
  timeoutMs: number,
  output: () => string,
): Promise<{ code: number | null; signal: NodeJS.Signals | null }> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      reject(
        new Error(
          `websocket probe did not exit within ${timeoutMs}ms\n--- child output ---\n${output()}`,
        ),
      );
    }, timeoutMs);
    child.once("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.once("exit", (code, signal) => {
      clearTimeout(timer);
      resolve({ code, signal });
    });
  });
}

type ProbeObservation = {
  acceptedErrorCode?: string | null;
  clientCloseCode?: number | null;
  nextConnectionOpened?: boolean;
};

async function runWebsocketProbe(mode: string): Promise<{
  exitCode: number | null;
  signal: NodeJS.Signals | null;
  stdout: string;
  stderr: string;
  observation: ProbeObservation;
}> {
  const configPath = await mkdtemp(path.join(tmpdir(), "nzbdav-1234-"));
  let child: ChildProcess | undefined;
  let stdout = "";
  let stderr = "";
  const output = () => `${stdout}\n${stderr}`;

  const childEnv: NodeJS.ProcessEnv = {
    PATH: process.env["PATH"],
    HOME: process.env["HOME"],
    TMPDIR: process.env["TMPDIR"],
    NODE_ENV: "test",
    NO_COLOR: "1",
    SESSION_KEY: DUMMY_SESSION_KEY,
    CONFIG_PATH: configPath,
    BACKEND_URL: "http://127.0.0.1:9",
    FRONTEND_BACKEND_API_KEY: DUMMY_API_KEY,
    WEBSOCKET_PROBE_MODE: mode,
    TSX_TSCONFIG_PATH: path.join(FRONTEND_ROOT, "tsconfig.vite.json"),
  };

  try {
    child = spawn(process.execPath, ["--import", "tsx", PROBE_PATH], {
      cwd: FRONTEND_ROOT,
      env: childEnv,
      stdio: ["ignore", "pipe", "pipe"],
    });
    child.stdout?.setEncoding("utf8");
    child.stderr?.setEncoding("utf8");
    child.stdout?.on("data", (chunk: string) => {
      stdout += chunk;
    });
    child.stderr?.on("data", (chunk: string) => {
      stderr += chunk;
    });

    const result = await waitForExit(child, 25_000, output);
    const resultLine = stdout.split("\n").find((line) => line.startsWith("PROBE_RESULT="));
    const observation = resultLine
      ? (JSON.parse(resultLine.slice("PROBE_RESULT=".length)) as ProbeObservation)
      : {};
    return {
      exitCode: result.code,
      signal: result.signal,
      stdout,
      stderr,
      observation,
    };
  } finally {
    if (child && child.exitCode === null && child.signalCode === null) {
      child.kill("SIGKILL");
      await waitForExit(child, 5_000, output).catch(() => undefined);
    }
    child?.stdout?.destroy();
    child?.stderr?.destroy();
    await rm(configPath, { recursive: true, force: true });
  }
}

function assertNoRepoSessionKey(): void {
  expect(existsSync(path.join(REPO_ROOT, "session.key"))).toBe(false);
  expect(existsSync(path.join(FRONTEND_ROOT, "session.key"))).toBe(false);
  expect(existsSync(path.join(FRONTEND_ROOT, "server", "session.key"))).toBe(false);
}

async function startTestServer(
  overrides: Partial<WebsocketServerDependencies> & {
    authenticate: WebsocketServerDependencies["authenticate"];
  },
): Promise<{
  httpServer: http.Server;
  wss: WebSocketServer;
  port: number;
  reports: unknown[];
}> {
  const reports: unknown[] = [];
  const httpServer = http.createServer();
  const wss = new WebSocketServer({
    server: httpServer,
    path: "/ws",
    maxPayload: MAX_WEBSOCKET_PAYLOAD_BYTES,
  });
  attachWebsocketServerErrorListener(wss, {
    isOwned: () => false,
    onUnexpectedError: vi.fn(),
  });
  initializeWebsocketServer(
    wss,
    { backendApiKey: DUMMY_API_KEY },
    {
      authenticate: overrides.authenticate,
      startBackendClient: overrides.startBackendClient ?? (() => ({ stop() {} })),
      reportBrowserSocketError:
        overrides.reportBrowserSocketError ??
        ((error, context) => {
          reports.push(error);
          reportBrowserSocketError(error, context);
        }),
      registerBrowserSocketErrorListener:
        overrides.registerBrowserSocketErrorListener ?? attachBrowserWebsocketErrorListener,
    },
  );
  const address = await listenOnLoopback(httpServer);
  return { httpServer, wss, port: address.port, reports };
}

async function stopTestServer(httpServer: http.Server, wss: WebSocketServer): Promise<void> {
  for (const client of wss.clients) {
    client.terminate();
  }
  await new Promise<void>((resolve) => {
    wss.close(() => resolve());
  });
  await closeServer(httpServer);
}

describe("oversized pre-auth websocket child probes", () => {
  it("contains an oversized pre-auth frame and accepts a later connection", async () => {
    const fixed = await runWebsocketProbe("oversized-pre-auth");

    expect(fixed.exitCode).toBe(0);
    expect(fixed.signal).toBeNull();
    expect(fixed.stdout).toContain("WS_ERR_UNSUPPORTED_MESSAGE_LENGTH");
    expect(fixed.stdout).toContain("probe-complete");
    expect(fixed.observation.nextConnectionOpened).toBe(true);
    expect(fixed.observation.acceptedErrorCode).toBe("WS_ERR_UNSUPPORTED_MESSAGE_LENGTH");
    expect(fixed.observation.clientCloseCode).toBe(1009);
    expect(fixed.stdout).not.toContain(DUMMY_API_KEY);
    expect(fixed.stdout).not.toContain(DUMMY_SESSION_KEY);
    assertNoRepoSessionKey();
  }, 35_000);

  it("exits nonzero when the production accepted-socket listener is removed", async () => {
    const listenerRemoved = await runWebsocketProbe("oversized-pre-auth-listener-removed");

    expect(listenerRemoved.exitCode).not.toBe(0);
    expect(listenerRemoved.stdout).not.toContain("probe-complete");
    assertNoRepoSessionKey();
  }, 35_000);

  it("contains a pipelined upgrade-head oversized frame", async () => {
    const pipelined = await runWebsocketProbe("pipelined-oversized");

    expect(pipelined.exitCode).toBe(0);
    expect(pipelined.signal).toBeNull();
    expect(pipelined.stdout).toContain("WS_ERR_UNSUPPORTED_MESSAGE_LENGTH");
    expect(pipelined.stdout).toContain("probe-complete");
    expect(pipelined.observation.nextConnectionOpened).toBe(true);
    assertNoRepoSessionKey();
  }, 35_000);
});

describe("accepted browser websocket error handling", () => {
  const servers: Array<{ httpServer: http.Server; wss: WebSocketServer }> = [];
  const clients: WebSocket[] = [];
  const rawSockets: net.Socket[] = [];

  beforeEach(() => {
    resetClientErrorLogThrottleForTests();
  });

  afterEach(async () => {
    resetClientErrorLogThrottleForTests();
    for (const socket of rawSockets.splice(0)) {
      socket.destroy();
    }
    for (const client of clients.splice(0)) {
      client.terminate();
    }
    for (const server of servers.splice(0)) {
      await stopTestServer(server.httpServer, server.wss);
    }
    vi.restoreAllMocks();
  });

  it("cleans up an abrupt reset and still accepts a later connection", async () => {
    const auth = deferred<boolean>();
    const started = await startTestServer({ authenticate: () => auth.promise });
    servers.push(started);

    const client = connectClient(started.port);
    clients.push(client);
    const closed = waitForClose(client);
    await waitForOpen(client);
    client.terminate();
    await closed;
    await waitUntil(() => started.wss.clients.size === 0);

    const healthy = connectClient(started.port);
    clients.push(healthy);
    await waitForOpen(healthy);
    expect(healthy.readyState).toBe(WebSocket.OPEN);
    auth.resolve(false);
  }, 10_000);

  it("keeps only the newest pre-auth subscription snapshot", async () => {
    const auth = deferred<boolean>();
    const captured: string[] = [];
    const started = await startTestServer({
      authenticate: () => auth.promise,
      startBackendClient: (_subscriptions, _lastMessage, forwarder) => {
        forwarder?.setBackendSocket({
          readyState: WebSocket.OPEN,
          send: (data: string) => {
            captured.push(data);
          },
        } as unknown as WebSocket);
        return { stop() {} };
      },
    });
    servers.push(started);

    const client = connectClient(started.port);
    clients.push(client);
    await waitForOpen(client);
    client.send(JSON.stringify({ ls: "state" }));
    client.send(JSON.stringify({ cxs: "state" }));
    client.send(JSON.stringify({ ls: "state", cxs: "state" }));
    await waitUntil(() => client.bufferedAmount === 0);
    await new Promise((resolve) => setTimeout(resolve, 50));

    auth.resolve(true);
    await waitUntil(() => captured.length > 0);

    expect(captured).toHaveLength(1);
    const parsed = JSON.parse(captured[0]!) as { sub: string[] };
    expect(parsed.sub.sort()).toEqual(["cxs", "ls"]);
  }, 10_000);

  it("releases a pending snapshot if the socket closes before auth", async () => {
    const auth = deferred<boolean>();
    const captured: string[] = [];
    const started = await startTestServer({
      authenticate: () => auth.promise,
      startBackendClient: (_subscriptions, _lastMessage, forwarder) => {
        forwarder?.setBackendSocket({
          readyState: WebSocket.OPEN,
          send: (data: string) => {
            captured.push(data);
          },
        } as unknown as WebSocket);
        return { stop() {} };
      },
    });
    servers.push(started);

    const client = connectClient(started.port);
    clients.push(client);
    await waitForOpen(client);
    client.send(JSON.stringify({ ls: "state" }));
    await waitUntil(() => client.bufferedAmount === 0);
    await new Promise((resolve) => setTimeout(resolve, 50));
    const closed = waitForClose(client);
    client.close();
    await closed;
    await waitUntil(() => started.wss.clients.size === 0);

    auth.resolve(true);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(captured).toEqual([]);
  }, 10_000);

  it("constructs the browser WSS with the shared payload limit", async () => {
    const auth = deferred<boolean>();
    const started = await startTestServer({ authenticate: () => auth.promise });
    servers.push(started);
    expect(started.wss.options.maxPayload).toBe(MAX_WEBSOCKET_PAYLOAD_BYTES);
    auth.resolve(false);
  });

  it("does not treat an exact-limit frame as oversized, but does reject one extra byte", async () => {
    const auth = deferred<boolean>();
    const started = await startTestServer({ authenticate: () => auth.promise });
    servers.push(started);

    const exact = connectClient(started.port);
    clients.push(exact);
    await waitForOpen(exact);
    exact.send(Buffer.alloc(MAX_WEBSOCKET_PAYLOAD_BYTES));
    await waitUntil(() => clientBufferedOrClosed(exact));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(
      started.reports.some((error) => errorCode(error) === "WS_ERR_UNSUPPORTED_MESSAGE_LENGTH"),
    ).toBe(false);

    const oversized = connectClient(started.port);
    clients.push(oversized);
    const oversizedClosed = waitForClose(oversized);
    await waitForOpen(oversized);
    oversized.send(Buffer.alloc(MAX_WEBSOCKET_PAYLOAD_BYTES + 1));
    const closeCode = await oversizedClosed;
    await waitUntil(() =>
      started.reports.some((error) => errorCode(error) === "WS_ERR_UNSUPPORTED_MESSAGE_LENGTH"),
    );
    expect(closeCode).toBe(1009);
    auth.resolve(false);
  }, 10_000);

  it("attaches one error listener per accepted socket without growing it", async () => {
    const auth = deferred<boolean>();
    const accepted: WebSocket[] = [];
    const started = await startTestServer({ authenticate: () => auth.promise });
    servers.push(started);
    started.wss.on("connection", (socket) => {
      accepted.push(socket);
    });

    const client = connectClient(started.port);
    clients.push(client);
    await waitForOpen(client);
    await waitUntil(() => accepted.length === 1);
    expect(accepted[0]!.listenerCount("error")).toBe(1);

    auth.resolve(true);
    await new Promise((resolve) => setTimeout(resolve, 50));
    client.send(JSON.stringify({ ls: "state" }));
    client.send(JSON.stringify({ cxs: "state" }));
    await waitUntil(() => client.bufferedAmount === 0);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(accepted[0]!.listenerCount("error")).toBe(1);

    const closed = waitForClose(client);
    client.close();
    await closed;
    await waitUntil(() => started.wss.clients.size === 0);

    const next = connectClient(started.port);
    clients.push(next);
    await waitForOpen(next);
    await waitUntil(() => accepted.length === 2);
    expect(accepted[1]!.listenerCount("error")).toBe(1);
    expect(accepted[0]!.listenerCount("error")).toBe(1);
    expect(started.wss.clients.has(accepted[0]!)).toBe(false);
    expect(started.wss.clients.has(accepted[1]!)).toBe(true);
  }, 10_000);

  it("contains a pipelined upgrade plus oversized first frame in-process", async () => {
    const auth = deferred<boolean>();
    const started = await startTestServer({ authenticate: () => auth.promise });
    servers.push(started);

    const socket = net.connect(started.port, "127.0.0.1");
    rawSockets.push(socket);
    socket.on("error", () => {});
    await new Promise<void>((resolve, reject) => {
      socket.once("connect", () => resolve());
      socket.once("error", reject);
    });
    socket.write(
      Buffer.concat([
        websocketUpgradeRequest(started.port),
        oversizedMaskedBinaryFrame(MAX_WEBSOCKET_PAYLOAD_BYTES + 1),
      ]),
    );

    await waitUntil(() =>
      started.reports.some((error) => errorCode(error) === "WS_ERR_UNSUPPORTED_MESSAGE_LENGTH"),
    );

    const healthy = connectClient(started.port);
    clients.push(healthy);
    await waitForOpen(healthy);
    socket.destroy();
    auth.resolve(false);
  }, 10_000);
});

function clientBufferedOrClosed(client: WebSocket): boolean {
  return client.bufferedAmount === 0 || client.readyState === WebSocket.CLOSED;
}
