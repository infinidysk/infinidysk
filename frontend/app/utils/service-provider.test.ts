import { describe, expect, it } from "vitest";
import {
  isFeatureDisabled,
  isNavFeatureId,
  isNavRouteDisabled,
  isSettingsTabDisabled,
  type ServiceProviderConfig,
} from "./service-provider";

function configWith(
  disabledFeatures: ServiceProviderConfig["disabledFeatures"],
): ServiceProviderConfig {
  return { name: "ElfHosted", url: "https://elfhosted.com", disabledFeatures };
}

describe("isNavFeatureId", () => {
  it.each([
    "overview",
    "watchtower",
    "search",
    "settings.rclone",
    "settings.queue",
    "settings.streaming",
    "settings.migration",
  ])("accepts known identifier %s", (value) => {
    expect(isNavFeatureId(value)).toBe(true);
  });

  it.each(["", "watchtower ", "Watchtower", "unknown-feature"])(
    "rejects unknown identifier %s",
    (value) => {
      expect(isNavFeatureId(value)).toBe(false);
    },
  );
});

describe("isFeatureDisabled", () => {
  it("returns false when config is null or undefined", () => {
    expect(isFeatureDisabled(null, "search")).toBe(false);
    expect(isFeatureDisabled(undefined, "search")).toBe(false);
  });

  it("returns false when the feature is not in disabledFeatures", () => {
    expect(isFeatureDisabled(configWith(["watchtower"]), "search")).toBe(false);
  });

  it("returns true when the feature is in disabledFeatures", () => {
    expect(isFeatureDisabled(configWith(["watchtower", "search"]), "search")).toBe(true);
  });
});

describe("isSettingsTabDisabled", () => {
  it("checks the settings.<tab> namespaced identifier", () => {
    const config = configWith(["settings.rclone"]);
    expect(isSettingsTabDisabled(config, "rclone")).toBe(true);
    expect(isSettingsTabDisabled(config, "usenet")).toBe(false);
  });

  it("does not confuse a settings tab with a same-named top-level route", () => {
    const config = configWith(["watchtower"]);
    expect(isSettingsTabDisabled(config, "watchtower")).toBe(false);
  });

  it("returns false when config is null", () => {
    expect(isSettingsTabDisabled(null, "rclone")).toBe(false);
  });

  it.each(["queue", "streaming", "migration"] as const)(
    "supports the %s settings feature id",
    (tab) => {
      const config = configWith([`settings.${tab}`]);
      expect(isSettingsTabDisabled(config, tab)).toBe(true);
    },
  );
});

describe("isNavRouteDisabled", () => {
  it("checks the first path segment against disabledFeatures", () => {
    const config = configWith(["watchtower"]);
    expect(isNavRouteDisabled(config, "/watchtower")).toBe(true);
    expect(isNavRouteDisabled(config, "/watchtower/foo")).toBe(true);
    expect(isNavRouteDisabled(config, "/queue")).toBe(false);
  });

  it("does not confuse a top-level route with a same-named settings tab", () => {
    const config = configWith(["settings.watchtower"]);
    expect(isNavRouteDisabled(config, "/watchtower")).toBe(false);
  });

  it("returns false for the root path", () => {
    expect(isNavRouteDisabled(configWith(["overview"]), "/")).toBe(false);
  });

  it("returns false when config is null", () => {
    expect(isNavRouteDisabled(null, "/watchtower")).toBe(false);
  });
});
