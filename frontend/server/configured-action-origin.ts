import type express from "express";
import { backendClient } from "~/clients/backend-client.server";
import { logger } from "./logger";
import { getTrustProxyEnvironmentOverride } from "./trust-proxy-env.server";

const SUCCESS_CACHE_TTL_MS = 5_000;
const FAILURE_CACHE_TTL_MS = 1_000;
const CONFIG_READ_TIMEOUT_MS = 2_000;

type ProxySettings = {
  available: boolean;
  baseUrl: string | null;
  trustProxy: boolean;
};

let cachedSettings: ProxySettings | undefined;
let cacheExpiresAt = 0;
let pendingSettings: Promise<ProxySettings> | undefined;
let cacheGeneration = 0;

export function matchConfiguredActionOrigin(
  req: { method: string; get(name: "origin"): string | undefined },
  configuredBaseUrl: string | null | undefined,
): string | undefined {
  if (req.method === "GET" || req.method === "HEAD" || !configuredBaseUrl) return undefined;

  const requestOrigin = req.get("origin");
  if (!requestOrigin) return undefined;

  try {
    const parsedRequestOrigin = new URL(requestOrigin);
    const browserOrigin = parsedRequestOrigin.origin;
    if (requestOrigin !== browserOrigin) return undefined;

    const configuredUrl = new URL(configuredBaseUrl);
    if (configuredUrl.protocol !== "http:" && configuredUrl.protocol !== "https:") {
      return undefined;
    }

    const configuredOrigin = configuredUrl.origin;
    return browserOrigin === configuredOrigin ? configuredOrigin : undefined;
  } catch {
    return undefined;
  }
}

export async function resolveConfiguredActionOrigin(
  req: express.Request,
): Promise<string | null | undefined> {
  if (req.method === "GET" || req.method === "HEAD" || !req.get("origin")) return undefined;

  const settings = await refreshProxySettings();
  if (!settings.available) return null;
  if (!settings.baseUrl) return undefined;

  const matchingOrigin = matchConfiguredActionOrigin(req, settings.baseUrl);
  if (matchingOrigin) return matchingOrigin;
  return null;
}

export function isTrustProxyEnabled(): boolean {
  return getTrustProxyEnvironmentOverride() ?? cachedSettings?.trustProxy ?? false;
}

export async function refreshProxySettings(now = Date.now()): Promise<ProxySettings> {
  if (cachedSettings !== undefined && now < cacheExpiresAt) return cachedSettings;
  if (pendingSettings) return pendingSettings;

  const generation = cacheGeneration;
  const request = loadProxySettings();
  const pending = request.then(({ settings, cacheTtlMs }) => {
    if (cacheGeneration !== generation) return refreshProxySettings();

    cachedSettings = settings;
    cacheExpiresAt = Date.now() + cacheTtlMs;
    return settings;
  });
  const finalized = pending.finally(() => {
    if (pendingSettings === finalized) pendingSettings = undefined;
  });
  pendingSettings = finalized;
  return pendingSettings;
}

async function loadProxySettings(): Promise<{
  settings: ProxySettings;
  cacheTtlMs: number;
}> {
  try {
    const config = await backendClient.getConfig(
      ["general.base-url", "general.trust-proxy"],
      AbortSignal.timeout(CONFIG_READ_TIMEOUT_MS),
    );
    const baseUrl = config
      .find((item) => item.configName === "general.base-url")
      ?.configValue?.trim();
    const trustProxy = config
      .find((item) => item.configName === "general.trust-proxy")
      ?.configValue?.trim();
    return {
      settings: {
        available: true,
        baseUrl: baseUrl ? new URL(baseUrl).origin : null,
        trustProxy: trustProxy?.toLowerCase() === "true",
      },
      cacheTtlMs: SUCCESS_CACHE_TTL_MS,
    };
  } catch (error) {
    logger.debug("Could not read reverse-proxy settings", error);
    return {
      settings: { available: false, baseUrl: null, trustProxy: false },
      cacheTtlMs: FAILURE_CACHE_TTL_MS,
    };
  }
}

export function invalidateProxySettingsCache(): void {
  cacheGeneration += 1;
  cachedSettings = undefined;
  cacheExpiresAt = 0;
  pendingSettings = undefined;
}
