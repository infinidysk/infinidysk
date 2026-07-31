import { describe, expect, it } from "vitest";
import { hasConfiguredIndexers } from "./has-configured-indexers";

describe("hasConfiguredIndexers", () => {
    it.each([
        [undefined, false],
        ["", false],
        ["not json", false],
        ["{}", false],
        ['{"Indexers":[]}', false],
        ['{"Indexers":[{"Name":"Disabled","Enabled":false}]}', true],
        ['{"Indexers":[{"Name":"Enabled","Enabled":true}]}', true],
    ])("returns %s for %j", (configValue, expected) => {
        expect(hasConfiguredIndexers(configValue)).toBe(expected);
    });
});
