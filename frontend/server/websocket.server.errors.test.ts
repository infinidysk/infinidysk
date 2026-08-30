import { spawn, type ChildProcess } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, rm } from "node:fs/promises";
import http from "node:http";
import net from "node:net";
import type { AddressInfo } from "node:net";
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

vi.hoisted(() => {
  process.env["SESSION_KEY"] ??= "1234-in-process-session-key";
  process.env["FRONTEND_BACKEND_API_KEY"] ??= "1234-dummy-frontend-backend-api-key";
  process.env["BACKEND_URL"] ??= "http://127.0.0.1:9";
});

const FRONTEND_ROOT = fileURLToPath(new URL("..", import.meta.url));
const REPO_ROOT = fileURLToPath(new URL("../..", import.meta.url));
const DUMMY_API_KEY = "1234-dummy-frontend-backend-api-key";
const DUMMY_SESSION_KEY = "1234-dummy-session-key";

const PROBE_SOURCE = `
import { register } from "node:module";
import http from "node:http";
import net from "node:net";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { WebSocket, WebSocketServer } from "ws";

const frontendRoot = process.env.FRONTEND_ROOT;
const mode = process.env.WEBSOCKET_PROBE_MODE;
const appHref = pathToFileURL(path.join(frontendRoot, "app")).href + "/";
register("data:text/javascript," + encodeURIComponent(\`
export async function resolve(specifier, context, nextResolve) {
  if (specifier.startsWith("~/")) {
    return nextResolve(new URL(specifier.slice(2), \${JSON.stringify(appHref)}).href, context);
  }
  return nextResolve(specifier, context);
}
\`));

const [{ initializeWebsocketServer }, policy, { attachWebsocketServerErrorListener }] = await Promise.all([
  import(pathToFileURL(path.join(frontendRoot, "server/websocket.server.ts")).href),
  import(pathToFileURL(path.join(frontendRoot, "server/websocket-policy.ts")).href),
  import(pathToFileURL(path.join(frontendRoot, "server/http-server-lifecycle.ts")).href),
]);
const { MAX_WEBSOCKET_PAYLOAD_BYTES, attachBrowserWebsocketErrorListener, reportBrowserSocketError, errorCode } = policy;

function deferred() {
  let resolve;
  const promise = new Promise((res) => { resolve = res; });
  return { promise, resolve };
}

function listenOnLoopback(server) {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolve(server.address()));
  });
}

function waitForOpen(ws, timeoutMs = 5000) {
  if (ws.readyState === WebSocket.OPEN) return Promise.resolve();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("open timeout")), timeoutMs);
    ws.once("open", () => { clearTimeout(timer); resolve(); });
  });
}

function waitForClose(ws, timeoutMs = 5000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("close timeout")), timeoutMs);
    ws.once("close", (code) => { clearTimeout(timer); resolve(code); });
  });
}

function websocketUpgradeRequest(port) {
  const key = Buffer.from("1234567890abcdef").toString("base64");
  return Buffer.from(
    "GET /ws HTTP/1.1\\r\\n" +
    "Host: 127.0.0.1:" + port + "\\r\\n" +
    "Upgrade: websocket\\r\\n" +
    "Connection: Upgrade\\r\\n" +
    "Sec-WebSocket-Key: " + key + "\\r\\n" +
    "Sec-WebSocket-Version: 13\\r\\n" +
    "\\r\\n",
  );
}

function oversizedMaskedBinaryFrame(payloadLength) {
  const header = Buffer.alloc(2 + 8 + 4);
  header[0] = 0x82;
  header[1] = 0x80 | 127;
  header.writeBigUInt64BE(BigInt(payloadLength), 2);
  return Buffer.concat([header, Buffer.alloc(payloadLength)]);
}

const auth = deferred();
let acceptedErrorCode = null;
let resolveAccepted = () => {};
const accepted = new Promise((resolve) => { resolveAccepted = resolve; });

function report(error, context) {
  reportBrowserSocketError(error, context);
  const code = errorCode(error);
  process.stdout.write("ACCEPTED_ERROR_CODE=" + code + "\\n");
  if (acceptedErrorCode == null) {
    acceptedErrorCode = code;
    resolveAccepted(code);
  }
}

const httpServer = http.createServer();
const wss = new WebSocketServer({
  server: httpServer,
  path: "/ws",
  maxPayload: MAX_WEBSOCKET_PAYLOAD_BYTES,
});
attachWebsocketServerErrorListener(wss, {
  isOwned: () => false,
  onUnexpectedError: (error) => {
    process.stderr.write("WSS_FATAL " + error.message + "\\n");
    process.exitCode = 1;
    process.exit(1);
  },
});
initializeWebsocketServer(wss, {
  authenticate: () => auth.promise,
  startBackendClient: () => {},
  reportBrowserSocketError: report,
  registerBrowserSocketErrorListener: mode === "oversized-pre-auth-listener-removed"
    ? () => {}
    : attachBrowserWebsocketErrorListener,
});

const address = await listenOnLoopback(httpServer);
const url = "ws://127.0.0.1:" + address.port + "/ws";
let clientCloseCode = null;
let nextConnectionOpened = false;

try {
  if (mode === "pipelined-oversized") {
    const socket = net.connect(address.port, "127.0.0.1");
    socket.on("error", () => {});
    await new Promise((resolve, reject) => {
      socket.once("connect", resolve);
      socket.once("error", reject);
    });
    socket.write(Buffer.concat([
      websocketUpgradeRequest(address.port),
      oversizedMaskedBinaryFrame(MAX_WEBSOCKET_PAYLOAD_BYTES + 1),
    ]));
    await Promise.race([
      accepted,
      new Promise((_, reject) => setTimeout(() => reject(new Error("accepted error timeout")), 8000)),
    ]);
    socket.destroy();
  } else {
    const client = new WebSocket(url);
    client.on("error", () => {});
    await waitForOpen(client);
    const closed = waitForClose(client);
    client.send(Buffer.alloc(MAX_WEBSOCKET_PAYLOAD_BYTES + 1));
    clientCloseCode = await closed;
    await Promise.race([
      accepted,
      new Promise((_, reject) => setTimeout(() => reject(new Error("accepted error timeout")), 8000)),
    ]);
  }

  const healthy = new WebSocket(url);
  healthy.on("error", () => {});
  await waitForOpen(healthy);
  nextConnectionOpened = true;
  healthy.close();
  await waitForClose(healthy);

  const observation = { acceptedErrorCode, clientCloseCode, nextConnectionOpened };
  process.stdout.write("PROBE_RESULT=" + JSON.stringify(observation) + "\\n");
  process.stdout.write("probe-complete\\n");
  process.exitCode = 0;
} finally {
  auth.resolve(false);
  for (const client of wss.clients) client.terminate();
  await new Promise((resolve) => wss.close(() => resolve()));
  await new Promise((resolve) => httpServer.close(() => resolve()));
}
`;

function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason?: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function listenOnLoopback(server: http.Server): Promise<AddressInfo> {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (!address || typeof address === "string") {
        reject(new Error("server has no address"));
        return;
      }
      resolve(address);
    });
  });
}

function closeServer(server: http.Server): Promise<void> {
  return new Promise((resolve, reject) => {
    server.close((error) => {
      if (error) reject(error);
      else resolve();
    });
  });
}

function waitForOpen(ws: WebSocket, timeoutMs = 5_000): Promise<void> {
  if (ws.readyState === WebSocket.OPEN) return Promise.resolve();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("WebSocket open timed out")), timeoutMs);
    ws.once("open", () => {
      clearTimeout(timer);
      resolve();
    });
  });
}

function waitForClose(ws: WebSocket, timeoutMs = 5_000): Promise<number> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("WebSocket close timed out")), timeoutMs);
    ws.once("close", (code) => {
      clearTimeout(timer);
      resolve(code);
    });
  });
}

function connectClient(port: number): WebSocket {
  const client = new WebSocket(`ws://127.0.0.1:${port}/ws`);
  client.on("error", () => {});
  return client;
}

function websocketUpgradeRequest(port: number): Buffer {
  const key = Buffer.from("1234567890abcdef").toString("base64");
  return Buffer.from(
    `GET /ws HTTP/1.1\r\n` +
      `Host: 127.0.0.1:${port}\r\n` +
      `Upgrade: websocket\r\n` +
      `Connection: Upgrade\r\n` +
      `Sec-WebSocket-Key: ${key}\r\n` +
      `Sec-WebSocket-Version: 13\r\n` +
      `\r\n`,
  );
}

function oversizedMaskedBinaryFrame(payloadLength: number): Buffer {
  const header = Buffer.alloc(2 + 8 + 4);
  header[0] = 0x82;
  header[1] = 0x80 | 127;
  header.writeBigUInt64BE(BigInt(payloadLength), 2);
  return Buffer.concat([header, Buffer.alloc(payloadLength)]);
}

async function waitUntil(predicate: () => boolean, timeoutMs = 5_000): Promise<void> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error("waitUntil timed out");
}

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
    FRONTEND_ROOT,
    WEBSOCKET_PROBE_MODE: mode,
  };

  try {
    child = spawn(
      process.execPath,
      ["--import", "tsx", "--input-type=module", "--eval", PROBE_SOURCE],
      {
        cwd: FRONTEND_ROOT,
        env: childEnv,
        stdio: ["ignore", "pipe", "pipe"],
      },
    );
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
  initializeWebsocketServer(wss, {
    authenticate: overrides.authenticate,
    startBackendClient: overrides.startBackendClient ?? (() => {}),
    reportBrowserSocketError:
      overrides.reportBrowserSocketError ??
      ((error, context) => {
        reports.push(error);
        reportBrowserSocketError(error, context);
      }),
    registerBrowserSocketErrorListener:
      overrides.registerBrowserSocketErrorListener ?? attachBrowserWebsocketErrorListener,
  });
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

  beforeEach(() => {
    resetClientErrorLogThrottleForTests();
  });

  afterEach(async () => {
    resetClientErrorLogThrottleForTests();
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
