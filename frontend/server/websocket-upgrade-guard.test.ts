import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { logger } from "./logger";
import { resetClientErrorLogThrottleForTests } from "./request-log-throttle";
import { websocketUpgradeGuard } from "./websocket-upgrade-guard";

function mockReq(overrides: Record<string, unknown> = {}) {
  return {
    method: "GET",
    ip: "203.0.113.7",
    socket: { remoteAddress: "10.0.0.1" },
    ...overrides,
  };
}

function mockRes() {
  const res = {
    statusCode: 0,
    headers: new Map<string, string>(),
    body: "",
    status(code: number) {
      res.statusCode = code;
      return res;
    },
    setHeader(name: string, value: string) {
      res.headers.set(name, value);
      return res;
    },
    type(_value: string) {
      return res;
    },
    send(body: string) {
      res.body = body;
      return res;
    },
  };
  return res;
}

describe("websocketUpgradeGuard", () => {
  beforeEach(() => {
    resetClientErrorLogThrottleForTests();
    vi.spyOn(logger, "warn").mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("answers 426 with an Upgrade header and a plain-text body", () => {
    const res = mockRes();
    websocketUpgradeGuard(mockReq() as never, res as never, vi.fn());

    expect(res.statusCode).toBe(426);
    expect(res.headers.get("Upgrade")).toBe("websocket");
    expect(res.body).toMatch(/WebSocket/);
  });

  it("warns once per client and throttles repeats within the window", () => {
    websocketUpgradeGuard(mockReq() as never, mockRes() as never, vi.fn());
    websocketUpgradeGuard(mockReq() as never, mockRes() as never, vi.fn());

    expect(logger.warn).toHaveBeenCalledOnce();
    const message = vi.mocked(logger.warn).mock.calls[0]?.[0];
    expect(message).toContain("203.0.113.7");
    expect(message).toContain("Upgrade/Connection");
  });

  it("tracks different clients independently", () => {
    websocketUpgradeGuard(mockReq() as never, mockRes() as never, vi.fn());
    websocketUpgradeGuard(mockReq({ ip: "198.51.100.4" }) as never, mockRes() as never, vi.fn());

    expect(logger.warn).toHaveBeenCalledTimes(2);
  });
});
