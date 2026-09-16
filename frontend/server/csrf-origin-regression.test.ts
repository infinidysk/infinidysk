import http from "node:http";
import type { AddressInfo } from "node:net";
import express from "express";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { backendClient } from "~/clients/backend-client.server";
import {
  invalidateProxySettingsCache,
  resolveConfiguredActionOrigin,
} from "./configured-action-origin";
import { normalizeForwardedHost } from "./forwarded-headers";
import { resolveRequestUrl } from "./react-router-request-handler";

// Mirrors createRequestHandler in react-router-request-handler.ts (canonical
// origin resolution + SSR URL construction) plus react-router's action CSRF
// host check. The framework check is not exported, so it is reproduced locally
// to regression-test login against a real Express request/response cycle —
// directly and behind a reverse proxy — with the Base URL and trust-proxy
// settings the production handler reads from the backend.

function buildApp(trustProxy: boolean): express.Express {
  const app = express();
  if (trustProxy) {
    app.set("trust proxy", 1);
    app.use((req, _res, next) => {
      normalizeForwardedHost(req, trustProxy);
      next();
    });
  }
  app.post("/login.data", async (req, res) => {
    const canonicalOrigin = await resolveConfiguredActionOrigin(req);
    const requestUrlHost = resolveRequestUrl(req, canonicalOrigin).host;
    const origin = req.get("origin");
    const originHost = origin ? new URL(origin).host : null;
    if (originHost && originHost !== requestUrlHost) {
      res.status(400).send("Bad Request");
      return;
    }
    res.status(200).send("ok");
  });
  return app;
}

function listen(server: http.Server): Promise<number> {
  return new Promise((resolve, reject) => {
    server.listen(0, "127.0.0.1", () => {
      const address = server.address() as AddressInfo | null;
      if (!address) {
        reject(new Error("server has no address"));
        return;
      }
      resolve(address.port);
    });
    server.on("error", reject);
  });
}

function close(server: http.Server): Promise<void> {
  return new Promise((resolve, reject) => {
    server.close((error) => (error ? reject(error) : resolve()));
  });
}

function postLogin(port: number, headers: http.OutgoingHttpHeaders): Promise<number> {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: "127.0.0.1", port, path: "/login.data", method: "POST", headers },
      (res) => {
        res.resume();
        res.on("end", () => resolve(res.statusCode ?? 0));
        res.on("error", reject);
      },
    );
    req.on("error", reject);
    req.end();
  });
}

describe("login action origin check (reverse proxy regression)", () => {
  const servers: http.Server[] = [];

  beforeEach(() => {
    vi.restoreAllMocks();
    invalidateProxySettingsCache();
    // Reporter's setup: Base URL is the proxy's public address and proxy trust is enabled.
    vi.spyOn(backendClient, "getConfig").mockResolvedValue([
      { configName: "general.base-url", configValue: "https://nzbdav.example.com" },
      { configName: "general.trust-proxy", configValue: "true" },
    ]);
  });

  afterEach(async () => {
    await Promise.all(servers.splice(0).map((server) => close(server)));
  });

  it("accepts a direct login with no reverse proxy in front", async () => {
    const server = http.createServer(buildApp(false));
    servers.push(server);
    const port = await listen(server);

    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      Origin: `http://127.0.0.1:${port}`,
    });

    expect(status).toBe(200);
  });

  it("accepts a login proxied through a default-port HTTPS reverse proxy in front of an internal port", async () => {
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    // Matches a container listening internally on :3000 behind a reverse proxy
    // terminating TLS on the default HTTPS port for a public domain.
    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      "X-Forwarded-Host": "nzbdav.example.com",
      "X-Forwarded-Proto": "https",
      "X-Forwarded-For": "203.0.113.5",
      Origin: "https://nzbdav.example.com",
    });

    expect(status).toBe(200);
  });

  it("accepts a login proxied on a non-default forwarded port", async () => {
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      "X-Forwarded-Host": "nzbdav.example.com",
      "X-Forwarded-Proto": "https",
      "X-Forwarded-Port": "8443",
      Origin: "https://nzbdav.example.com:8443",
    });

    expect(status).toBe(200);
  });

  it("still rejects a genuine cross-origin action request (CSRF protection preserved)", async () => {
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      "X-Forwarded-Host": "nzbdav.example.com",
      "X-Forwarded-Proto": "https",
      Origin: "https://attacker.example",
    });

    expect(status).toBe(400);
  });

  it("accepts a login proxied through an IPv6 literal on the default HTTPS port", async () => {
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      "X-Forwarded-Host": "[2001:db8::1]",
      "X-Forwarded-Proto": "https",
      Origin: "https://[2001:db8::1]",
    });

    expect(status).toBe(200);
  });

  it("accepts a login proxied through an IPv6 literal on a non-default port (regression)", async () => {
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    // Naively splitting "[2001:db8::1]:8443" on ":" treats "db8" as the port
    // candidate, fails numeric parsing, and silently drops 8443 — the bug
    // CodeRabbit flagged in @react-router/express's createRemixRequest.
    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      "X-Forwarded-Host": "[2001:db8::1]:8443",
      "X-Forwarded-Proto": "https",
      Origin: "https://[2001:db8::1]:8443",
    });

    expect(status).toBe(200);
  });

  it("accepts a login proxied through a multi-hop X-Forwarded-Host chain (regression)", async () => {
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    // Without trimming to the first hop before parsing the port, splitHostPort
    // would return "8443, edge.internal" as the port and build an invalid URL.
    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      "X-Forwarded-Host": "nzbdav.example.com:8443, edge.internal",
      "X-Forwarded-Proto": "https",
      Origin: "https://nzbdav.example.com:8443",
    });

    expect(status).toBe(200);
  });

  it("accepts a direct login when Base URL points at the reverse proxy and proxy trust is enabled", async () => {
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    // No X-Forwarded-* headers: the browser reached the container directly.
    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      Origin: `http://127.0.0.1:${port}`,
    });

    expect(status).toBe(200);
  });

  it("accepts a direct login when the reverse-proxy settings cannot be read", async () => {
    vi.spyOn(backendClient, "getConfig").mockRejectedValue(new Error("backend unavailable"));
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      Origin: `http://127.0.0.1:${port}`,
    });

    expect(status).toBe(200);
  });

  it("still rejects a cross-origin action on a direct connection when Base URL is configured", async () => {
    const server = http.createServer(buildApp(true));
    servers.push(server);
    const port = await listen(server);

    const status = await postLogin(port, {
      Host: `127.0.0.1:${port}`,
      Origin: "https://attacker.example",
    });

    expect(status).toBe(400);
  });
});
