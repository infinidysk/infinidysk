import { describe, expect, it } from "vitest";
import { isIndexersSettingsUpdated, isIndexersSettingsValid } from "./indexers";

const validConfig: Record<string, string> = {
    "indexers.instances": JSON.stringify({
        Indexers: [
            {
                Name: "Prowlarr indexer",
                Url: "http://prowlarr:9696/7/api",
                ApiKey: "prowlarr-key",
                Enabled: true,
                ProwlarrIndexerId: 7,
            },
        ],
    }),
    "api.user-agent": "",
    "api.search-user-agent": "",
    "search.exclude-patterns": "",
    "search.exclude-sync-urls": "",
    "search.exclude-sync-refresh-minutes": "720",
    "prowlarr.url": "http://prowlarr:9696/prowlarr",
    "prowlarr.api-key": "prowlarr-key",
    "prowlarr.sync-enabled": "true",
    "prowlarr.sync-interval-minutes": "60",
};

describe("Indexer settings", () => {
    it("accepts Prowlarr pull-sync settings and managed-indexer metadata", () => {
        expect(isIndexersSettingsValid(validConfig)).toBe(true);
    });

    it("detects changes to every Prowlarr setting", () => {
        for (const key of [
            "prowlarr.url",
            "prowlarr.api-key",
            "prowlarr.sync-enabled",
            "prowlarr.sync-interval-minutes",
        ]) {
            expect(isIndexersSettingsUpdated(validConfig, {
                ...validConfig,
                [key]: key === "prowlarr.sync-enabled" ? "false" : "changed",
            })).toBe(true);
        }
        expect(isIndexersSettingsUpdated(validConfig, { ...validConfig })).toBe(false);
    });

    it("rejects unsafe Prowlarr URLs and out-of-range sync intervals", () => {
        for (const url of [
            "not-a-url",
            "ftp://prowlarr:9696",
            "https://user:secret@prowlarr:9696",
            "http://prowlarr:9696/?x=1",
            "http://prowlarr:9696/#fragment",
        ]) {
            expect(isIndexersSettingsValid({ ...validConfig, "prowlarr.url": url })).toBe(false);
        }

        for (const interval of ["0", "4", "10081", "1.5"]) {
            expect(isIndexersSettingsValid({
                ...validConfig,
                "prowlarr.sync-interval-minutes": interval,
            })).toBe(false);
        }
    });

    it("requires the Prowlarr URL and API key as a pair", () => {
        expect(isIndexersSettingsValid({ ...validConfig, "prowlarr.url": "" })).toBe(false);
        expect(isIndexersSettingsValid({ ...validConfig, "prowlarr.api-key": "" })).toBe(false);
        expect(isIndexersSettingsValid({
            ...validConfig,
            "prowlarr.url": "",
            "prowlarr.api-key": "",
        })).toBe(true);
    });
});
