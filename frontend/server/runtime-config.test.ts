import { beforeEach, describe, expect, it, vi } from "vitest";
import { FRONTEND_BACKEND_API_KEY_ERROR, readFrontendRuntimeConfig } from "./runtime-config";

describe("readFrontendRuntimeConfig", () => {
  it.each([
    ["missing", undefined],
    ["empty", ""],
    ["spaces", "   "],
    ["mixed whitespace", " \t\r\n "],
  ] as const)("rejects %s FRONTEND_BACKEND_API_KEY", (_name, value) => {
    const environment: NodeJS.ProcessEnv = {};
    if (value !== undefined) environment["FRONTEND_BACKEND_API_KEY"] = value;

    expect(() => readFrontendRuntimeConfig(environment)).toThrowError(
      FRONTEND_BACKEND_API_KEY_ERROR,
    );
  });

  it("returns the valid key unchanged", () => {
    const frontendBackendApiKey = "unit-test-shared-key";

    expect(readFrontendRuntimeConfig({ FRONTEND_BACKEND_API_KEY: frontendBackendApiKey })).toEqual({
      frontendBackendApiKey,
    });
  });

  it("preserves leading and trailing whitespace when the trimmed value is nonblank", () => {
    const frontendBackendApiKey = "  keep-these-bytes  ";

    expect(readFrontendRuntimeConfig({ FRONTEND_BACKEND_API_KEY: frontendBackendApiKey })).toEqual({
      frontendBackendApiKey,
    });
  });
});

describe("installFrontendRuntimeConfig", () => {
  beforeEach(() => {
    vi.resetModules();
  });

  it("installs once and returns the same frozen config", async () => {
    const { getFrontendRuntimeConfig: getConfig, installFrontendRuntimeConfig: install } =
      await import("./runtime-config");
    const config = Object.freeze({ frontendBackendApiKey: "installer-key" });

    install(config);
    expect(getConfig()).toEqual(config);
    install(config);
    expect(getConfig()).toBe(getConfig());
  });

  it("treats reinstalling the same key as a no-op", async () => {
    const { getFrontendRuntimeConfig: getConfig, installFrontendRuntimeConfig: install } =
      await import("./runtime-config");

    install({ frontendBackendApiKey: "same-key" });
    install({ frontendBackendApiKey: "same-key" });
    expect(getConfig()).toEqual({ frontendBackendApiKey: "same-key" });
  });

  it("throws before install without including a credential", async () => {
    const { getFrontendRuntimeConfig: getConfig } = await import("./runtime-config");

    expect(() => getConfig()).toThrowError(
      "Frontend runtime configuration has not been initialized.",
    );
  });

  it("rejects a different installed value without including either key", async () => {
    const { installFrontendRuntimeConfig: install } = await import("./runtime-config");

    install({ frontendBackendApiKey: "first-key" });
    expect(() => install({ frontendBackendApiKey: "second-key" })).toThrowError(
      "Frontend runtime configuration is already initialized.",
    );
  });
});
