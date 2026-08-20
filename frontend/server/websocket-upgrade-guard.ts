import type { RequestHandler } from "express";
import { logger } from "./logger.js";
import { clientErrorKey, shouldLogClientError } from "./request-log-throttle.js";

// The WebSocketServer (see server.ts) only intercepts HTTP Upgrade requests,
// so this handler only ever sees plain HTTP on the socket path — typically a
// reverse proxy that is not forwarding Upgrade/Connection headers (which also
// breaks live UI updates), an uptime probe, or a browser tab opened on /ws.
// Answer 426 instead of letting React Router dump a "No route matches /ws"
// stack; throttle the warning so a retrying client cannot flood the log.
export const websocketUpgradeGuard: RequestHandler = (req, res, _next) => {
  const client = req.ip ?? req.socket.remoteAddress ?? "unknown";
  const { log, suppressed } = shouldLogClientError(clientErrorKey(req.method, 426, "/ws", client));
  if (log) {
    logger.warn(
      `Rejected plain HTTP ${req.method} on the WebSocket endpoint from ${client}; ` +
        "this endpoint only accepts WebSocket upgrade requests. If this came from the web UI, " +
        "the reverse proxy in front is not forwarding Upgrade/Connection headers " +
        "(see docs/configuration/url-base.md)." +
        (suppressed > 0 ? ` (+${suppressed} similar suppressed)` : ""),
    );
  }
  res
    .status(426)
    .setHeader("Upgrade", "websocket")
    .type("text/plain")
    .send("This endpoint only accepts WebSocket connections.\n");
};
