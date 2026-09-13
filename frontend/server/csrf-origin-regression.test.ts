import http from "node:http";
import type { AddressInfo } from "node:net";
import express from "express";
import { afterEach, describe, expect, it } from "vitest";
import { normalizeForwardedHost } from "./forwarded-headers";
import { resolvePort } from "./react-router-request-handler";

// Mirrors @react-router/express's createRemixRequest URL resolution (as fixed
// in react-router-request-handler.ts) plus react-router's action CSRF host
// check. Neither is exported by those packages, so this reproduces them
// locally to regression-test the fix against a real Express request/response
// cycle instead of only the normalizeForwardedHost/resolvePort units, matching
// how login is actually served in production behind a reverse proxy.
function resolveRequestUrlHost(req: express.Request): string {
  const port = resolvePort(req);
  return `${req.hostname}${port ? `:${port}` : ""}`;
}

function buildApp(trustProxy: boolean): express.Express {
  const app = express();
  if (trustProxy) {
    app.set("trust proxy", 1);
    app.use((req, _res, next) => {
      normalizeForwardedHost(req, trustProxy);
      next();
    });
  }
  app.post("/login.data", (req, res) => {
    const origin = req.get("origin");
    const originHost = origin ? new URL(origin).host : null;
    const requestUrlHost = resolveRequestUrlHost(req);
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
});
