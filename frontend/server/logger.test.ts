import { EventEmitter } from "node:events";
import type { Request, Response } from "express";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { logger, requestLogger, requestPathForLog } from "./logger";
import {
  CLIENT_ERROR_LOG_THROTTLE_MS,
  resetClientErrorLogThrottleForTests,
} from "./request-log-throttle";

const apiKey = "synthetic-1441-api-key";
const nestedToken = "synthetic-1441-indexer-token";
const nestedUrl = `https://indexer.example.test/nzb?token=${nestedToken}`;
const requestUrl =
  `/prefix/api?mode=addurl&apikey=${apiKey}&name=${encodeURIComponent(nestedUrl)}`;
const logLevels = ["debug", "info", "warn", "error"] as const;

function finishRequest(statusCode: number, originalUrl = requestUrl) {
  const req = {
    method: "POST",
    originalUrl,
    url: originalUrl,
    path: "/prefix/api",
    ip: "127.0.0.1",
    socket: { remoteAddress: "127.0.0.1" },
    headers: {
      "user-agent": "synthetic-1441-client",
      "x-api-key": "synthetic-1441-header-key",
    },
  } as unknown as Request;
  const res = Object.assign(new EventEmitter(), { statusCode });
  const next = vi.fn();
  requestLogger(req, res as unknown as Response, next);
  res.emit("finish");
  return { req, res, next };
}

function expectNoQuerySecrets(): void {
  const text = logLevels
    .flatMap((level) => vi.mocked(logger[level]).mock.calls)
    .flat()
    .map(String)
    .join("\n");
  for (const value of [apiKey, nestedToken, "synthetic-1441-header-key"]) {
    expect(text).not.toContain(value);
  }
  expect(text).not.toContain("?mode=");
  expect(text).not.toContain("apikey=");
  expect(text).not.toContain("name=");
}

beforeEach(() => {
  vi.stubEnv("NODE_ENV", "production");
  vi.spyOn(process, "uptime").mockReturnValue(120);
  vi.spyOn(Date, "now").mockReturnValue(1_000_000);
  resetClientErrorLogThrottleForTests();
  for (const level of logLevels) {
    vi.spyOn(logger, level).mockImplementation(() => {});
  }
});

afterEach(() => {
  resetClientErrorLogThrottleForTests();
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
});

describe("requestPathForLog", () => {
  it.each([
    ["/api", "/api"],
    ["/api?apikey=synthetic-1441-api-key&apikey=another-synthetic-key", "/api"],
    ["/prefix/api?%61PiKeY=synthetic-1441-api-key&APIKEY=second-key", "/prefix/api"],
    [requestUrl, "/prefix/api"],
    ["/api?unknownCapability=synthetic-1441-api-key", "/api"],
    ["/api?apikey=%ZZ#synthetic-1441-api-key", "/api"],
    ["/content/a%20b%3Fc.mkv?downloadKey=synthetic-1441-api-key", "/content/a%20b%3Fc.mkv"],
    ["https://user:synthetic-password@example.test/api?apikey=synthetic-1441-api-key", "/api"],
    ["http://[?apikey=synthetic-1441-api-key", "[invalid path]"],
    ["data:synthetic-1441-api-key?token=synthetic-1441-indexer-token", "[invalid path]"],
    [undefined, "[unknown path]"],
    ["", "[unknown path]"],
  ])("returns a safe pathname for %s", (input, expected) => {
    expect(requestPathForLog(input)).toBe(expected);
  });
});

describe("requestLogger", () => {
  it.each([400, 500])("logs pathname-only context for a %i response", (statusCode) => {
    const { req, next } = finishRequest(statusCode);
    const selectedLogger = statusCode === 400 ? logger.warn : logger.error;

    expect(next).toHaveBeenCalledOnce();
    expect(selectedLogger).toHaveBeenCalledOnce();
    for (const level of logLevels) {
      if (level !== (statusCode === 400 ? "warn" : "error")) {
        expect(logger[level]).not.toHaveBeenCalled();
      }
    }
    const message = vi.mocked(selectedLogger).mock.calls[0]?.[0] as string;
    for (const value of ["POST", "/prefix/api", String(statusCode), "ms", "127.0.0.1", "synthetic-1441-client"]) {
      expect(message).toContain(value);
    }
    expectNoQuerySecrets();
    expect(req.url).toBe(requestUrl);
    expect(req.originalUrl).toBe(requestUrl);
    expect(req.headers["x-api-key"]).toBe("synthetic-1441-header-key");
  });

  it("keeps successful production requests quiet", () => {
    finishRequest(200);
    for (const level of logLevels) expect(logger[level]).not.toHaveBeenCalled();
  });

  it("keeps successful development logs query-free", () => {
    vi.stubEnv("NODE_ENV", "development");
    finishRequest(200);
    expect(logger.debug).toHaveBeenCalledOnce();
    expect(logger.debug).toHaveBeenCalledWith(expect.stringContaining("/prefix/api"));
    expect(logger.warn).not.toHaveBeenCalled();
    expect(logger.error).not.toHaveBeenCalled();
    expectNoQuerySecrets();
  });

  it("keeps startup 502 logs query-free and debug-only", () => {
    vi.spyOn(process, "uptime").mockReturnValue(1);
    finishRequest(502);
    expect(logger.debug).toHaveBeenCalledOnce();
    expect(logger.error).not.toHaveBeenCalled();
    expectNoQuerySecrets();
  });

  it("keeps post-startup 502 logs query-free", () => {
    finishRequest(502);
    expect(logger.error).toHaveBeenCalledOnce();
    expect(logger.debug).not.toHaveBeenCalled();
    expectNoQuerySecrets();
  });

  it("preserves client-error throttling and suppression counts without secrets", () => {
    finishRequest(400);
    finishRequest(400);
    expect(logger.warn).toHaveBeenCalledTimes(1);
    expect(logger.debug).toHaveBeenCalledTimes(1);

    vi.mocked(Date.now).mockReturnValue(1_000_000 + CLIENT_ERROR_LOG_THROTTLE_MS);
    finishRequest(400);

    expect(logger.warn).toHaveBeenCalledTimes(2);
    expect(logger.warn).toHaveBeenLastCalledWith(
      expect.stringContaining("(+1 similar suppressed)"),
    );
    expect(logger.error).not.toHaveBeenCalled();
    expectNoQuerySecrets();
  });

  it("keeps the favicon exception quiet", () => {
    const { next } = finishRequest(404, "/favicon.ico");
    for (const level of logLevels) expect(logger[level]).not.toHaveBeenCalled();
    expect(next).toHaveBeenCalledOnce();
  });
});