import "react-router";
import { createRequestHandler } from "@react-router/express";
import express from "express";
import { ipKeyGenerator, rateLimit } from "express-rate-limit";
import { createProxyMiddleware } from "http-proxy-middleware";
import { websocketServer } from "./websocket.server";
import { safeDecodePath, shouldProxyToBackend } from "./proxy-path";
import { logger } from "./logger";
import { authMiddleware } from "~/auth/auth-middleware.server";
import { setApiKeyForAuthenticatedRequests } from "./inject-api-key.server";
import {
  BACKEND_FAILURE_LOG_THROTTLE_MS,
  isExpectedBackendConnectionError,
  isWithinBackendStartupGrace,
} from "./startup-grace";
import { applyCanonicalForwardedHeaders } from "./forwarded-headers";
import { backendProxyTimeoutOptions } from "./backend-proxy-options";
import { handleBackendProxyResponse } from "./backend-proxy-response";
import { oidcRouter } from "./oidc-routes";

export const app = express();
app.disable("x-powered-by");
export const initializeWebsocketServer = websocketServer.initialize;

const trustProxy =
  process.env.TRUST_PROXY === "1"
  || process.env.TRUST_PROXY?.toLowerCase() === "true"
  || process.env.TRUST_PROXY?.toLowerCase() === "yes";
if (trustProxy) {
  // Opt-in: honor X-Forwarded-* from the reverse proxy in front of this container.
  // Required for correct public scheme/host when rewriting headers to the backend.
  app.set("trust proxy", 1);
}

let loggedStartupWait = false;
let lastProxyFailureLogAt = 0;

function logProxyFailure(message: string, error: unknown) {
  const now = Date.now();
  if (isExpectedBackendConnectionError(error) && isWithinBackendStartupGrace()) {
    if (!loggedStartupWait) {
      logger.info("Waiting for backend to start...");
      loggedStartupWait = true;
      lastProxyFailureLogAt = now;
    }
    return;
  }

  if (now - lastProxyFailureLogAt >= BACKEND_FAILURE_LOG_THROTTLE_MS) {
    logger.warn(message, error);
    lastProxyFailureLogAt = now;
  }
}

// Proxy all webdav and api requests to the backend.
// Long POSTs (e.g. /api/benchmark-usenet-connection) rely on proxyTimeout here
// plus server.requestTimeout/headersTimeout in server.ts — not httpxy's inbound
// `timeout` option, which leaks socket listeners under keep-alive (#486).
const forwardToBackend = createProxyMiddleware({
  target: process.env.BACKEND_URL,
  changeOrigin: true,
  ...backendProxyTimeoutOptions,
  on: {
    proxyReq: (proxyReq, req) => {
      applyCanonicalForwardedHeaders(proxyReq, req as express.Request, { trustProxy });
    },
    error: (error, req, res) => {
      logProxyFailure(
        `Backend proxy failed for ${req.method ?? "UNKNOWN"} ${req.url ?? "unknown URL"}`,
        error,
      );
      if ("writeHead" in res && !res.headersSent) {
        res.writeHead(502, { "Content-Type": "text/plain" });
        res.end("Bad Gateway");
      }
    },
    proxyRes: handleBackendProxyResponse,
  },
});

const credentialPostPaths = new Set([
  "/login",
  "/login.data",
  "/onboarding",
  "/onboarding.data",
]);
const oidcGetPaths = new Set([
  "/auth/oidc/login",
  "/auth/oidc/callback",
]);
const credentialRateLimiter = rateLimit({
  windowMs: 15 * 60 * 1000,
  limit: 20,
  standardHeaders: true,
  legacyHeaders: false,
  keyGenerator: (req) => {
    // When TRUST_PROXY is set, req.ip reflects the client behind the reverse proxy.
    // Default stays socket-IP keyed (spoof-safe without a trusted proxy story).
    if (trustProxy && req.ip) return ipKeyGenerator(req.ip);
    const remoteAddress = req.socket.remoteAddress;
    return remoteAddress ? ipKeyGenerator(remoteAddress) : "unknown";
  },
  skip: (req) => {
    // Malformed encoding is not a credential path; must not throw (see #217).
    const decodedPath = safeDecodePath(req.path);
    if (decodedPath === null) return true;

    const method = req.method.toUpperCase();
    return !(
      (method === "POST" && credentialPostPaths.has(decodedPath))
      || (method === "GET" && oidcGetPaths.has(decodedPath))
    );
  },
  handler: (req, res, _next, options) => {
    logger.warn(
      `Credential rate limit exceeded for ${req.ip ?? "unknown IP"} on ${req.path}`,
    );
    res.status(options.statusCode).send(options.message);
  },
});

app.use(async (req, res, next) => {
  if (shouldProxyToBackend(req.method, req.path)) {
    await setApiKeyForAuthenticatedRequests(req);
    return forwardToBackend(req, res, next);
  }
  next();
});

// Limit credential attempts without throttling WebDAV, API, or regular UI traffic.
app.use(credentialRateLimiter);

// OIDC endpoints must remain public so the provider can complete the callback.
app.use(oidcRouter);

// Require authentication for all React Router routes
app.use(authMiddleware);

// Let frontend handle all other requests
app.use(
  createRequestHandler({
    build: () => import("virtual:react-router/server-build"),
  }),
);
