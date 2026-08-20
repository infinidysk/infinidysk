import { describe, expect, it } from "vitest";
import { hasConfiguredArrs, isArrHealthEnabled } from "./has-configured-arrs";

describe("hasConfiguredArrs", () => {
    it.each([
        [undefined, false],
        ["", false],
        ["not json", false],
        ["{}", false],
        ['{"RadarrInstances":[],"SonarrInstances":[]}', false],
        ['{"RadarrInstances":[{"Host":"http://radarr","Enabled":false}],"SonarrInstances":[]}', false],
        ['{"SonarrInstances":[{"Host":"http://sonarr","Enabled":false}]}', false],
        ['{"RadarrInstances":[{"Host":"http://radarr"}],"SonarrInstances":[]}', true],
        ['{"RadarrInstances":[{"Host":"http://radarr","Enabled":true}],"SonarrInstances":[]}', true],
        ['{"RadarrInstances":[],"SonarrInstances":[{"Host":"http://sonarr"}]}', true],
        ['{"RadarrInstances":[{"Enabled":false}],"SonarrInstances":[{"Host":"http://sonarr","Enabled":true}]}', true],
    ])("returns %s for %j", (configValue, expected) => {
        expect(hasConfiguredArrs(configValue)).toBe(expected);
    });
});

describe("isArrHealthEnabled", () => {
    it.each([
        [undefined, true],
        ["", true],
        ["true", true],
        ["false", false],
        ["FALSE", false],
    ])("returns %s for %j", (configValue, expected) => {
        expect(isArrHealthEnabled(configValue)).toBe(expected);
    });
});
