import { describe, expect, it } from "vitest";
import { getChangedConfig } from "./route";

describe("settings change persistence", () => {
  it("includes repair settings in the save payload", () => {
    const config = {
      "repair.par2-enabled": "true",
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
    };
    const changedRepairs = {
      "repair.par2-enabled": "false",
      "repair.par2-preferred-over-arr": "false",
      "repair.par2-max-missing-slices": "12",
      "repair.par2-max-release-gb": "24",
      "repair.par2-max-memory-mb": "512",
      "repair.par2-max-patch-gb": "8",
      "repair.par2-fetch-concurrency": "4",
      "repair.par2-failure-cooldown-hours": "12",
      "repair.degraded-tolerance-enabled": "false",
      "repair.corruption-tracking-enabled": "false",
      "repair.degraded-max-consecutive-missing": "3",
      "repair.degraded-max-total-missing": "10",
      "repair.degraded-max-missing-byte-percent": "2.5",
    };

    expect(getChangedConfig(config, { ...config, ...changedRepairs })).toEqual(changedRepairs);
  });
});
