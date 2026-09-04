import { beforeEach, describe, expect, it } from "vitest";
import {
  isRcloneProxyWarningActive,
  isRcloneUserAgent,
  observeRcloneProxyRequest,
  recordRcloneProxyRequest,
  RCLONE_PROXY_WARNING_INTERVAL_MS,
  resetRcloneProxyWarningForTests,
} from "./rclone-proxy-warning.server";

describe("rclone proxy warning", () => {
  beforeEach(() => {
    resetRcloneProxyWarningForTests();
  });

  it.each(["rclone/v1.70.3", "RCLONE/v1.71.0", "rclone test"])(
    "recognizes the rclone user agent %s",
    (userAgent) => {
      expect(isRcloneUserAgent(userAgent)).toBe(true);
    },
  );

  it.each([undefined, "", "Mozilla/5.0", "my-rclone-client/1.0"])(
    "ignores unrelated user agent %s",
    (userAgent) => {
      expect(isRcloneUserAgent(userAgent)).toBe(false);
    },
  );

  it("logs the first detection and reports suppressed detections after the interval", () => {
    const now = 1_000_000;

    expect(recordRcloneProxyRequest("rclone/v1.70.3", now)).toEqual({
      detected: true,
      shouldLog: true,
      suppressed: 0,
    });
    expect(recordRcloneProxyRequest("rclone/v1.70.3", now + 1)).toEqual({
      detected: true,
      shouldLog: false,
      suppressed: 0,
    });
    expect(recordRcloneProxyRequest("rclone/v1.70.3", now + 2)).toEqual({
      detected: true,
      shouldLog: false,
      suppressed: 0,
    });
    expect(
      recordRcloneProxyRequest("rclone/v1.70.3", now + RCLONE_PROXY_WARNING_INTERVAL_MS),
    ).toEqual({ detected: true, shouldLog: true, suppressed: 2 });
  });

  it("keeps the warning active while detections continue and expires after the interval", () => {
    const now = 1_000_000;

    recordRcloneProxyRequest("rclone/v1.70.3", now);
    expect(isRcloneProxyWarningActive(now + RCLONE_PROXY_WARNING_INTERVAL_MS - 1)).toBe(true);

    recordRcloneProxyRequest("rclone/v1.70.3", now + RCLONE_PROXY_WARNING_INTERVAL_MS - 1);
    expect(isRcloneProxyWarningActive(now + RCLONE_PROXY_WARNING_INTERVAL_MS * 2 - 2)).toBe(true);
    expect(isRcloneProxyWarningActive(now + RCLONE_PROXY_WARNING_INTERVAL_MS * 2)).toBe(false);
  });

  it("does not activate for unrelated clients", () => {
    expect(recordRcloneProxyRequest("Mozilla/5.0", 1_000_000).detected).toBe(false);
    expect(isRcloneProxyWarningActive(1_000_000)).toBe(false);
  });

  it("emits one warning per interval and reports suppressed detections", () => {
    const warnings: string[] = [];
    const warn = (message: string) => warnings.push(message);
    const now = 1_000_000;

    expect(observeRcloneProxyRequest("rclone/v1.70.3", warn, now)).toBe(true);
    observeRcloneProxyRequest("rclone/v1.70.3", warn, now + 1);
    observeRcloneProxyRequest("rclone/v1.70.3", warn, now + 2);
    observeRcloneProxyRequest("rclone/v1.70.3", warn, now + RCLONE_PROXY_WARNING_INTERVAL_MS);

    expect(warnings).toHaveLength(2);
    expect(warnings[0]).toContain("frontend proxy on port 3000");
    expect(warnings[0]).toContain("backend port 8080");
    expect(warnings[1]).toContain("Suppressed 2 repeated detection(s)");
  });
});
