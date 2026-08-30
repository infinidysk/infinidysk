import { logger } from "./logger.js";
import { shouldLogClientError } from "./request-log-throttle.js";

export const MAX_WEBSOCKET_PAYLOAD_BYTES = 64 * 1024;

const EXPECTED_TRANSPORT_CODES = new Set(["ECONNRESET", "EPIPE", "ETIMEDOUT", "ECONNABORTED"]);

export type BrowserWebsocketErrorContext = Readonly<{
  remote: string;
  isAuthenticated: () => boolean;
}>;

export type BrowserWebsocketErrorReporter = (
  error: unknown,
  context: BrowserWebsocketErrorContext,
) => void;

export type BrowserWebsocketErrorTarget = {
  on(event: "error", listener: (error: Error) => void): unknown;
};

export function errorCode(error: unknown): string | null {
  if (!error || typeof error !== "object" || !("code" in error)) return null;
  const code = (error as { code?: unknown }).code;
  return typeof code === "string" ? code : null;
}

function authPhase(context: BrowserWebsocketErrorContext): "authenticated" | "pre-auth" {
  return context.isAuthenticated() ? "authenticated" : "pre-auth";
}

function logExpectedBrowserWebsocketWarning(message: string, throttleKey: string): void {
  const { log, suppressed } = shouldLogClientError(throttleKey);
  if (!log) return;
  if (suppressed > 0) {
    logger.warn(`${message} (+${suppressed} similar suppressed)`);
    return;
  }
  logger.warn(message);
}

export function reportBrowserSocketError(
  error: unknown,
  context: BrowserWebsocketErrorContext,
): void {
  const code = errorCode(error);
  const phase = authPhase(context);

  if (code === "WS_ERR_UNSUPPORTED_MESSAGE_LENGTH") {
    logExpectedBrowserWebsocketWarning(
      `Browser websocket rejected oversized frame from ${context.remote} during ${phase} ` +
        `(limit ${MAX_WEBSOCKET_PAYLOAD_BYTES} bytes)`,
      `websocket:${phase}:oversized`,
    );
    return;
  }

  if (code?.startsWith("WS_ERR_") || EXPECTED_TRANSPORT_CODES.has(code ?? "")) {
    logExpectedBrowserWebsocketWarning(
      `Browser websocket peer error from ${context.remote} during ${phase} (${code})`,
      `websocket:${phase}:${code}`,
    );
    return;
  }

  logger.error(`Unexpected browser websocket error from ${context.remote} during ${phase}`, error);
}

export function attachBrowserWebsocketErrorListener(
  socket: BrowserWebsocketErrorTarget,
  context: BrowserWebsocketErrorContext,
  report: BrowserWebsocketErrorReporter = reportBrowserSocketError,
): void {
  socket.on("error", (error) => {
    report(error, context);
  });
}
