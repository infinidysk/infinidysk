import http from "node:http";
import type { AddressInfo } from "node:net";
import compression from "compression";
import express, { type Request, type Response } from "express";
import { afterEach, describe, expect, it } from "vitest";
import { shouldCompressResponse } from "./compression-filter";

function mockReq(path: string): Request {
  return { path, headers: {}, method: "GET" } as Request;
}

function mockRes(headers: Record<string, string | number | undefined>): Response {
  return {
    getHeader(name: string) {
      return headers[name.toLowerCase()];
    },
    statusCode: 200,
  } as unknown as Response;
}

describe("shouldCompressResponse", () => {
  it("skips backend proxy paths even for compressible types", () => {
    expect(
      shouldCompressResponse(
        mockReq("/view/movies"),
        mockRes({ "content-type": "application/javascript", "content-length": 4096 }),
      ),
    ).toBe(false);
  });

  it("skips React Router streamed HTML regardless of Accept", () => {
    expect(
      shouldCompressResponse(
        mockReq("/queue"),
        mockRes({ "content-type": "text/html; charset=utf-8" }),
      ),
    ).toBe(false);
  });

  it("skips React Router .data streams (text/x-script)", () => {
    expect(
      shouldCompressResponse(mockReq("/queue.data"), mockRes({ "content-type": "text/x-script" })),
    ).toBe(false);
  });

  it("skips text/event-stream", () => {
    expect(
      shouldCompressResponse(mockReq("/events"), mockRes({ "content-type": "text/event-stream" })),
    ).toBe(false);
  });

  it("allows compression for static JS with Content-Length", () => {
    expect(
      shouldCompressResponse(
        mockReq("/assets/app.js"),
        mockRes({
          "content-type": "application/javascript; charset=UTF-8",
          "content-length": 8192,
        }),
      ),
    ).toBe(true);
  });
});

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

describe("compression filter integration", () => {
  let server: http.Server | undefined;

  afterEach(async () => {
    if (server) {
      await close(server);
      server = undefined;
    }
  });

  it("does not gzip streamed HTML with Accept */* and flush", async () => {
    const warnings: Error[] = [];
    const onWarning = (warning: Error) => warnings.push(warning);
    process.on("warning", onWarning);

    try {
      const app = express();
      app.use(compression({ filter: shouldCompressResponse }));
      app.get("/queue", (_req, res) => {
        res.setHeader("Content-Type", "text/html; charset=utf-8");
        res.statusCode = 200;
        // Force headers through compression's onHeaders hook.
        res.writeHead(200);
        for (let i = 0; i < 20; i++) {
          res.write(`<p>chunk-${i}-${"x".repeat(256)}</p>`);
          if (typeof res.flush === "function") res.flush();
        }
        res.end();
      });

      server = http.createServer(app);
      const port = await listen(server);

      const encoding = await new Promise<string | undefined>((resolve, reject) => {
        http
          .get(
            {
              hostname: "127.0.0.1",
              port,
              path: "/queue",
              headers: {
                Accept: "*/*",
                "Accept-Encoding": "gzip",
              },
            },
            (res) => {
              res.resume();
              res.on("end", () =>
                resolve(
                  typeof res.headers["content-encoding"] === "string"
                    ? res.headers["content-encoding"]
                    : undefined,
                ),
              );
              res.on("error", reject);
            },
          )
          .on("error", reject);
      });

      expect(encoding).toBeUndefined();
      expect(
        warnings.some(
          (w) =>
            w.name === "MaxListenersExceededWarning" ||
            /drain listeners added to \[Gzip\]/i.test(w.message),
        ),
      ).toBe(false);
    } finally {
      process.off("warning", onWarning);
    }
  });

  it("still gzips static-like JS responses", async () => {
    const body = "x".repeat(4096);
    const app = express();
    app.use(compression({ filter: shouldCompressResponse }));
    app.get("/assets/app.js", (_req, res) => {
      res.setHeader("Content-Type", "application/javascript");
      res.setHeader("Content-Length", Buffer.byteLength(body));
      res.end(body);
    });

    server = http.createServer(app);
    const port = await listen(server);

    const encoding = await new Promise<string | undefined>((resolve, reject) => {
      http
        .get(
          {
            hostname: "127.0.0.1",
            port,
            path: "/assets/app.js",
            headers: { "Accept-Encoding": "gzip" },
          },
          (res) => {
            res.resume();
            res.on("end", () =>
              resolve(
                typeof res.headers["content-encoding"] === "string"
                  ? res.headers["content-encoding"]
                  : undefined,
              ),
            );
            res.on("error", reject);
          },
        )
        .on("error", reject);
    });

    expect(encoding).toBe("gzip");
  });
});
