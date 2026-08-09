import { logger } from "../../server/logger";
import {
  isNavFeatureId,
  NON_DISABLEABLE_FEATURE_ID,
  type NavFeatureId,
  type ServiceProviderConfig,
} from "./service-provider";

type ParsedServiceProvider = {
  config: ServiceProviderConfig;
  ignoredFeatures: string[];
  rejectedFeatures: string[];
  supportUrlError?: string;
};

let cachedRawValue: string | undefined;
let cachedConfig: ServiceProviderConfig | null = null;

function parseUrl(value: unknown, fieldName = "url"): string {
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`"${fieldName}" must be a non-empty string`);
  }

  const url = new URL(value.trim());
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new Error(`"${fieldName}" must use http or https`);
  }

  return url.toString();
}

function parseConfig(rawValue: string): ParsedServiceProvider {
  const value: unknown = JSON.parse(rawValue);
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("the value must be a JSON object");
  }

  const candidate = value as Record<string, unknown>;
  if (typeof candidate["name"] !== "string" || !candidate["name"].trim()) {
    throw new Error('"name" must be a non-empty string');
  }
  if (
    !Array.isArray(candidate["disabledFeatures"])
    || !candidate["disabledFeatures"].every((feature) => typeof feature === "string")
  ) {
    throw new Error('"disabledFeatures" must be an array of strings');
  }

  const disabledFeatures: NavFeatureId[] = [];
  const ignoredFeatures: string[] = [];
  const rejectedFeatures: string[] = [];
  for (const feature of new Set(candidate["disabledFeatures"])) {
    if (feature === NON_DISABLEABLE_FEATURE_ID) {
      rejectedFeatures.push(feature);
    } else if (isNavFeatureId(feature)) {
      disabledFeatures.push(feature);
    } else {
      ignoredFeatures.push(feature);
    }
  }

  // supportUrl is optional; a bad value must not discard the whole config,
  // otherwise a typo would silently turn off all feature gating.
  let supportUrl: string | undefined;
  let supportUrlError: string | undefined;
  if (candidate["supportUrl"] !== undefined) {
    try {
      supportUrl = parseUrl(candidate["supportUrl"], "supportUrl");
    } catch (error) {
      supportUrlError = error instanceof Error ? error.message : String(error);
    }
  }

  return {
    config: {
      name: candidate["name"].trim(),
      url: parseUrl(candidate["url"]),
      ...(supportUrl !== undefined ? { supportUrl } : {}),
      disabledFeatures,
    },
    ignoredFeatures,
    rejectedFeatures,
    ...(supportUrlError !== undefined ? { supportUrlError } : {}),
  };
}

export function getServiceProvider(): ServiceProviderConfig | null {
  const rawValue = process.env["SERVICE_PROVIDER"]?.trim();
  if (rawValue === cachedRawValue) {
    return cachedConfig;
  }

  cachedRawValue = rawValue;
  cachedConfig = null;
  if (!rawValue) {
    return null;
  }

  try {
    const { config, ignoredFeatures, rejectedFeatures, supportUrlError } = parseConfig(rawValue);
    cachedConfig = config;
    if (supportUrlError) {
      logger.warn(
        `SERVICE_PROVIDER "supportUrl" is invalid and will be ignored (feature gating stays active). Reason: ${supportUrlError}`,
      );
    }
    if (rejectedFeatures.length > 0) {
      logger.warn(
        `SERVICE_PROVIDER cannot disable "${NON_DISABLEABLE_FEATURE_ID}" because it is the fallback landing page; ignoring.`,
      );
    }
    if (ignoredFeatures.length > 0) {
      logger.warn(
        `SERVICE_PROVIDER contains unknown disabled feature identifiers; ignoring: ${ignoredFeatures.join(", ")}`,
      );
    }
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    logger.warn(`SERVICE_PROVIDER is invalid and will be ignored. Reason: ${reason}`);
  }

  return cachedConfig;
}

export function resetServiceProviderCache(): void {
  cachedRawValue = undefined;
  cachedConfig = null;
}
