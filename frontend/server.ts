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

// NZBDAV_URL_BASE (or bare URL_BASE as a fallback) controls the sub-path the
// app is mounted under (e.g. "/nzbdav"). Mirror of normalizeUrlBase in
// app/utils/url-base.ts (this file is compiled by tsc into dist-node without
// the app graph) — keep them in sync; url-base.test.ts asserts parity. The
// Vite build bakes the same value into the client bundle at build time; the
// runtime env var here mounts middleware at the matching prefix, and the
// baked-vs-runtime guard below refuses to start when the two halves disagree.
const SAFE_URL_BASE = /^[A-Za-z0-9._~\-/]+$/;
function normalizeUrlBase(raw: string | undefined): string {
  if (!raw) return "";
  const trimmed = raw.trim();
  if (trimmed === "" || trimmed === "/") return "";
  if (!SAFE_URL_BASE.test(trimmed)) {
    throw new Error(
      `Invalid URL base ${JSON.stringify(raw)}: only letters, digits, ".", "_", "~", "-", and "/" are allowed`,
    );
  }
  const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  return withLeading.replace(/\/+$/, "");
}
const URL_BASE = normalizeUrlBase(process.env.NZBDAV_URL_BASE ?? process.env.URL_BASE);

// The server build exports the value baked into the bundle at build time.
// Mounting under a different prefix than the bundle was built for produces a
// fully broken app (assets and basename at one prefix, routes at another), so
// fail fast with an actionable message instead.
function assertUrlBaseMatchesBuild(bakedUrlBase: unknown): void {
  if (typeof bakedUrlBase !== "string" || bakedUrlBase === URL_BASE) return;
  logger.error(
    `NZBDAV_URL_BASE mismatch: this build was compiled with ${JSON.stringify(bakedUrlBase)} `
    + `but the runtime environment says ${JSON.stringify(URL_BASE)}. The two halves of the `
    + `setting must match — rebuild with --build-arg NZBDAV_URL_BASE=${URL_BASE || '""'} or `
    + `set NZBDAV_URL_BASE=${bakedUrlBase || '""'} at runtime. See docs/configuration/url-base.md.`,
  );
  process.exit(1);
}

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

// Frontend-local healthcheck. Registered BEFORE request logging and the React
// Router catch-all so probes bypass SSR and stay quiet in access logs.
// Adopted from elfhosted/rebased-v3.
// Served at the bare root regardless of URL_BASE so container healthchecks
// have a stable URL, and additionally under URL_BASE for reverse-proxy probes.
app.get("/healthz", (_req, res) => {
  res.status(200).type("text/plain").send("ok");
});
if (URL_BASE) {
  app.get(`${URL_BASE}/healthz`, (_req, res) => {
    res.status(200).type("text/plain").send("ok");
  });
}

app.use(requestLogger);

// Path-sensitive middleware (security headers, static assets, and the app
// module) goes on a sub-router mounted under URL_BASE, so it inherits the
// prefix without per-middleware path arithmetic. Inside the router `req.path`
// is stripped of URL_BASE — existing path-prefix checks (`/api`, `/nzbs`, …,
// including securityHeadersMiddleware's proxied-path exemption) work
// unchanged — while the React Router request handler reads `req.originalUrl`
// and therefore still sees the full, basename-prefixed path.
const router = express.Router();
router.use(securityHeadersMiddleware);

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
  // Vite's dev middlewares handle their own `base` prefix, so they stay on the
  // root app; only the SSR module goes on the URL_BASE-mounted router.
  app.use(viteDevServer.middlewares);
  router.use(async (req, res, next) => {
    try {
      const serverModule = await viteDevServer.ssrLoadModule("./server/app.ts");
      assertUrlBaseMatchesBuild(serverModule.bakedUrlBase);
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
  router.use(
    "/assets",
    express.static("build/client/assets", { immutable: true, maxAge: "1y" }),
  );
  router.use(express.static("build/client", { maxAge: "1h" }));
  const serverModule = await import(BUILD_PATH);
  assertUrlBaseMatchesBuild(serverModule.bakedUrlBase);
  router.use(serverModule.app);
  setServerModule(serverModule);
}

// Mount the router. When URL_BASE is empty we mount at root (no prefix).
// Otherwise we mount under URL_BASE and redirect the bare host root so users
// hitting `/` land in the right place.
if (URL_BASE) {
  app.get("/", (_req, res) => res.redirect(`${URL_BASE}/`));
  app.use(URL_BASE, router);
} else {
  app.use(router);
}

// Create both the http and websocket servers
const server = http.createServer(app);
// Allow long-lived proxied API calls (Usenet speed tests can run for many
// minutes on large data budgets). Node defaults are 5 minutes / 60s headers.
const LONG_RUNNING_REQUEST_TIMEOUT_MS = 3 * 60 * 60 * 1000; // 3 hours
server.requestTimeout = LONG_RUNNING_REQUEST_TIMEOUT_MS;
server.headersTimeout = LONG_RUNNING_REQUEST_TIMEOUT_MS + 1000;
setWebsocketServer(new WebSocketServer({ server, path: `${URL_BASE}/ws`, maxPayload: 64 * 1024 }));

// Begin listening for connections
server.listen(PORT, () => {
  logger.info(`Frontend server listening on http://localhost:${PORT}${URL_BASE}`);
});
