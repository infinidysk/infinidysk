import http from "node:http";
import type { AddressInfo } from "node:net";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createProxyMiddleware } from "http-proxy-middleware";
import { handleBackendProxyResponse } from "./backend-proxy-response";

vi.mock("./logger", () => ({
  logger: { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

type TransferResult = {
  status: number;
  bytes: number;
  body: string;
  endedCleanly: boolean;
};

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

/**
 * Settles on a clean end, an aborted response, or a request error, so a request
 * that hangs instead fails the test by timing out.
 */
function fetchThroughProxy(
  port: number,
  path: string,
  headers?: http.OutgoingHttpHeaders,
): Promise<TransferResult> {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { hostname: "127.0.0.1", port, path, method: "GET", headers },
      (res) => {
        let bytes = 0;
        const chunks: Buffer[] = [];
        const status = res.statusCode ?? 0;
        res.on("data", (chunk: Buffer) => {
          bytes += chunk.length;
          chunks.push(chunk);
        });
        res.on("end", () => resolve({
          status,
          bytes,
          body: Buffer.concat(chunks).toString("utf8"),
          endedCleanly: true,
        }));
        res.on("aborted", () => resolve({
          status,
          bytes,
          body: Buffer.concat(chunks).toString("utf8"),
          endedCleanly: false,
        }));
        res.on("error", () => resolve({
          status,
          bytes,
          body: Buffer.concat(chunks).toString("utf8"),
          endedCleanly: false,
        }));
      },
    );
    req.on("error", (error) => {
      const code = (error as NodeJS.ErrnoException).code;
      if (code === "ECONNRESET" || error.message === "socket hang up") {
        resolve({ status: 0, bytes: 0, body: "", endedCleanly: false });
        return;
      }
      reject(error);
    });
    req.end();
  });
}

/**
 * A backend that streams a prefix and then aborts, the way a WebDAV read does
 * when it finds the rest of the data is missing. The prefix is flushed before
 * the socket goes away so the client really is mid-transfer, rather than the
 * proxy failing before it ever saw a response.
 */
function abortAfterPrefix(headers: http.OutgoingHttpHeaders): http.RequestListener {
  return (_req, res) => {
    res.writeHead(200, headers);
    res.write(Buffer.alloc(200, 1), () => {
      setTimeout(() => res.socket?.destroy(), 10);
    });
  };
}

describe("handleBackendProxyResponse", () => {
  const servers: http.Server[] = [];

  afterEach(async () => {
    await Promise.all(servers.splice(0).map((server) => close(server)));
  });

  async function startProxy(backendHandler: http.RequestListener): Promise<number> {
    const backend = http.createServer(backendHandler);
    servers.push(backend);
    const backendPort = await listen(backend);

    const proxy = createProxyMiddleware({
      target: `http://127.0.0.1:${backendPort}`,
      changeOrigin: true,
      selfHandleResponse: true,
      on: { proxyRes: handleBackendProxyResponse },
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
    return listen(frontend);
  }

  it("does not pass off a truncated backend response as a finished transfer", async () => {
    const frontendPort = await startProxy(abortAfterPrefix({
      "Content-Type": "video/x-matroska",
      "Content-Length": "1000",
    }));

    const result = await fetchThroughProxy(frontendPort, "/content/movie.mkv");

    expect(result.endedCleanly).toBe(false);
    expect(result.bytes).toBeLessThan(1000);
  });

  it("does not pass off a truncated chunked backend response as finished", async () => {
    const frontendPort = await startProxy(abortAfterPrefix({
      "Content-Type": "video/x-matroska",
    }));

    const result = await fetchThroughProxy(frontendPort, "/content/movie.mkv");

    expect(result.endedCleanly).toBe(false);
  });

  it("ends a complete backend response normally", async () => {
    const body = Buffer.alloc(1000, 7);
    const frontendPort = await startProxy((_req, res) => {
      res.writeHead(200, {
        "Content-Type": "video/x-matroska",
        "Content-Length": String(body.length),
      });
      res.end(body);
    });

    const result = await fetchThroughProxy(frontendPort, "/content/movie.mkv");

    expect(result.status).toBe(200);
    expect(result.endedCleanly).toBe(true);
    expect(result.bytes).toBe(body.length);
  });

  it("ends a bodyless backend response normally", async () => {
    const frontendPort = await startProxy((_req, res) => {
      res.writeHead(404, { "Content-Type": "text/plain" });
      res.end();
    });

    const result = await fetchThroughProxy(frontendPort, "/content/missing.mkv");

    expect(result.status).toBe(404);
    expect(result.endedCleanly).toBe(true);
  });

  it("renders a friendly page for browser requests to unavailable view files", async () => {
    const frontendPort = await startProxy((_req, res) => {
      res.writeHead(400, { "Content-Type": "text/plain" });
      res.end("The file does not exist.");
    });

    const result = await fetchThroughProxy(
      frontendPort,
      "/view/content/tv/example-show/example.mkv?downloadKey=abc",
      { Accept: "text/html,application/xhtml+xml" },
    );

    expect(result.status).toBe(200);
    expect(result.endedCleanly).toBe(true);
    expect(result.body).toContain("File unavailable");
    expect(result.body).toContain('href="/explore/content/tv/example-show"');
  });

  it("passes unavailable view files through unchanged for non-browser clients", async () => {
    const frontendPort = await startProxy((_req, res) => {
      res.writeHead(404, { "Content-Type": "text/plain" });
      res.end("The file does not exist.");
    });

    const result = await fetchThroughProxy(
      frontendPort,
      "/view/content/tv/example-show/example.mkv?downloadKey=abc",
      { Accept: "application/octet-stream" },
    );

    expect(result.status).toBe(404);
    expect(result.endedCleanly).toBe(true);
    expect(result.body).toBe("The file does not exist.");
  });

  it("ends a chunked backend response that finishes normally", async () => {
    const frontendPort = await startProxy((_req, res) => {
      res.writeHead(200, { "Content-Type": "video/x-matroska" });
      res.write(Buffer.alloc(200, 1));
      res.end(Buffer.alloc(200, 2));
    });

    const result = await fetchThroughProxy(frontendPort, "/content/movie.mkv");

    expect(result.endedCleanly).toBe(true);
    expect(result.bytes).toBe(400);
  });
});
