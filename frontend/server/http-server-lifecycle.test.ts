import { spawn, type ChildProcess } from "node:child_process";
import { EventEmitter } from "node:events";
import { mkdtemp, rm } from "node:fs/promises";
import http from "node:http";
import type { AddressInfo } from "node:net";
import net from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  attachHttpServerLifecycle,
  attachWebsocketServerErrorListener,
  formatStartupListenError,
  isHttpServerErrorOwned,
  markHttpServerErrorOwned,
  type HttpServerLifecycleOptions,
} from "./http-server-lifecycle";

const FRONTEND_ROOT = fileURLToPath(new URL("..", import.meta.url));
const SECRET_MARKER = "DO_NOT_LOG_SECRET_MARKER";
const DUMMY_API_KEY = "1246-dummy-frontend-backend-api-key";

function systemError(
  message: string,
  fields: { code?: unknown; address?: unknown; port?: unknown; syscall?: unknown },
): Error {
  return Object.assign(new Error(message), fields);
}

function lifecycleOptions(
  overrides: Partial<HttpServerLifecycleOptions> = {},
): HttpServerLifecycleOptions & {
  logError: ReturnType<typeof vi.fn>;
  onListening: ReturnType<typeof vi.fn>;
  disposeStartupResources: ReturnType<typeof vi.fn>;
  markFatal: ReturnType<typeof vi.fn>;
} {
  return {
    configuredPort: 3000,
    logError: vi.fn(),
    onListening: vi.fn(),
    disposeStartupResources: vi.fn(() => Promise.resolve()),
    markFatal: vi.fn(),
    ...overrides,
  };
}

function listen(server: http.Server | net.Server): Promise<AddressInfo> {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, () => {
      const address = server.address();
      if (!address || typeof address === "string") {
        reject(new Error("server has no address"));
        return;
      }
      resolve(address);
    });
  });
}

function listenOnLoopback(server: http.Server | net.Server): Promise<AddressInfo> {
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

function closeServer(server: http.Server | net.Server): Promise<void> {
  return new Promise((resolve, reject) => {
    server.close((error) => {
      if (error) reject(error);
      else resolve();
    });
  });
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
          `frontend child did not exit within ${timeoutMs}ms\n--- child output ---\n${output()}`,
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

describe("formatStartupListenError", () => {
  it("formats EADDRINUSE with address, port, and a corrective action", () => {
    const error = systemError(SECRET_MARKER, {
      code: "EADDRINUSE",
      address: "127.0.0.1",
      port: 45678,
      syscall: "listen",
    });

    const message = formatStartupListenError(error, 3000);

    expect(message).toContain("Frontend server could not start");
    expect(message).toContain("EADDRINUSE");
    expect(message).toContain("127.0.0.1");
    expect(message).toContain("45678");
    expect(message).toContain("Stop the other listener or set PORT to an available port");
    expect(message).not.toContain(SECRET_MARKER);
    expect(message).not.toContain("\n");
  });

  it("formats EACCES without attempting a privileged bind", () => {
    const error = systemError(SECRET_MARKER, {
      code: "EACCES",
      address: "0.0.0.0",
      port: 80,
      syscall: "listen",
    });

    const message = formatStartupListenError(error, 3000);

    expect(message).toContain("Frontend server could not start");
    expect(message).toContain("EACCES");
    expect(message).toContain("0.0.0.0");
    expect(message).toContain("80");
    expect(message).toContain(
      "Use an unprivileged PORT or grant the runtime permission to bind it",
    );
    expect(message).not.toContain(SECRET_MARKER);
  });

  it("formats unknown codes without interpolating the raw Error message", () => {
    const error = systemError(SECRET_MARKER, {
      code: "EFOO",
      address: "127.0.0.1",
      port: 3000,
    });

    const message = formatStartupListenError(error, 3000);

    expect(message).toContain("code EFOO");
    expect(message).toContain(
      "Check the bind configuration and operating-system limits, then restart",
    );
    expect(message).not.toContain(SECRET_MARKER);
  });

  it("formats EADDRNOTAVAIL and descriptor-limit codes", () => {
    expect(
      formatStartupListenError(
        systemError(SECRET_MARKER, { code: "EADDRNOTAVAIL", address: "192.0.2.1", port: 3000 }),
        3000,
      ),
    ).toContain("the local address is unavailable");
    expect(
      formatStartupListenError(
        systemError(SECRET_MARKER, { code: "EMFILE", address: "127.0.0.1", port: 3000 }),
        3000,
      ),
    ).toContain("the file-descriptor limit was reached");
    expect(
      formatStartupListenError(
        systemError(SECRET_MARKER, { code: "ENFILE", address: "127.0.0.1", port: 3000 }),
        3000,
      ),
    ).toContain("the file-descriptor limit was reached");
  });

  it("falls back when metadata is absent, malformed, or injects control characters", () => {
    const error = systemError(SECRET_MARKER, {
      code: 123,
      address: "127.0.0.1\ninjected",
      port: 1.5,
    });

    const message = formatStartupListenError(error, 3000);

    expect(message).toContain("address default interface");
    expect(message).toContain("port 3000");
    expect(message).toContain("code UNKNOWN");
    expect(message).not.toContain("injected");
    expect(message).not.toContain(SECRET_MARKER);
    expect(message).not.toContain("\n");
  });

  it("rejects overlong tokens and out-of-range ports", () => {
    const error = systemError(SECRET_MARKER, {
      code: `EADDRINUSE${"x".repeat(200)}`,
      address: "a".repeat(200),
      port: 70000,
    });

    const message = formatStartupListenError(error, 4123);

    expect(message).toContain("address default interface");
    expect(message).toContain("port 4123");
    expect(message).toContain("code UNKNOWN");
    expect(message).not.toContain("EADDRINUSE");
    expect(() => formatStartupListenError(error, 4123)).not.toThrow();
  });

  it("accepts the legal TCP port range and rejects negatives", () => {
    expect(
      formatStartupListenError(
        systemError("x", { code: "EADDRINUSE", address: "127.0.0.1", port: 0 }),
        3000,
      ),
    ).toContain("port 0");
    expect(
      formatStartupListenError(
        systemError("x", { code: "EADDRINUSE", address: "127.0.0.1", port: 65535 }),
        3000,
      ),
    ).toContain("port 65535");
    expect(
      formatStartupListenError(
        systemError(SECRET_MARKER, { code: "EADDRINUSE", address: "127.0.0.1", port: -1 }),
        3000,
      ),
    ).toContain("port 3000");
  });
});

describe("attachHttpServerLifecycle", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("registers an error listener immediately and never touches process.exit", () => {
    const server = http.createServer();
    const exitSpy = vi
      .spyOn(process, "exit")
      .mockImplementation((() => undefined) as typeof process.exit);
    const previousExitCode = process.exitCode;
    const options = lifecycleOptions();

    expect(server.listenerCount("error")).toBe(0);
    const listeningBefore = server.listenerCount("listening");
    attachHttpServerLifecycle(server, options);
    expect(server.listenerCount("error")).toBe(1);
    expect(server.listenerCount("listening")).toBe(listeningBefore + 1);

    const error = systemError(SECRET_MARKER, {
      code: "EADDRINUSE",
      address: "127.0.0.1",
      port: 3000,
    });
    server.emit("error", error);

    expect(exitSpy).not.toHaveBeenCalled();
    expect(process.exitCode).toBe(previousExitCode);
  });

  it("treats a pre-listening error as an exactly-once startup failure", async () => {
    const server = http.createServer();
    let resolveDispose: (() => void) | undefined;
    const options = lifecycleOptions({
      disposeStartupResources: vi.fn(
        () =>
          new Promise<void>((resolve) => {
            resolveDispose = resolve;
          }),
      ),
    });
    const lifecycle = attachHttpServerLifecycle(server, options);
    const first = systemError(SECRET_MARKER, {
      code: "EADDRINUSE",
      address: "127.0.0.1",
      port: 45678,
    });
    const second = systemError("another", { code: "EACCES", address: "127.0.0.1", port: 3000 });

    server.emit("error", first);
    expect(lifecycle.owns(first)).toBe(true);

    server.emit("error", first);
    server.emit("error", second);

    expect(options.logError).toHaveBeenCalledOnce();
    expect(options.logError.mock.calls[0]).toEqual([
      expect.stringContaining("Frontend server could not start"),
    ]);
    expect(options.markFatal).toHaveBeenCalledOnce();
    expect(options.markFatal).toHaveBeenCalledWith(1, "startup");
    expect(options.disposeStartupResources).toHaveBeenCalledOnce();
    expect(options.onListening).not.toHaveBeenCalled();
    expect(lifecycle.owns(second)).toBe(true);

    resolveDispose?.();
    await lifecycle.failure();
  });

  it("invokes onListening exactly once and ignores a later startup callback", async () => {
    const server = http.createServer();
    const options = lifecycleOptions();
    attachHttpServerLifecycle(server, options);

    const address = await listenOnLoopback(server);
    expect(address.port).toBeGreaterThan(0);
    expect(options.onListening).toHaveBeenCalledOnce();
    expect(options.markFatal).not.toHaveBeenCalled();
    expect(options.disposeStartupResources).not.toHaveBeenCalled();
    expect(options.logError).not.toHaveBeenCalled();

    server.emit("listening");
    expect(options.onListening).toHaveBeenCalledOnce();

    await closeServer(server);
  });

  it("does not initialize after a late listening event that follows failure", () => {
    const server = http.createServer();
    const options = lifecycleOptions();
    attachHttpServerLifecycle(server, options);

    server.emit(
      "error",
      systemError(SECRET_MARKER, { code: "EADDRINUSE", address: "127.0.0.1", port: 3000 }),
    );
    server.emit("listening");

    expect(options.onListening).not.toHaveBeenCalled();
    expect(options.markFatal).toHaveBeenCalledWith(1, "startup");
  });

  it("describes a post-listening error as a runtime failure", async () => {
    const server = http.createServer();
    const options = lifecycleOptions();
    const lifecycle = attachHttpServerLifecycle(server, options);
    const error = systemError(SECRET_MARKER, {
      code: "ECONNRESET",
      address: "127.0.0.1",
      port: 3000,
    });

    server.emit("listening");
    server.emit("error", error);

    expect(options.onListening).toHaveBeenCalledOnce();
    expect(options.logError).toHaveBeenCalledOnce();
    expect(options.logError).toHaveBeenCalledWith(
      "Unexpected frontend HTTP server error after startup",
      error,
    );
    expect(options.markFatal).toHaveBeenCalledWith(1, "runtime");
    expect(options.disposeStartupResources).toHaveBeenCalledOnce();
    expect(lifecycle.owns(error)).toBe(true);
    await lifecycle.failure();
  });
});

describe("attachWebsocketServerErrorListener", () => {
  it("suppresses the exact HTTP-owned Error and forwards any other WSS error", () => {
    const server = http.createServer();
    const options = lifecycleOptions();
    const lifecycle = attachHttpServerLifecycle(server, options);
    const onUnexpectedError = vi.fn();
    const websocketServer = new EventEmitter();
    attachWebsocketServerErrorListener(websocketServer, {
      isOwned: lifecycle.owns,
      onUnexpectedError,
    });

    const forwarded = systemError(SECRET_MARKER, {
      code: "EADDRINUSE",
      address: "127.0.0.1",
      port: 3000,
    });
    server.emit("error", forwarded);
    websocketServer.emit("error", forwarded);

    expect(options.logError).toHaveBeenCalledOnce();
    expect(onUnexpectedError).not.toHaveBeenCalled();

    const independent = systemError("wss-only", {
      code: "EADDRINUSE",
      address: "127.0.0.1",
      port: 3000,
    });
    websocketServer.emit("error", independent);

    expect(onUnexpectedError).toHaveBeenCalledOnce();
    expect(onUnexpectedError).toHaveBeenCalledWith(independent);
    expect(options.logError).toHaveBeenCalledOnce();
  });

  it("exports identity mark helpers for issue 1234", () => {
    const error = new Error("owned-elsewhere");
    expect(isHttpServerErrorOwned(error)).toBe(false);
    markHttpServerErrorOwned(error);
    expect(isHttpServerErrorOwned(error)).toBe(true);
  });
});

describe("occupied ephemeral port child process", () => {
  it("exits 1 with one controlled EADDRINUSE line and no backend connection", async () => {
    const reservation = net.createServer();
    const fakeBackend = net.createServer();
    let child: ChildProcess | undefined;
    let configPath: string | undefined;
    let killedByHarness = false;
    let stdout = "";
    let stderr = "";
    let backendConnections = 0;

    const output = () => `${stdout}\n${stderr}`;

    try {
      const reserved = await listen(reservation);
      fakeBackend.on("connection", () => {
        backendConnections += 1;
      });
      const backendAddress = await listenOnLoopback(fakeBackend);
      configPath = await mkdtemp(path.join(tmpdir(), "nzbdav-1246-"));

      const childEnv: NodeJS.ProcessEnv = {
        ...process.env,
        NODE_ENV: "development",
        PORT: String(reserved.port),
        NO_COLOR: "1",
        FRONTEND_BACKEND_API_KEY: DUMMY_API_KEY,
        BACKEND_URL: `http://127.0.0.1:${backendAddress.port}`,
        CONFIG_PATH: configPath,
      };
      delete childEnv["NODE_OPTIONS"];

      child = spawn(process.execPath, ["--import", "tsx", "server.ts"], {
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

      const result = await waitForExit(child, 45_000, output);

      expect(killedByHarness).toBe(false);
      expect(result.code).toBe(1);
      expect(result.signal).toBeNull();

      const combined = output();
      expect(combined.match(/Frontend server could not start/g)).toHaveLength(1);
      expect(combined).toContain("EADDRINUSE");
      expect(combined).toContain(String(reserved.port));
      expect(combined).toContain("address ");
      expect(combined).toContain("Stop the other listener or set PORT to an available port");
      expect(combined).not.toContain("Unhandled 'error' event");
      expect(combined).not.toContain("at Server.setupListenHandle");
      expect(combined).not.toContain(SECRET_MARKER);
      expect(combined).not.toContain(DUMMY_API_KEY);
      expect(combined).not.toContain("Frontend server listening");
      expect(combined).not.toContain("Backend websocket connected");
      expect(combined).not.toContain("Backend websocket reconnected");
      expect(combined).not.toContain("Waiting for backend to start");
      expect(combined).not.toContain("Could not connect to backend websocket");
      expect(backendConnections).toBe(0);
    } finally {
      if (child && child.exitCode === null && child.signalCode === null) {
        killedByHarness = true;
        child.kill("SIGKILL");
        await waitForExit(child, 5_000, output).catch(() => undefined);
      }
      child?.stdout?.destroy();
      child?.stderr?.destroy();
      await Promise.allSettled([closeServer(reservation), closeServer(fakeBackend)]);
      if (configPath) {
        await rm(configPath, { recursive: true, force: true });
      }
    }
    expect(killedByHarness).toBe(false);
  }, 60_000);
});
