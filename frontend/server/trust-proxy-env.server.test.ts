import { afterEach, describe, expect, it, vi } from "vitest";
import {
  getTrustProxyEnvironmentOverride,
  parseTrustProxyEnvironment,
} from "./trust-proxy-env.server";

afterEach(() => {
  vi.unstubAllEnvs();
});

describe("parseTrustProxyEnvironment", () => {
  it.each(["1", "true", "TRUE", "yes", " Yes "])("enables trust for %s", (value) => {
    expect(parseTrustProxyEnvironment(value)).toBe(true);
  });

  it.each(["0", "false", "no", "invalid", ""])("disables trust for %s", (value) => {
    expect(parseTrustProxyEnvironment(value)).toBe(false);
  });

  it("leaves the GUI setting authoritative when the variable is absent", () => {
    expect(parseTrustProxyEnvironment(undefined)).toBeUndefined();
  });

  it("reads the legacy environment override at runtime", () => {
    vi.stubEnv("TRUST_PROXY", "yes");
    expect(getTrustProxyEnvironmentOverride()).toBe(true);

    vi.stubEnv("TRUST_PROXY", "false");
    expect(getTrustProxyEnvironmentOverride()).toBe(false);
  });
});
