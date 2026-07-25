import type { IncomingMessage, ServerResponse } from "node:http";
import { logger } from "./logger";

/**
 * Closes a proxied backend response downstream.
 *
 * The backend aborts a streaming response mid-body when it discovers the data it
 * promised is not there. Ending the downstream response on that abort keeps the
 * client from hanging, but a client that gets a clean end treats the bytes it
 * already has as the whole file — the same silent truncation the backend went out
 * of its way to refuse. So an incomplete upstream body destroys the downstream
 * response instead, and only a complete one ends normally.
 */
export function handleBackendProxyResponse(
  proxyRes: IncomingMessage,
  req: IncomingMessage,
  res: ServerResponse,
): void {
  proxyRes.on("close", () => {
    if (res.writableEnded) return;

    // Node sets `complete` only once the whole upstream message has been read,
    // so it covers both a declared Content-Length that was not met and a
    // chunked body that never got its terminating chunk.
    if (proxyRes.complete) {
      res.end();
      return;
    }

    logger.warn(
      `Backend response for ${req.method ?? "?"} ${req.url ?? "?"} ended before its body was `
      + "complete; aborting the client transfer instead of ending it successfully.",
    );
    res.destroy();
  });
}
