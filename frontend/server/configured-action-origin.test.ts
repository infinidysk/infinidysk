import type express from "express";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { backendClient } from "~/clients/backend-client.server";
import {
  invalidateProxySettingsCache,
  isTrustProxyEnabled,
  matchConfiguredActionOrigin,
  refreshProxySettings,
  resolveConfiguredActionOrigin,
} from "./configured-action-origin";
import { logger } from "./logger";

function request(method: string, origin?: string) {
  return {
    method,
    protocol: "http",
    get: (name: string) => (name.toLowerCase() === "origin" ? origin : undefined),
  };
}

function proxiedRequest(): express.Request {
  return {
    method: "POST",
    protocol: "http",
    get: (name: string) => {
      if (name.toLowerCase() === "origin") return "https://nzbdav.example.com";
      if (name.toLowerCase() === "host") return "internal-container:3000";
      return undefined;
    },
  } as express.Request;
}

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

beforeEach(() => {
  invalidateProxySettingsCache();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

describe("matchConfiguredActionOrigin", () => {
  it("trusts the configured Base URL when its origin matches the browser", () => {
    expect(
      matchConfiguredActionOrigin(
        request("POST", "https://nzbdav.example.com"),
        "https://nzbdav.example.com/app",
      ),
    ).toBe("https://nzbdav.example.com");
  });

  it("does not trust a different browser origin", () => {
    expect(
      matchConfiguredActionOrigin(
        request("POST", "https://attacker.example"),
        "https://nzbdav.example.com",
      ),
    ).toBeUndefined();
  });

  it("does not trust a different browser scheme", () => {
    expect(
      matchConfiguredActionOrigin(
        request("POST", "http://nzbdav.example.com"),
        "https://nzbdav.example.com",
      ),
    ).toBeUndefined();
  });

  it("does not override safe requests", () => {
    expect(
      matchConfiguredActionOrigin(
        request("GET", "https://nzbdav.example.com"),
        "https://nzbdav.example.com",
      ),
    ).toBeUndefined();
  });

  it.each([
    ["https://nzbdav.example.com/path", "https://nzbdav.example.com"],
    ["https://user@nzbdav.example.com", "https://nzbdav.example.com"],
    ["https://nzbdav.example.com?query", "https://nzbdav.example.com"],
    ["https://nzbdav.example.com#fragment", "https://nzbdav.example.com"],
  ])("rejects a non-origin browser Origin value: %s", (origin, baseUrl) => {
    expect(matchConfiguredActionOrigin(request("POST", origin), baseUrl)).toBeUndefined();
  });

  it.each(["not a URL", "ftp://nzbdav.example.com"])(
    "rejects an invalid configured Base URL: %s",
    (baseUrl) => {
      expect(
        matchConfiguredActionOrigin(request("POST", "https://nzbdav.example.com"), baseUrl),
      ).toBeUndefined();
    },
  );
});

describe("resolveConfiguredActionOrigin", () => {
  it("coalesces concurrent Base URL reads", async () => {
    const configLoad = deferred<Awaited<ReturnType<typeof backendClient.getConfig>>>();
    const getConfig = vi.spyOn(backendClient, "getConfig").mockReturnValue(configLoad.promise);

    const first = resolveConfiguredActionOrigin(proxiedRequest());
    const second = resolveConfiguredActionOrigin(proxiedRequest());
    configLoad.resolve([
      { configName: "general.base-url", configValue: "https://nzbdav.example.com" },
    ]);

    await expect(Promise.all([first, second])).resolves.toEqual([
      "https://nzbdav.example.com",
      "https://nzbdav.example.com",
    ]);
    expect(getConfig).toHaveBeenCalledOnce();
  });

  it("retries promptly after a transient backend failure", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(1_000);
    vi.spyOn(logger, "debug").mockImplementation(() => {});
    const getConfig = vi
      .spyOn(backendClient, "getConfig")
      .mockRejectedValueOnce(new Error("backend unavailable"))
      .mockResolvedValueOnce([
        { configName: "general.base-url", configValue: "https://nzbdav.example.com" },
      ]);

    await expect(resolveConfiguredActionOrigin(proxiedRequest())).resolves.toBeNull();
    vi.setSystemTime(2_001);
    await expect(resolveConfiguredActionOrigin(proxiedRequest())).resolves.toBe(
      "https://nzbdav.example.com",
    );
    expect(getConfig).toHaveBeenCalledTimes(2);
  });

  it("discards stale trusted settings when a refresh fails", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(1_000);
    vi.spyOn(logger, "debug").mockImplementation(() => {});
    const getConfig = vi
      .spyOn(backendClient, "getConfig")
      .mockResolvedValueOnce([
        { configName: "general.base-url", configValue: "https://nzbdav.example.com" },
        { configName: "general.trust-proxy", configValue: "true" },
      ])
      .mockRejectedValueOnce(new Error("backend unavailable"));

    await expect(refreshProxySettings()).resolves.toMatchObject({
      available: true,
      trustProxy: true,
    });
    vi.setSystemTime(6_001);
    await expect(refreshProxySettings()).resolves.toEqual({
      available: false,
      baseUrl: null,
      trustProxy: false,
    });
    expect(isTrustProxyEnabled()).toBe(false);
    expect(getConfig).toHaveBeenCalledTimes(2);
  });

  it("rejects an alias even when Host and Origin agree", async () => {
    vi.spyOn(backendClient, "getConfig").mockResolvedValue([
      { configName: "general.base-url", configValue: "https://canonical.example.com" },
      { configName: "general.trust-proxy", configValue: "false" },
    ]);
    const aliasRequest = {
      ...proxiedRequest(),
      protocol: "https",
      get: (name: string) => {
        if (name.toLowerCase() === "origin") return "https://alias.example.com";
        if (name.toLowerCase() === "host") return "alias.example.com";
        return undefined;
      },
    } as express.Request;

    await expect(resolveConfiguredActionOrigin(aliasRequest)).resolves.toBeNull();
  });

  it("does not restore stale settings when invalidated during an in-flight read", async () => {
    const staleLoad = deferred<Awaited<ReturnType<typeof backendClient.getConfig>>>();
    const getConfig = vi
      .spyOn(backendClient, "getConfig")
      .mockReturnValueOnce(staleLoad.promise)
      .mockResolvedValueOnce([
        { configName: "general.base-url", configValue: "https://new.example.com" },
        { configName: "general.trust-proxy", configValue: "false" },
      ]);

    const staleRequest = refreshProxySettings();
    invalidateProxySettingsCache();
    const currentRequest = refreshProxySettings();
    staleLoad.resolve([
      { configName: "general.base-url", configValue: "https://old.example.com" },
      { configName: "general.trust-proxy", configValue: "true" },
    ]);

    await expect(staleRequest).resolves.toMatchObject({ available: true, trustProxy: true });
    await expect(currentRequest).resolves.toMatchObject({
      available: true,
      baseUrl: "https://new.example.com",
    });
    expect(isTrustProxyEnabled()).toBe(false);
    expect(getConfig).toHaveBeenCalledTimes(2);
  });

  it("loads and caches the persisted trust-proxy setting", async () => {
    const getConfig = vi.spyOn(backendClient, "getConfig").mockResolvedValue([
      { configName: "general.base-url", configValue: "https://nzbdav.example.com" },
      { configName: "general.trust-proxy", configValue: "true" },
    ]);

    await refreshProxySettings();
    await refreshProxySettings();

    expect(isTrustProxyEnabled()).toBe(true);
    expect(getConfig).toHaveBeenCalledOnce();
  });
});
