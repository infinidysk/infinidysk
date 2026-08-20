import { beforeEach, describe, expect, it } from "vitest";
import {
  CLIENT_ERROR_LOG_THROTTLE_MS,
  clientErrorKey,
  resetClientErrorLogThrottleForTests,
  shouldLogClientError,
} from "./request-log-throttle";

describe("request-log-throttle", () => {
  beforeEach(() => {
    resetClientErrorLogThrottleForTests();
  });

  it("logs the first line and suppresses the rest of the window", () => {
    const now = 1_000_000;

    expect(shouldLogClientError("key", now)).toEqual({ log: true, suppressed: 0 });

    for (let i = 1; i <= 50; i++) {
      expect(shouldLogClientError("key", now + i)).toEqual({ log: false, suppressed: 0 });
    }
  });

  it("reports the suppressed count on the next line once the window elapses", () => {
    const now = 1_000_000;

    shouldLogClientError("key", now);
    shouldLogClientError("key", now + 1);
    shouldLogClientError("key", now + 2);

    expect(shouldLogClientError("key", now + CLIENT_ERROR_LOG_THROTTLE_MS)).toEqual({
      log: true,
      suppressed: 2,
    });

    // The count resets once reported.
    expect(shouldLogClientError("key", now + CLIENT_ERROR_LOG_THROTTLE_MS * 2)).toEqual({
      log: true,
      suppressed: 0,
    });
  });

  it("tracks keys independently", () => {
    const now = 1_000_000;

    expect(shouldLogClientError("a", now).log).toBe(true);
    expect(shouldLogClientError("b", now).log).toBe(true);
    expect(shouldLogClientError("a", now + 1).log).toBe(false);
    expect(shouldLogClientError("b", now + 1).log).toBe(false);
  });

  it("collapses a per-release path storm onto one mount key", () => {
    const client = "10.0.0.5 Emby/4.8";

    expect(
      clientErrorKey("MKCOL", 403, "/completed-symlinks/tv-unmatched/release-1", client),
    ).toEqual(clientErrorKey("MKCOL", 403, "/completed-symlinks/tv-unmatched/release-2", client));
  });

  it("keeps different clients, methods, statuses and mounts distinct", () => {
    const path = "/completed-symlinks/tv-unmatched/release-1";
    const base = clientErrorKey("MKCOL", 403, path, "10.0.0.5 Emby/4.8");

    expect(clientErrorKey("PUT", 403, path, "10.0.0.5 Emby/4.8")).not.toEqual(base);
    expect(clientErrorKey("MKCOL", 404, path, "10.0.0.5 Emby/4.8")).not.toEqual(base);
    expect(clientErrorKey("MKCOL", 403, "/content/release-1", "10.0.0.5 Emby/4.8")).not.toEqual(
      base,
    );
    expect(clientErrorKey("MKCOL", 403, path, "10.0.0.9 Emby/4.8")).not.toEqual(base);
  });

  it("handles root and empty paths without throwing", () => {
    expect(clientErrorKey("GET", 404, "/", "c")).toBe("GET 404 / c");
    expect(clientErrorKey("GET", 404, "", "c")).toBe("GET 404 / c");
  });
});
