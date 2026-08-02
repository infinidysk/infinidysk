import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { warnMock } = vi.hoisted(() => ({
  warnMock: vi.fn(),
}));

vi.mock("../../server/logger", () => ({
  logger: {
    warn: warnMock,
  },
}));

import {
  getServiceProvider,
  resetServiceProviderCache,
} from "./service-provider.server";

const originalServiceProvider = process.env.SERVICE_PROVIDER;

beforeEach(() => {
  delete process.env.SERVICE_PROVIDER;
  resetServiceProviderCache();
  warnMock.mockReset();
});

afterEach(() => {
  if (originalServiceProvider === undefined) {
    delete process.env.SERVICE_PROVIDER;
  } else {
    process.env.SERVICE_PROVIDER = originalServiceProvider;
  }
  resetServiceProviderCache();
});

describe("getServiceProvider", () => {
  it("returns null when SERVICE_PROVIDER is unset", () => {
    expect(getServiceProvider()).toBeNull();
    expect(warnMock).not.toHaveBeenCalled();
  });

  it("parses and normalizes a valid configuration", () => {
    process.env.SERVICE_PROVIDER = JSON.stringify({
      name: " ElfHosted ",
      url: "https://elfhosted.com",
      disabledFeatures: ["search", "settings.rclone", "search"],
    });

    expect(getServiceProvider()).toEqual({
      name: "ElfHosted",
      url: "https://elfhosted.com/",
      disabledFeatures: ["search", "settings.rclone"],
    });
    expect(warnMock).not.toHaveBeenCalled();
  });

  it("ignores unknown feature identifiers without discarding the provider", () => {
    process.env.SERVICE_PROVIDER = JSON.stringify({
      name: "Example Hosting",
      url: "https://example.com",
      disabledFeatures: ["search", "future-feature"],
    });

    expect(getServiceProvider()?.disabledFeatures).toEqual(["search"]);
    expect(warnMock).toHaveBeenCalledWith(
      "SERVICE_PROVIDER contains unknown disabled feature identifiers; ignoring: future-feature",
    );
  });

  it("rejects disabling overview so the fallback landing page always stays reachable", () => {
    process.env.SERVICE_PROVIDER = JSON.stringify({
      name: "Example Hosting",
      url: "https://example.com",
      disabledFeatures: ["overview", "search"],
    });

    expect(getServiceProvider()?.disabledFeatures).toEqual(["search"]);
    expect(warnMock).toHaveBeenCalledWith(
      'SERVICE_PROVIDER cannot disable "overview" because it is the fallback landing page; ignoring.',
    );
  });

  it.each([
    ["malformed JSON", "{"],
    ["missing name", JSON.stringify({ url: "https://example.com", disabledFeatures: [] })],
    ["unsafe URL", JSON.stringify({ name: "Example", url: "javascript:alert(1)", disabledFeatures: [] })],
    ["invalid feature list", JSON.stringify({ name: "Example", url: "https://example.com", disabledFeatures: "search" })],
  ])("ignores %s", (_description, rawValue) => {
    process.env.SERVICE_PROVIDER = rawValue;

    expect(getServiceProvider()).toBeNull();
    expect(warnMock).toHaveBeenCalledOnce();
    expect(warnMock.mock.calls[0][0]).toContain(
      "SERVICE_PROVIDER is invalid and will be ignored. Reason:",
    );
  });

  it("caches parsing and warnings for an unchanged value", () => {
    process.env.SERVICE_PROVIDER = "{";

    expect(getServiceProvider()).toBeNull();
    expect(getServiceProvider()).toBeNull();
    expect(warnMock).toHaveBeenCalledOnce();
  });
});
