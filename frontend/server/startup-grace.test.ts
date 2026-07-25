import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  BACKEND_FAILURE_LOG_THROTTLE_MS,
  BACKEND_MIGRATING_CODE,
  BACKEND_STARTUP_GRACE_MS,
  formatBackendUnavailableReason,
  isExpectedBackendConnectionError,
  isExpectedBackendUnavailableError,
  isWithinBackendStartupGrace,
  resetBackendUnavailableLogThrottleForTests,
  shouldEmitThrottledBackendUnavailableLog,
} from "./startup-grace";

describe("startup-grace helpers", () => {
  beforeEach(() => {
    resetBackendUnavailableLogThrottleForTests();
  });

  it("recognizes ECONNREFUSED AggregateErrors", () => {
    const error = Object.assign(new Error("proxy failed"), {
      code: undefined,
      cause: Object.assign(new Error("refused"), {
        code: "ECONNREFUSED",
        errors: [{ code: "ECONNREFUSED" }, { code: "ECONNREFUSED" }],
      }),
    });

    expect(isExpectedBackendConnectionError(error)).toBe(true);
    expect(isExpectedBackendConnectionError(new Error("boom"))).toBe(false);
  });

  it("recognizes undici timeout codes", () => {
    const error = Object.assign(new Error("fetch failed"), {
      cause: Object.assign(new Error("headers timeout"), {
        code: "UND_ERR_HEADERS_TIMEOUT",
      }),
    });

    expect(isExpectedBackendConnectionError(error)).toBe(true);
    expect(isExpectedBackendUnavailableError(error)).toBe(true);
  });

  it("reports within the startup grace window for a fresh process", () => {
    expect(isWithinBackendStartupGrace()).toBe(true);
    expect(isWithinBackendStartupGrace(0)).toBe(true);
    expect(isWithinBackendStartupGrace(BACKEND_STARTUP_GRACE_MS)).toBe(false);
  });

  it("treats BackendUnavailableError with a network code as expected", () => {
    const error = Object.assign(
      new Error("Failed to get history: fetch failed (ECONNREFUSED)"),
      { name: "BackendUnavailableError", code: "ECONNREFUSED" },
    );

    expect(isExpectedBackendUnavailableError(error)).toBe(true);
  });

  it("treats BackendUnavailableError with MIGRATING code as expected", () => {
    const error = Object.assign(
      new Error("Failed to get config items: backend is starting or migrating"),
      { name: "BackendUnavailableError", code: BACKEND_MIGRATING_CODE },
    );

    expect(isExpectedBackendUnavailableError(error)).toBe(true);
  });

  it("does not treat BackendUnavailableError without a network code as expected", () => {
    const error = Object.assign(
      new Error("Failed to get config items: Invalid URL"),
      { name: "BackendUnavailableError" },
    );

    expect(isExpectedBackendUnavailableError(error)).toBe(false);
  });

  it("treats BackendUnavailableError with a network cause chain as expected", () => {
    const error = Object.assign(
      new Error("Failed to get history: fetch failed"),
      {
        name: "BackendUnavailableError",
        cause: Object.assign(new Error("connect failed"), { code: "ECONNRESET" }),
      },
    );

    expect(isExpectedBackendUnavailableError(error)).toBe(true);
  });

  it("treats raw connection errors as expected backend unavailability", () => {
    const error = Object.assign(new Error("connect failed"), { code: "ECONNREFUSED" });
    expect(isExpectedBackendUnavailableError(error)).toBe(true);
  });

  it("formats BackendUnavailableError messages for warn lines", () => {
    const error = Object.assign(
      new Error("Failed to get history: fetch failed (UND_ERR_HEADERS_TIMEOUT)"),
      { name: "BackendUnavailableError", code: "UND_ERR_HEADERS_TIMEOUT" },
    );
    expect(formatBackendUnavailableReason(error)).toBe(
      "Failed to get history: fetch failed (UND_ERR_HEADERS_TIMEOUT)",
    );
    expect(formatBackendUnavailableReason("plain")).toBe("plain");
  });

  it("throttles repeated expected-backend-unavailable log emission", () => {
    const t0 = 1_000_000;
    expect(shouldEmitThrottledBackendUnavailableLog(t0)).toBe(true);
    expect(shouldEmitThrottledBackendUnavailableLog(t0 + 1)).toBe(false);
    expect(
      shouldEmitThrottledBackendUnavailableLog(t0 + BACKEND_FAILURE_LOG_THROTTLE_MS - 1),
    ).toBe(false);
    expect(
      shouldEmitThrottledBackendUnavailableLog(t0 + BACKEND_FAILURE_LOG_THROTTLE_MS),
    ).toBe(true);
  });

  it("shares throttle state across separately loaded server bundles", async () => {
    const t0 = 1_000_000;
    expect(shouldEmitThrottledBackendUnavailableLog(t0)).toBe(true);

    vi.resetModules();
    const separatelyLoaded = await import("./startup-grace");

    expect(separatelyLoaded.shouldEmitThrottledBackendUnavailableLog(t0 + 1)).toBe(false);
  });
});
