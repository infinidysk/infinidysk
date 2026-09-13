import type express from "express";

const DEFAULT_PORT_BY_SCHEME: Record<string, string> = { http: "80", https: "443" };

/**
 * Rewrite the inbound Host header to the trusted proxy's public scheme/host/port
 * before Express or React Router see the request. @react-router/express builds the
 * SSR request URL from `req.hostname` plus a port fallback that reads the *raw*
 * Host header whenever X-Forwarded-Host omits one (the common case for a
 * default-port public origin), so a container listening on a non-default internal
 * port (e.g. :3000) leaks that port into the SSR request URL. React Router's
 * action CSRF check then compares that bogus `origin:internalPort` host against
 * the browser's plain `Origin` header and rejects the request.
 *
 * This reconstructs the public origin explicitly from X-Forwarded-Proto,
 * X-Forwarded-Host, and X-Forwarded-Port (in that trust order) and rewrites Host
 * to match — never falling back to the container's own Host header port — so the
 * CSRF check keeps comparing the same origin the browser sent, preserving the
 * protection instead of bypassing it.
 */
export function normalizeForwardedHost(req: express.Request, trustProxy: boolean): void {
  if (!trustProxy) return;

  const forwardedHost = firstForwardedValue(req.headers["x-forwarded-host"]);
  if (!forwardedHost) return;

  const scheme = firstForwardedValue(req.headers["x-forwarded-proto"]) ?? req.protocol;
  const [hostname, hostHeaderPort] = splitHostPort(forwardedHost);
  const forwardedPort = hostHeaderPort ?? firstForwardedValue(req.headers["x-forwarded-port"]);
  const isDefaultPort = forwardedPort === DEFAULT_PORT_BY_SCHEME[scheme.toLowerCase()];

  req.headers.host = forwardedPort && !isDefaultPort ? `${hostname}:${forwardedPort}` : hostname;
}

function firstForwardedValue(value: string | string[] | undefined): string | undefined {
  const raw = Array.isArray(value) ? value[0] : value?.split(",")[0];
  const trimmed = raw?.trim();
  return trimmed ? trimmed : undefined;
}

function splitHostPort(hostHeader: string): [hostname: string, port: string | undefined] {
  // IPv6 literals are bracketed (e.g. "[::1]:8080") so the port can't be found
  // by naively splitting on the last colon of the address itself.
  const bracketMatch = /^\[([^\]]+)](?::(\d+))?$/.exec(hostHeader);
  if (bracketMatch) return [`[${bracketMatch[1]}]`, bracketMatch[2]];

  const separatorIndex = hostHeader.lastIndexOf(":");
  if (separatorIndex === -1) return [hostHeader, undefined];
  return [hostHeader.slice(0, separatorIndex), hostHeader.slice(separatorIndex + 1)];
}

/**
 * Strip client-supplied forwarded headers and set canonical values from Express.
 * Prevents spoofed X-Forwarded-* from being laundered through the loopback backend.
 */
export function applyCanonicalForwardedHeaders(
  proxyReq: {
    removeHeader: (name: string) => void;
    setHeader: (name: string, value: string) => void;
  },
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
