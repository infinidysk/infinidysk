import { PassThrough } from "node:stream";

import type { EntryContext, HandleErrorFunction, RouterContextProvider } from "react-router";
import { createReadableStreamFromReadable } from "@react-router/node";
import { isRouteErrorResponse, ServerRouter } from "react-router";
import { isbot } from "isbot";
import type { RenderToPipeableStreamOptions } from "react-dom/server";
import { renderToPipeableStream } from "react-dom/server";
import {
  formatBackendUnavailableReason,
  isExpectedBackendUnavailableError,
  isWithinBackendStartupGrace,
  shouldEmitThrottledBackendUnavailableLog,
} from "../server/startup-grace";
import { logger } from "../server/logger";

export const streamTimeout = 5_000;

let lastOriginRejectionLogAt = 0;
const ORIGIN_REJECTION_LOG_THROTTLE_MS = 60_000;

export function resetOriginRejectionLogThrottleForTests(): void {
  lastOriginRejectionLogAt = 0;
}

export function isActionOriginRejection(error: unknown, request: Request): boolean {
  const method = request.method.toUpperCase();
  if (method === "GET" || method === "HEAD") return false;

  const origin = request.headers.get("origin");
  if (!origin) return false;

  if (!(error instanceof Error) || error.message !== "Bad Request") return false;

  let isMismatch: boolean;
  try {
    const originUrl = new URL(origin);
    const requestUrl = new URL(request.url);
    isMismatch = originUrl.origin !== requestUrl.origin;
  } catch {
    isMismatch = true;
  }

  if (!isMismatch) return false;

  return true;
}

/**
 * Quiet expected BackendUnavailableError stacks during frontend-first Docker
 * startup. Outside the grace window, emit a throttled single-line warn (no stack).
 * Unexpected errors keep default stack logging.
 */
export const handleError: HandleErrorFunction = (error, { request }) => {
  if (request.signal.aborted) return;
  // Unwrap route error responses the same way React Router's default handler does.
  // The wrapped error is internal to ErrorResponseImpl, hence the cast.
  const routeError = isRouteErrorResponse(error) ? (error as { error?: unknown }).error : undefined;
  const unwrapped = routeError ?? error;
  if (isExpectedBackendUnavailableError(unwrapped)) {
    if (isWithinBackendStartupGrace()) return;
    if (shouldEmitThrottledBackendUnavailableLog()) {
      logger.warn(
        `Backend unreachable during SSR. Reason: ${formatBackendUnavailableReason(unwrapped)}`,
      );
    }
    return;
  }
  if (isActionOriginRejection(unwrapped, request)) {
    const now = Date.now();
    if (now - lastOriginRejectionLogAt >= ORIGIN_REJECTION_LOG_THROTTLE_MS) {
      lastOriginRejectionLogAt = now;
      const origin = request.headers.get("origin");
      logger.warn(
        `Action request origin rejected. Request URL: ${request.url}, Origin: ${origin ?? "unknown"}. If behind a reverse proxy terminating HTTPS, ensure TRUST_PROXY=1 is set.`,
      );
    }
    return;
  }
  console.error(unwrapped);
};

export default function handleRequest(
  request: Request,
  responseStatusCode: number,
  responseHeaders: Headers,
  routerContext: EntryContext,
  _loadContext: RouterContextProvider,
) {
  // https://httpwg.org/specs/rfc9110.html#HEAD
  if (request.method.toUpperCase() === "HEAD") {
    return new Response(null, {
      status: responseStatusCode,
      headers: responseHeaders,
    });
  }

  return new Promise((resolve, reject) => {
    let shellRendered = false;
    const userAgent = request.headers.get("user-agent");

    // Ensure requests from bots and SPA Mode renders wait for all content to load before responding
    // https://react.dev/reference/react-dom/server/renderToPipeableStream#waiting-for-all-content-to-load-for-crawlers-and-static-generation
    const readyOption: keyof RenderToPipeableStreamOptions =
      (userAgent && isbot(userAgent)) || routerContext.isSpaMode ? "onAllReady" : "onShellReady";

    // Abort the rendering stream after the `streamTimeout` so it has time to
    // flush down the rejected boundaries
    let timeoutId: ReturnType<typeof setTimeout> | undefined = setTimeout(
      () => abort(),
      streamTimeout + 1000,
    );

    const { pipe, abort } = renderToPipeableStream(
      <ServerRouter context={routerContext} url={request.url} />,
      {
        [readyOption]() {
          shellRendered = true;
          const body = new PassThrough({
            final(callback) {
              // Clear the timeout to prevent retaining the closure and memory leak
              clearTimeout(timeoutId);
              timeoutId = undefined;
              callback();
            },
          });
          const stream = createReadableStreamFromReadable(body);

          responseHeaders.set("Content-Type", "text/html");

          pipe(body);

          resolve(
            new Response(stream, {
              headers: responseHeaders,
              status: responseStatusCode,
            }),
          );
        },
        onShellError(error: unknown) {
          reject(error instanceof Error ? error : new Error("Unknown shell rendering error"));
        },
        onError(error: unknown) {
          responseStatusCode = 500;
          // Log streaming rendering errors from inside the shell.  Don't log
          // errors encountered during initial shell rendering since they'll
          // reject and get logged in handleDocumentRequest.
          if (shellRendered) {
            console.error(error);
          }
        },
      },
    );
  });
}
