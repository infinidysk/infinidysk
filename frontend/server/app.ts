import "react-router";
import { createRequestHandler } from "@react-router/express";
import type { ServerBuild } from "react-router";
import express from "express";
import { ipKeyGenerator, rateLimit } from "express-rate-limit";
import { createProxyMiddleware } from "http-proxy-middleware";
import { websocketServer } from "./websocket.server";
import { isBackendApiDocsPath, safeDecodePath, shouldProxyToBackend } from "./proxy-path";
import { logger } from "./logger";
import { authMiddleware } from "~/auth/auth-middleware.server";
import { getSessionUser, isAuthenticated } from "~/auth/authentication.server";
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
import { URL_BASE } from "~/utils/url-base";

export const app = express();

// The URL base this bundle was compiled with (Vite bakes it via __URL_BASE__).
// server.ts compares this against the runtime env var and refuses to start on
// a mismatch — the two halves of the setting cannot work independently.
export const bakedUrlBase = URL_BASE;
app.disable("x-powered-by");
export const initializeWebsocketServer = websocketServer.initialize;

const trustProxy =
  process.env["TRUST_PROXY"] === "1" ||
  process.env["TRUST_PROXY"]?.toLowerCase() === "true" ||
  process.env["TRUST_PROXY"]?.toLowerCase() === "yes";
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
  target: process.env["BACKEND_URL"]!,
  changeOrigin: false,
  selfHandleResponse: true,
  ...backendProxyTimeoutOptions,
  on: {
    proxyReq: (proxyReq, req) => {
      applyCanonicalForwardedHeaders(proxyReq, req as express.Request, {
        trustProxy,
        pathBase: URL_BASE,
      });
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

const credentialPostPaths = new Set(["/login", "/login.data", "/onboarding", "/onboarding.data"]);
const oidcGetPaths = new Set(["/auth/oidc/login", "/auth/oidc/callback"]);
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
      (method === "POST" && credentialPostPaths.has(decodedPath)) ||
      (method === "GET" && oidcGetPaths.has(decodedPath)) ||
      (method === "GET" && decodedPath === "/metrics")
    );
  },
  handler: (req, res, _next, options) => {
    logger.warn(`Credential rate limit exceeded for ${req.ip ?? "unknown IP"} on ${req.path}`);
    res.status(options.statusCode).send(options.message);
  },
});

// Limit credential attempts and protected metrics scrapes before the early backend proxy.
app.use(credentialRateLimiter);

app.use(async (req, res, next) => {
  if (shouldProxyToBackend(req.method, req.path)) {
    const decodedPath = safeDecodePath(req.path);
    if (decodedPath === "/metrics" && !(await isAuthenticated(req))) {
      res.status(401).type("text/plain").send("Metrics authentication required.");
      return;
    }

    await setApiKeyForAuthenticatedRequests(req);

    if (
      decodedPath !== null &&
      req.method.toUpperCase() === "POST" &&
      (decodedPath === "/api/delete-webdav-item" ||
        decodedPath.startsWith("/api/delete-webdav-item/"))
    ) {
      const user = await getSessionUser(req);
      if (user?.role === "readonly") {
        res.status(403).json({
          status: false,
          error: "Read-only users cannot delete files.",
        });
        return;
      }
    }

    return forwardToBackend(req, res, next);
  }
  next();
});

// OIDC endpoints must remain public so the provider can complete the callback.
app.use(oidcRouter);

// Require authentication for all React Router routes
app.use(authMiddleware);

// API documentation is opt-in on the backend, and is protected by the frontend
// session when accessed through the public UI port. Do not move this beside the
// early backend proxy above: that middleware intentionally runs before auth for
// WebDAV and API clients.
app.use(async (req, res, next) => {
  if (isBackendApiDocsPath(req.path)) {
    await setApiKeyForAuthenticatedRequests(req);
    return forwardToBackend(req, res, next);
  }
  next();
});

// Let frontend handle all other requests
app.use(
  createRequestHandler({
    build: () => import("virtual:react-router/server-build") as unknown as Promise<ServerBuild>,
  }),
);
