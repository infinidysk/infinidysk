import type { Server as HttpServer } from "node:http";

type ListenSystemError = Error & {
  code?: unknown;
  address?: unknown;
  port?: unknown;
  syscall?: unknown;
};

type ListenErrorContext = Readonly<{
  code: string;
  address: string;
  port: number;
}>;

export type FatalPhase = "startup" | "runtime";

export type HttpServerLifecycleOptions = Readonly<{
  configuredPort: number;
  logError: (message: string, detail?: Error) => void;
  onListening: () => void;
  disposeStartupResources: () => Promise<void>;
  markFatal: (exitCode: number, phase: FatalPhase) => void;
}>;

export type HttpServerLifecycle = Readonly<{
  owns: (error: Error) => boolean;
  failure: () => Promise<void> | null;
}>;

export type WebsocketServerErrorTarget = {
  on(event: "error", listener: (error: Error) => void): unknown;
};

export type WebsocketServerErrorListenerOptions = Readonly<{
  isOwned: (error: Error) => boolean;
  onUnexpectedError: (error: Error) => void;
}>;

const ownedHttpServerErrors = new WeakSet<Error>();

export function markHttpServerErrorOwned(error: Error): void {
  ownedHttpServerErrors.add(error);
}

export function isHttpServerErrorOwned(error: Error): boolean {
  return ownedHttpServerErrors.has(error);
}

function safeToken(value: unknown, fallback: string): string {
  if (typeof value !== "string") return fallback;
  if (value.length === 0 || value.length > 128 || /[\r\n\0]/.test(value)) return fallback;
  return value;
}

function listenPort(value: unknown, configuredPort: number): number {
  if (typeof value === "number" && Number.isInteger(value) && value >= 0 && value <= 65535) {
    return value;
  }
  return configuredPort;
}

function listenErrorContext(error: Error, configuredPort: number): ListenErrorContext {
  const systemError = error as ListenSystemError;
  return {
    code: safeToken(systemError.code, "UNKNOWN"),
    address: safeToken(systemError.address, "default interface"),
    port: listenPort(systemError.port, configuredPort),
  };
}

export function formatStartupListenError(error: Error, configuredPort: number): string {
  const { code, address, port } = listenErrorContext(error, configuredPort);
  const endpoint = `address ${address}, port ${port}, code ${code}`;

  switch (code) {
    case "EADDRINUSE":
      return (
        `Frontend server could not start (${endpoint}): the address is already in use. ` +
        "Stop the other listener or set PORT to an available port."
      );
    case "EACCES":
      return (
        `Frontend server could not start (${endpoint}): permission to bind was denied. ` +
        "Use an unprivileged PORT or grant the runtime permission to bind it."
      );
    case "EADDRNOTAVAIL":
      return (
        `Frontend server could not start (${endpoint}): the local address is unavailable. ` +
        "Correct the bind configuration and restart the frontend."
      );
    case "EMFILE":
    case "ENFILE":
      return (
        `Frontend server could not start (${endpoint}): the file-descriptor limit was reached. ` +
        "Close leaked descriptors or raise the limit before restarting."
      );
    default:
      return (
        `Frontend server could not start (${endpoint}). ` +
        "Check the bind configuration and operating-system limits, then restart."
      );
  }
}

export function attachHttpServerLifecycle(
  server: HttpServer,
  options: HttpServerLifecycleOptions,
): HttpServerLifecycle {
  let phase: "starting" | "listening" | "failing" = "starting";
  let failure: Promise<void> | null = null;

  server.on("error", (error: Error) => {
    if (error instanceof Error) {
      markHttpServerErrorOwned(error);
    }
    if (phase === "failing") return;

    const fatalPhase: FatalPhase = phase === "starting" ? "startup" : "runtime";
    phase = "failing";

    if (fatalPhase === "startup") {
      options.logError(formatStartupListenError(error, options.configuredPort));
    } else {
      options.logError("Unexpected frontend HTTP server error after startup", error);
    }

    options.markFatal(1, fatalPhase);
    if (failure === null) {
      try {
        failure = Promise.resolve(options.disposeStartupResources()).catch(() => {
          return;
        });
      } catch {
        failure = Promise.resolve();
      }
    }
  });

  server.once("listening", () => {
    if (phase !== "starting") return;
    phase = "listening";
    options.onListening();
  });

  return {
    owns: isHttpServerErrorOwned,
    failure: () => failure,
  };
}

export function attachWebsocketServerErrorListener(
  websocketServer: WebsocketServerErrorTarget,
  options: WebsocketServerErrorListenerOptions,
): void {
  let unexpectedFailureClaimed = false;
  websocketServer.on("error", (error: Error) => {
    if (error instanceof Error && options.isOwned(error)) return;
    if (unexpectedFailureClaimed) return;
    unexpectedFailureClaimed = true;
    options.onUnexpectedError(error);
  });
}
