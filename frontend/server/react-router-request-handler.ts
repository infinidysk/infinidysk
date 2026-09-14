import { createRequestHandler as createReactRouterRequestHandler } from "react-router";
import type { ServerBuild } from "react-router";
import {
  createReadableStreamFromReadable,
  writeReadableStreamToWritable,
} from "@react-router/node";
import type express from "express";
import { firstForwardedValue, splitHostPort } from "./forwarded-headers";

type Build = ServerBuild | (() => ServerBuild | Promise<ServerBuild>);

/**
 * Drop-in replacement for @react-router/express's createRequestHandler. That
 * adapter derives the SSR request's port by naively splitting the raw Host and
 * X-Forwarded-Host headers on ":", which misparses bracketed IPv6 literals
 * (e.g. "[2001:db8::1]:8443" -> treats "db8" as the port, fails, and drops
 * 8443 entirely). That breaks the action CSRF origin check for reverse-proxied
 * IPv6 hosts on a non-default port. This mirrors the adapter but reuses the
 * bracket-aware splitHostPort helper for the port fallback.
 */
export function createRequestHandler({
  build,
  mode = process.env["NODE_ENV"],
  resolveCanonicalOrigin,
}: {
  build: Build;
  mode?: string;
  resolveCanonicalOrigin?: (req: express.Request) => Promise<string | null | undefined>;
}): express.RequestHandler {
  const handleRequest = createReactRouterRequestHandler(build, mode);
  return async (req, res, next) => {
    try {
      const canonicalOrigin = await resolveCanonicalOrigin?.(req);
      if (canonicalOrigin === null) {
        await sendRemixResponse(res, new Response("Bad Request", { status: 400 }));
        return;
      }
      const response = await handleRequest(createRemixRequest(req, res, canonicalOrigin));
      await sendRemixResponse(res, response);
    } catch (error) {
      next(error);
    }
  };
}

/** Exported for direct regression coverage of the bracket-aware port fallback. */
export function resolvePort(req: express.Request): string | undefined {
  const trustProxy = req.app?.get("trust proxy fn") as
    ((address: string, hop: number) => boolean) | undefined;
  const trustsImmediateProxy = trustProxy?.(req.socket?.remoteAddress ?? "", 0) ?? false;
  const forwardedHost = trustsImmediateProxy
    ? firstForwardedValue(req.get("X-Forwarded-Host"))
    : undefined;
  const forwardedPort = forwardedHost ? splitHostPort(forwardedHost)[1] : undefined;
  if (forwardedPort) return forwardedPort;

  const rawHost = req.get("host");
  return rawHost ? splitHostPort(rawHost)[1] : undefined;
}

/** Exported to verify canonical-origin handling independently of React Router internals. */
export function resolveRequestUrl(req: express.Request, canonicalOrigin?: string): URL {
  const port = resolvePort(req);
  const resolvedHost = `${req.hostname.split(/[\\/?#@]/)[0] || "localhost"}${port ? `:${port}` : ""}`;
  const origin = new URL(canonicalOrigin ?? `${req.protocol}://${resolvedHost}`).origin;
  const requestTarget = req.originalUrl.startsWith("/") ? req.originalUrl : `/${req.originalUrl}`;
  return new URL(`${origin}${requestTarget}`);
}

function createRemixHeaders(requestHeaders: express.Request["headers"]): Headers {
  const headers = new Headers();
  for (const [key, values] of Object.entries(requestHeaders)) {
    if (!values) continue;
    if (Array.isArray(values)) {
      for (const value of values) headers.append(key, value);
    } else {
      headers.set(key, values);
    }
  }
  return headers;
}

function createRemixRequest(
  req: express.Request,
  res: express.Response,
  canonicalOrigin?: string,
): Request {
  const url = resolveRequestUrl(req, canonicalOrigin);

  let controller: AbortController | null = new AbortController();
  const init: RequestInit = {
    method: req.method,
    headers: createRemixHeaders(req.headers),
    signal: controller.signal,
  };

  res.on("finish", () => (controller = null));
  res.on("close", () => controller?.abort());

  if (req.method !== "GET" && req.method !== "HEAD") {
    init.body = createReadableStreamFromReadable(req);
    (init as { duplex?: "half" }).duplex = "half";
  }

  return new Request(url.href, init);
}

async function sendRemixResponse(res: express.Response, nodeResponse: Response): Promise<void> {
  if (isResponseClosed(res)) {
    await nodeResponse.body?.cancel();
    return;
  }
  res.statusMessage = nodeResponse.statusText;
  res.status(nodeResponse.status);
  for (const [key, value] of nodeResponse.headers.entries()) res.append(key, value);
  if (nodeResponse.headers.get("Content-Type")?.match(/text\/event-stream/i)) res.flushHeaders();
  if (nodeResponse.body) {
    try {
      await writeReadableStreamToWritable(nodeResponse.body, res);
    } catch (error) {
      if (isResponseClosed(res)) return;
      throw error;
    }
  } else {
    res.end();
  }
}

function isResponseClosed(res: express.Response): boolean {
  return res.destroyed || res.writableEnded;
}
