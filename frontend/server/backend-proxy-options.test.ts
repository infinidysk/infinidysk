import http from "node:http";
import type { AddressInfo, Socket } from "node:net";
import { afterEach, describe, expect, it } from "vitest";
import { createProxyMiddleware } from "http-proxy-middleware";
import { backendProxyTimeoutOptions, LONG_RUNNING_PROXY_TIMEOUT_MS } from "./backend-proxy-options";

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
    server.close((error) => {
      if (error) reject(error);
      else resolve();
    });
  });
}

function requestOnce(port: number, agent: http.Agent, path: string): Promise<number> {
  return new Promise((resolve, reject) => {
    const req = http.request(
      {
        hostname: "127.0.0.1",
        port,
        path,
        method: "GET",
        agent,
      },
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

describe("backendProxyTimeoutOptions", () => {
  it("omits httpxy inbound timeout to avoid keep-alive listener leaks", () => {
    expect("timeout" in backendProxyTimeoutOptions).toBe(false);
    expect(backendProxyTimeoutOptions.proxyTimeout).toBe(LONG_RUNNING_PROXY_TIMEOUT_MS);
  });
});

describe("backend proxy keep-alive timeout listeners", () => {
  const servers: http.Server[] = [];
  const agents: http.Agent[] = [];

  afterEach(async () => {
    for (const agent of agents.splice(0)) {
      agent.destroy();
    }
    await Promise.all(servers.splice(0).map((server) => close(server)));
  });

  it("does not stack timeout listeners on keep-alive client sockets", async () => {
    const backend = http.createServer((_req, res) => {
      res.writeHead(200, { "Content-Type": "text/plain" });
      res.end("ok");
    });
    servers.push(backend);
    const backendPort = await listen(backend);

    const proxy = createProxyMiddleware({
      target: `http://127.0.0.1:${backendPort}`,
      changeOrigin: false,
      ...backendProxyTimeoutOptions,
    });

    const frontend = http.createServer((req, res) => {
      void proxy(req, res, (error) => {
        if (error && !res.headersSent) {
          res.writeHead(502);
          res.end("Bad Gateway");
        }
      });
    });
    servers.push(frontend);

    let inboundSocket: Socket | undefined;
    frontend.on("connection", (socket) => {
      if (!inboundSocket) inboundSocket = socket;
    });

    const frontendPort = await listen(frontend);
    const agent = new http.Agent({ keepAlive: true, maxSockets: 1 });
    agents.push(agent);

    const requestCount = 12;
    const listenerCounts: number[] = [];

    for (let i = 0; i < requestCount; i++) {
      const status = await requestOnce(frontendPort, agent, `/probe-${i}`);
      expect(status).toBe(200);
      expect(inboundSocket).toBeDefined();
      listenerCounts.push(inboundSocket!.listenerCount("timeout"));
    }

    // Same TCP connection reused for every request.
    expect(inboundSocket).toBeDefined();
    const first = listenerCounts[0];
    for (const count of listenerCounts) {
      expect(count).toBe(first);
    }
    // Must stay well below Node's default MaxListeners of 10.
    expect(first).toBeLessThanOrEqual(1);
  });
});
