import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  handleError,
  isActionOriginRejection,
  resetOriginRejectionLogThrottleForTests,
} from "./entry.server";
import { logger } from "../server/logger";

describe("entry.server handleError", () => {
  beforeEach(() => {
    resetOriginRejectionLogThrottleForTests();
    vi.restoreAllMocks();
  });

  it("detects action origin rejection on POST with origin mismatch and Bad Request", () => {
    const request = new Request("http://localhost:3000/login.data", {
      method: "POST",
      headers: { Origin: "https://public.example.com" },
    });
    const error = new Error("Bad Request");

    expect(isActionOriginRejection(error, request)).toBe(true);
  });

  it("does not classify request as action origin rejection if origin matches", () => {
    const request = new Request("http://localhost:3000/login.data", {
      method: "POST",
      headers: { Origin: "http://localhost:3000" },
    });
    const error = new Error("Bad Request");

    expect(isActionOriginRejection(error, request)).toBe(false);
  });

  it("does not classify request as action origin rejection if method is GET", () => {
    const request = new Request("http://localhost:3000/login.data", {
      method: "GET",
      headers: { Origin: "https://public.example.com" },
    });
    const error = new Error("Bad Request");

    expect(isActionOriginRejection(error, request)).toBe(false);
  });

  it("does not classify request as action origin rejection if error is unrelated", () => {
    const request = new Request("http://localhost:3000/login.data", {
      method: "POST",
      headers: { Origin: "https://public.example.com" },
    });
    const error = new Error("Database connection failed");

    expect(isActionOriginRejection(error, request)).toBe(false);
  });

  it("logs throttled warning on action origin rejection in handleError", () => {
    const loggerWarnSpy = vi.spyOn(logger, "warn").mockImplementation(() => {});
    const consoleErrorSpy = vi.spyOn(console, "error").mockImplementation(() => {});

    const request = new Request("http://localhost:3000/login.data", {
      method: "POST",
      headers: { Origin: "https://public.example.com" },
    });
    const error = new Error("Bad Request");

    handleError(error, {
      request,
      params: {},
      context: {},
    });

    expect(loggerWarnSpy).toHaveBeenCalledTimes(1);
    expect(loggerWarnSpy.mock.calls[0][0]).toContain("Action request origin rejected");
    expect(loggerWarnSpy.mock.calls[0][0]).toContain("TRUST_PROXY=1");
    expect(consoleErrorSpy).not.toHaveBeenCalled();

    // Repeated call within throttle window should be suppressed
    handleError(error, {
      request,
      params: {},
      context: {},
    });
    expect(loggerWarnSpy).toHaveBeenCalledTimes(1);
  });

  it("logs console.error for unexpected errors in handleError", () => {
    const loggerWarnSpy = vi.spyOn(logger, "warn").mockImplementation(() => {});
    const consoleErrorSpy = vi.spyOn(console, "error").mockImplementation(() => {});

    const request = new Request("http://localhost:3000/login.data", {
      method: "POST",
      headers: { Origin: "http://localhost:3000" },
    });
    const error = new Error("Unexpected crash");

    handleError(error, {
      request,
      params: {},
      context: {},
    });

    expect(loggerWarnSpy).not.toHaveBeenCalled();
    expect(consoleErrorSpy).toHaveBeenCalledWith(error);
  });
});
