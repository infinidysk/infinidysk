import type express from "express";

/**
 * Strip client-supplied forwarded headers and set canonical values from Express.
 * Prevents spoofed X-Forwarded-* from being laundered through the loopback backend.
 */
export function applyCanonicalForwardedHeaders(
  proxyReq: { removeHeader: (name: string) => void; setHeader: (name: string, value: string) => void },
  req: express.Request,
  options?: { trustProxy?: boolean; pathBase?: string },
): void {
  for (const header of [
    "x-forwarded-for",
    "x-forwarded-host",
    "x-forwarded-proto",
    "x-forwarded-port",
    "x-forwarded-prefix",
    "forwarded",
  ]) {
    proxyReq.removeHeader(header);
  }

  proxyReq.setHeader("X-Forwarded-Proto", req.protocol);
  const host = req.get("host");
  if (host) proxyReq.setHeader("X-Forwarded-Host", host);

  const trustProxy = options?.trustProxy ?? false;
  const clientIp = trustProxy ? req.ip : req.socket.remoteAddress;
  if (clientIp) proxyReq.setHeader("X-Forwarded-For", clientIp);
  if (options?.pathBase) proxyReq.setHeader("X-Forwarded-Prefix", options.pathBase);
}
