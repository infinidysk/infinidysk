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
        expect(isRepairsSettingsValid({
            ...baseConfig,
            "repair.par2-max-missing-slices": "0",
        })).toBe(false);
    });
});
