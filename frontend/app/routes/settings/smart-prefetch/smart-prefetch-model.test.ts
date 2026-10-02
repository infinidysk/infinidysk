import { describe, expect, it } from "vitest";
import {
  bytesToDecimalGb,
  decimalGbToBytes,
  hasCustomizedPrefetchPolicy,
  numericFields,
  parsePrefetchSettings,
  resetPrefetchPolicyDefaults,
  validatePrefetchSettings,
  hasSmartPrefetchSettingsChanged,
  isSmartPrefetchSettingsValid,
} from "./smart-prefetch-model";
import type { DisabledPlexLibrary } from "./smart-prefetch-model";

describe("Smart Prefetch persisted settings", () => {
  it("converts the daily budget between bytes and decimal GB", () => {
    expect(bytesToDecimalGb(10_000_000_000)).toBe(10);
    expect(decimalGbToBytes(10.5)).toBe(10_500_000_000);
    expect(decimalGbToBytes(0)).toBe(0);
  });

  it("detects policy customization but ignores enablement, users and sources", () => {
    const defaults = parsePrefetchSettings(undefined);
    expect(hasCustomizedPrefetchPolicy(defaults)).toBe(false);
    expect(hasCustomizedPrefetchPolicy({ ...defaults, Enabled: true, Users: ["server:7"] })).toBe(
      false,
    );
    expect(hasCustomizedPrefetchPolicy({ ...defaults, MaxRetries: 4 })).toBe(true);
  });

  it("resets policy values while preserving enablement, users and sources", () => {
    const source = {
      ServerId: "server",
      LibraryId: "2",
      Kind: "hub",
      Key: "/hubs/recent",
      Title: "Recent",
      Type: "show",
      Enabled: true,
      Limit: 10,
      ExcludedShows: ["42"],
    };
    const reset = resetPrefetchPolicyDefaults({
      ...parsePrefetchSettings(undefined),
      Enabled: true,
      MaxRetries: 9,
      MovieEnabled: false,
      Users: ["server:7"],
      Sources: [source],
      DisabledLibraries: [{ ServerId: "server", LibraryId: "2", Type: "show" }],
    });
    expect(reset.Enabled).toBe(true);
    expect(reset.MaxRetries).toBe(3);
    expect(reset.MovieEnabled).toBe(true);
    expect(reset.Users).toEqual(["server:7"]);
    expect(reset.Sources).toEqual([source]);
    expect(reset.DisabledLibraries).toEqual([{ ServerId: "server", LibraryId: "2", Type: "show" }]);
  });

  it("defaults disabled libraries empty and round-trips stable identities", () => {
    expect(parsePrefetchSettings(undefined).DisabledLibraries).toEqual([]);
    const settings = parsePrefetchSettings(
      '{"disabledLibraries":[{"serverId":"server","libraryId":"2","type":"show"}]}',
    );
    expect(settings.DisabledLibraries).toEqual([
      { ServerId: "server", LibraryId: "2", Type: "show" },
    ]);
    expect(validatePrefetchSettings(settings)).toBeNull();
  });

  it("rejects invalid, duplicate, and over-limit disabled library identities", () => {
    expect(() => parsePrefetchSettings('{"DisabledLibraries":null}')).toThrow();
    const defaults = parsePrefetchSettings(undefined);
    expect(
      validatePrefetchSettings({
        ...defaults,
        DisabledLibraries: [
          { ServerId: "server", LibraryId: "2", Type: "episode" } as unknown as DisabledPlexLibrary,
        ],
      }),
    ).not.toBeNull();
    const identity = { ServerId: "server", LibraryId: "2", Type: "show" as const };
    expect(
      validatePrefetchSettings({ ...defaults, DisabledLibraries: [identity, identity] }),
    ).not.toBeNull();
    expect(
      validatePrefetchSettings({
        ...defaults,
        DisabledLibraries: Array.from({ length: 129 }, (_, index) => ({
          ServerId: "server",
          LibraryId: String(index),
          Type: "movie" as const,
        })),
      }),
    ).not.toBeNull();
  });

  it("exposes bounded queue lifetime, retries and verified-session expiry", () => {
    const defaults = parsePrefetchSettings(undefined);
    expect(defaults.QueueCapacity).toBe(256);
    expect(defaults.MaxRetries).toBe(3);
    expect(defaults.IntentTtlHours).toBe(24);
    expect(defaults.VerifiedSessionExpirySeconds).toBe(60);
    expect(validatePrefetchSettings({ ...defaults, MaxRetries: 0 })).toBeNull();
    expect(validatePrefetchSettings({ ...defaults, QueueCapacity: 257 })).not.toBeNull();
    expect(validatePrefetchSettings({ ...defaults, IntentTtlHours: 0 })).not.toBeNull();
    expect(
      validatePrefetchSettings({ ...defaults, VerifiedSessionExpirySeconds: 301 }),
    ).not.toBeNull();
  });
  it("supports fractional prediction confidence and bounded cooldown minutes", () => {
    expect(parsePrefetchSettings(undefined).ConfidenceThreshold).toBe(0.5);
    expect(parsePrefetchSettings(undefined).CooldownMinutes).toBe(15);
    expect(
      validatePrefetchSettings({ ...parsePrefetchSettings(undefined), ConfidenceThreshold: 0.75 }),
    ).toBeNull();
    expect(
      validatePrefetchSettings({ ...parsePrefetchSettings(undefined), ConfidenceThreshold: 1.1 }),
    ).not.toBeNull();
    expect(
      validatePrefetchSettings({ ...parsePrefetchSettings(undefined), CooldownMinutes: 0 }),
    ).not.toBeNull();
  });
  it("matches backend case-insensitive known fields and zero-budget semantics", () => {
    const settings = parsePrefetchSettings(
      '{"enabled":true,"sources":[{"serverId":"server","key":"/hubs/recent","title":"Recent"}]}',
    );
    expect(settings.Enabled).toBe(true);
    expect(settings.Sources[0]?.Kind).toBe("hub");
    expect(settings.Sources[0]?.LibraryId).toBe("");
    expect(numericFields.find((field) => field.key === "DailyByteBudget")?.label).toContain(
      "0 means unlimited",
    );
    expect(() => parsePrefetchSettings('{"Sources":[{"unknown":1}]}')).toThrow();
  });
  it("uses complete backend defaults and preserves source/user identifiers", () => {
    const defaults = parsePrefetchSettings(undefined);
    expect(defaults.Enabled).toBe(false);
    expect(defaults.RealtimeEnabled).toBe(true);
    expect(defaults.DailyByteBudget).toBe(10_000_000_000);
    const configured = {
      ...defaults,
      Users: ["server-a:7"],
      Sources: [
        {
          ServerId: "server-a",
          LibraryId: "2",
          Kind: "hub",
          Key: "/hubs/recent",
          Title: "Recent",
          Type: "show",
          Enabled: false,
          Limit: 250,
          ExcludedShows: ["42"],
        },
      ],
    };
    expect(parsePrefetchSettings(JSON.stringify(configured))).toEqual(configured);
    expect(validatePrefetchSettings(configured)).toBeNull();
  });
  it("rejects malformed shape, unknown keys and out-of-range settings", () => {
    expect(() => parsePrefetchSettings('{"Unknown":true}')).toThrow();
    expect(() => parsePrefetchSettings("[]")).toThrow();
    expect(() => parsePrefetchSettings('{"Users":null}')).toThrow();
    expect(
      validatePrefetchSettings({ ...parsePrefetchSettings(undefined), MaxConcurrentJobs: 5 }),
    ).not.toBeNull();
    expect(
      validatePrefetchSettings({ ...parsePrefetchSettings(undefined), MinimumHeadMb: -1 }),
    ).not.toBeNull();
    expect(isSmartPrefetchSettingsValid({ "smart-prefetch.settings": "bad json" })).toBe(false);
  });
  it("detects semantic changes only within its setting", () => {
    expect(hasSmartPrefetchSettingsChanged({}, { other: "changed" })).toBe(false);
    expect(
      hasSmartPrefetchSettingsChanged(
        {},
        { "smart-prefetch.settings": JSON.stringify(parsePrefetchSettings(undefined)) },
      ),
    ).toBe(false);
    expect(
      hasSmartPrefetchSettingsChanged({}, { "smart-prefetch.settings": '{"Enabled":true}' }),
    ).toBe(true);
  });
});
