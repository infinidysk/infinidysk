import type { SettingsTab } from "~/routes/settings/settings-tabs";

export const NAV_FEATURE_IDS = [
  "overview",
  "queue",
  "watchdog",
  "watchtower",
  "explore",
  "health",
  "logs",
  "search",
  "settings.usenet",
  "settings.indexers",
  "settings.profiles",
  "settings.watchdog",
  "settings.preflight",
  "settings.watchtower",
  "settings.warden",
  "settings.sabnzbd",
  "settings.webdav",
  "settings.arrs",
  "settings.repairs",
  "settings.rclone",
  "settings.maintenance",
  "settings.backup",
  "settings.support",
] as const;

export type NavFeatureId = (typeof NAV_FEATURE_IDS)[number];

export type ServiceProviderConfig = {
  name: string;
  url: string;
  disabledFeatures: NavFeatureId[];
};

const NAV_FEATURE_ID_SET = new Set<string>(NAV_FEATURE_IDS);

export function isNavFeatureId(value: string): value is NavFeatureId {
  return NAV_FEATURE_ID_SET.has(value);
}

/**
 * Overview is the app's landing page and the fallback destination used when
 * closing the "feature not available" notice. It must always stay reachable,
 * so it cannot be disabled even though it's a valid nav feature identifier.
 */
export const NON_DISABLEABLE_FEATURE_ID = "overview" satisfies NavFeatureId;

export function isFeatureDisabled(
  config: ServiceProviderConfig | null | undefined,
  featureId: string,
): boolean {
  return config?.disabledFeatures.includes(featureId as NavFeatureId) ?? false;
}

export function isSettingsTabDisabled(
  config: ServiceProviderConfig | null | undefined,
  tab: SettingsTab,
): boolean {
  return isFeatureDisabled(config, `settings.${tab}`);
}

export function isNavRouteDisabled(
  config: ServiceProviderConfig | null | undefined,
  pathname: string,
): boolean {
  const featureId = pathname.split("/").filter(Boolean)[0];
  return featureId ? isFeatureDisabled(config, featureId) : false;
}
