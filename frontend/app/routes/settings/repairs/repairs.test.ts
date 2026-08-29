import { describe, expect, it } from "vitest";
import { isRepairsSettingsUpdated, isRepairsSettingsValid } from "./repairs";

const baseConfig: Record<string, string> = {
  "repair.enable": "true",
  "repair.healthcheck-concurrency": "50",
  "repair.healthcheck-workers": "1",
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
  "repair.healthcheck-schedule": "",
  "repair.action-schedule": "",
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

  it("detects and validates health scheduling changes", () => {
    expect(
      isRepairsSettingsUpdated(baseConfig, {
        ...baseConfig,
        "repair.healthcheck-workers": "2",
      }),
    ).toBe(true);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.healthcheck-concurrency": "200",
        "repair.healthcheck-workers": "8",
      }),
    ).toBe(true);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.healthcheck-concurrency": "201",
      }),
    ).toBe(true);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.healthcheck-concurrency": "0",
      }),
    ).toBe(true);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.healthcheck-concurrency": "not-a-number",
      }),
    ).toBe(false);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.healthcheck-workers": "9",
      }),
    ).toBe(false);
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.healthcheck-workers": "",
      }),
    ).toBe(false);
    const withoutWorkers = { ...baseConfig };
    delete withoutWorkers["repair.healthcheck-workers"];
    expect(isRepairsSettingsValid(withoutWorkers)).toBe(true);
  });

  it("accepts only signed 64-bit health concurrency values", () => {
    for (const value of ["-9223372036854775808", "9223372036854775807"]) {
      expect(
        isRepairsSettingsValid({
          ...baseConfig,
          "repair.healthcheck-concurrency": value,
        }),
      ).toBe(true);
    }
    for (const value of ["-9223372036854775809", "9223372036854775808"]) {
      expect(
        isRepairsSettingsValid({
          ...baseConfig,
          "repair.healthcheck-concurrency": value,
        }),
      ).toBe(false);
    }
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

  it("rejects invalid health-check schedules", () => {
    expect(
      isRepairsSettingsValid({
        ...baseConfig,
        "repair.healthcheck-schedule": '{"Enabled":true,"Windows":[]}',
      }),
    ).toBe(false);
  });
});
