import { describe, expect, it } from "vitest";
import { isRepairsSettingsUpdated, isRepairsSettingsValid } from "./repairs";

const baseConfig: Record<string, string> = {
  "repair.enable": "true",
  "repair.healthcheck-concurrency": "50",
  "repair.healthcheck-depth": "standard",
  "repair.healthcheck-aging": "false",
  "repair.auto-remove-after-failures": "0",
  "repair.auto-remove-unlinked-only": "true",
  "repair.par2-enabled": "false",
  "repair.par2-preferred-over-arr": "true",
  "repair.par2-max-missing-slices": "8",
  "repair.par2-max-release-gb": "16",
  "repair.par2-max-memory-mb": "256",
  "repair.par2-max-patch-gb": "4",
  "repair.par2-fetch-concurrency": "2",
  "repair.par2-failure-cooldown-hours": "6",
  "repair.degraded-tolerance-enabled": "true",
  "repair.corruption-tracking-enabled": "true",
  "repair.degraded-max-consecutive-missing": "2",
  "repair.degraded-max-total-missing": "5",
  "repair.degraded-max-missing-byte-percent": "1.0",
  "media.library-dir": "/library",
  "arr.instances": JSON.stringify({ RadarrInstances: [{}], SonarrInstances: [] }),
};

describe("Repairs settings helpers", () => {
  it("detects PAR2 setting changes", () => {
    const updated = { ...baseConfig, "repair.par2-enabled": "true" };
    expect(isRepairsSettingsUpdated(baseConfig, updated)).toBe(true);
  });

  it("accepts valid PAR2 numeric settings", () => {
    expect(isRepairsSettingsValid(baseConfig)).toBe(true);
  });

  it("rejects invalid PAR2 numeric settings", () => {
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.par2-max-missing-slices": "0",
      }),
    ).toBe(false);
  });

  it("detects degraded tolerance setting changes", () => {
    expect(
      isRepairsSettingsUpdated(baseConfig, {
        ...baseConfig,
        "repair.degraded-tolerance-enabled": "false",
      }),
    ).toBe(true);
    expect(
      isRepairsSettingsUpdated(baseConfig, {
        ...baseConfig,
        "repair.degraded-max-consecutive-missing": "1",
      }),
    ).toBe(true);
    expect(
      isRepairsSettingsUpdated(baseConfig, {
        ...baseConfig,
        "repair.degraded-max-total-missing": "10",
      }),
    ).toBe(true);
    expect(
      isRepairsSettingsUpdated(baseConfig, {
        ...baseConfig,
        "repair.degraded-max-missing-byte-percent": "2.5",
      }),
    ).toBe(true);
    expect(isRepairsSettingsUpdated(baseConfig, baseConfig)).toBe(false);
  });

  it("detects corruption tracking setting changes", () => {
    expect(
      isRepairsSettingsUpdated(baseConfig, {
        ...baseConfig,
        "repair.corruption-tracking-enabled": "false",
      }),
    ).toBe(true);
    expect(isRepairsSettingsUpdated(baseConfig, baseConfig)).toBe(false);
  });

  it("accepts valid degraded tolerance settings, including decimal percents", () => {
    expect(isRepairsSettingsValid(baseConfig)).toBe(true);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.degraded-max-missing-byte-percent": "0.5",
      }),
    ).toBe(true);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.degraded-max-missing-byte-percent": "2.5",
      }),
    ).toBe(true);
  });

  it("rejects invalid degraded tolerance settings", () => {
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.degraded-max-consecutive-missing": "0",
      }),
    ).toBe(false);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.degraded-max-total-missing": "-3",
      }),
    ).toBe(false);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.degraded-max-missing-byte-percent": "abc",
      }),
    ).toBe(false);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.degraded-max-missing-byte-percent": "0",
      }),
    ).toBe(false);
  });
});
