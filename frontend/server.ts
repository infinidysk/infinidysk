import compression from "compression";
import express from "express";
import http from "http";
import { WebSocketServer } from "ws";
import { shouldCompressResponse } from "./server/compression-filter.js";
import { logger, requestLogger } from "./server/logger.js";
import { securityHeadersMiddleware } from "./server/security-headers.js";
import {
  formatBackendUnavailableReason,
  isExpectedBackendUnavailableError,
  isWithinBackendStartupGrace,
  shouldEmitThrottledBackendUnavailableLog,
} from "./server/startup-grace.js";

// Short-circuit the type-checking of the built output.
const BUILD_PATH = "../build/server/index.js";
const DEVELOPMENT = process.env.NODE_ENV === "development";
const PORT = Number.parseInt(process.env.PORT || "3000");

// Keep the frontend alive when the backend is slow. SSR loaders fetch the
// backend; when those fetches reject and a loader doesn't catch them, the
// rejection can become an unhandledRejection that Node terminates on by
// default (v15+). Logging without crashing keeps /healthz, /assets, and
// websockets up while the affected SSR request returns an error page.
// Adopted from elfhosted/rebased-v3.
//
// Deliberately do not hook uncaughtException: that fires for fatal errors
// where restarting the process is the right answer.
process.on("unhandledRejection", (reason) => {
  if (isExpectedBackendUnavailableError(reason)) {
    if (isWithinBackendStartupGrace()) return;
    if (shouldEmitThrottledBackendUnavailableLog()) {
      logger.warn(
        `Backend unreachable during SSR. Reason: ${formatBackendUnavailableReason(reason)}`,
      );
    }
    return;
  }
  logger.error("Unhandled promise rejection:", reason);
});

// Initialize the express app
const app = express();
app.use(
  compression({
    // Skip WebDAV/media/API and React Router streamed bodies (see shouldCompressResponse).
    filter: shouldCompressResponse,
  }),
);
app.disable("x-powered-by");
app.use(securityHeadersMiddleware);

// Frontend-local healthcheck. Registered BEFORE request logging and the React
// Router catch-all so probes bypass SSR and stay quiet in access logs.
// Adopted from elfhosted/rebased-v3.
app.get("/healthz", (_req, res) => {
  res.status(200).type("text/plain").send("ok");
});

app.use(requestLogger);

// Initialize the websocket server as soon as both it and the server-module are ready
let _serverModule: any = null;
let _websocketServer: WebSocketServer | null = null;
const setWebsocketServer = (websocketServer: WebSocketServer) => {
  if (_websocketServer != null) return;
  if (_serverModule != null) _serverModule.initializeWebsocketServer(websocketServer);
  _websocketServer = websocketServer;
}
const setServerModule = (serverModule: any) => {
  if (_serverModule != null) return;
  if (_websocketServer != null) serverModule.initializeWebsocketServer(_websocketServer);
  _serverModule = serverModule;
}

// Handle development vs production
if (DEVELOPMENT) {
  logger.info("Starting frontend development server");
  const viteDevServer = await import("vite").then((vite) =>
    vite.createServer({
      server: { middlewareMode: true },
    }),
  );
  app.use(viteDevServer.middlewares);
  app.use(async (req, res, next) => {
    try {
      const serverModule = await viteDevServer.ssrLoadModule("./server/app.ts");
      setServerModule(serverModule);
      return await serverModule.app(req, res, next);
    } catch (error) {
      if (typeof error === "object" && error instanceof Error) {
        viteDevServer.ssrFixStacktrace(error);
      }
      next(error);
    }
  });
} else {
  logger.info("Starting frontend production server");
  app.use(
    "/assets",
    express.static("build/client/assets", { immutable: true, maxAge: "1y" }),
  );
  app.use(express.static("build/client", { maxAge: "1h" }));
  const serverModule = await import(BUILD_PATH);
  app.use(serverModule.app);
  setServerModule(serverModule);
}

// Create both the http and websocket servers
const server = http.createServer(app);
// Allow long-lived proxied API calls (Usenet speed tests can run for many
// minutes on large data budgets). Node defaults are 5 minutes / 60s headers.
const LONG_RUNNING_REQUEST_TIMEOUT_MS = 3 * 60 * 60 * 1000; // 3 hours
server.requestTimeout = LONG_RUNNING_REQUEST_TIMEOUT_MS;
server.headersTimeout = LONG_RUNNING_REQUEST_TIMEOUT_MS + 1000;
setWebsocketServer(new WebSocketServer({ server, path: "/ws", maxPayload: 64 * 1024 }));

// Begin listening for connections
server.listen(PORT, () => {
  logger.info(`Frontend server listening on http://localhost:${PORT}`);
});
