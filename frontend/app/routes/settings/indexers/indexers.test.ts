import { describe, expect, it } from "vitest";
import { isIndexersSettingsUpdated, isIndexersSettingsValid } from "./indexers";

type IndexerConnection = {
  Name: string;
  Url: string;
  ApiKey: string;
  Enabled: boolean;
  ProwlarrIndexerId?: number;
  MaxResponseBytes?: number;
};

type IndexerInstances = {
  MaxResponseBytes?: number;
  Indexers: IndexerConnection[];
};

const validIndexer: IndexerConnection = {
  Name: "Prowlarr indexer",
  Url: "http://prowlarr:9696/7/api",
  ApiKey: "prowlarr-key",
  Enabled: true,
  ProwlarrIndexerId: 7,
};

const validIndexers: IndexerInstances = { Indexers: [validIndexer] };

const validConfig: Record<string, string> = {
  "indexers.instances": JSON.stringify(validIndexers),
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
      expect(
        isIndexersSettingsUpdated(validConfig, {
          ...validConfig,
          [key]: key === "prowlarr.sync-enabled" ? "false" : "changed",
        }),
      ).toBe(true);
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
      expect(
        isIndexersSettingsValid({
          ...validConfig,
          "prowlarr.sync-interval-minutes": interval,
        }),
      ).toBe(false);
    }
  });

  it("rejects MaxResponseBytes of zero or above the hard clamp", () => {
    const hardMax = 16 * 1024 * 1024;
    const withGlobal: Record<string, string> = {
      ...validConfig,
      "indexers.instances": JSON.stringify({
        MaxResponseBytes: 0,
        Indexers: validIndexers.Indexers,
      } satisfies IndexerInstances),
    };
    expect(isIndexersSettingsValid(withGlobal)).toBe(false);

    const withPerIndexer: Record<string, string> = {
      ...validConfig,
      "indexers.instances": JSON.stringify({
        Indexers: [{ ...validIndexer, MaxResponseBytes: hardMax + 1 }],
      } satisfies IndexerInstances),
    };
    expect(isIndexersSettingsValid(withPerIndexer)).toBe(false);
  });

  it("accepts MaxResponseBytes at the hard clamp", () => {
    const hardMax = 16 * 1024 * 1024;
    const next: Record<string, string> = {
      ...validConfig,
      "indexers.instances": JSON.stringify({
        MaxResponseBytes: hardMax,
        Indexers: validIndexers.Indexers,
      } satisfies IndexerInstances),
    };
    expect(isIndexersSettingsValid(next)).toBe(true);
  });
});
