import { describe, expect, it } from "vitest";
import { cacheMode, parseNativeFolders, validateNativeFolders } from "./native-cache-model";

describe("native cache settings", () => {
  it("uses an explicit exclusive mode before the legacy alias", () => {
    expect(cacheMode({ "cache.mode": "native", "usenet.segment-cache.enabled": "true" })).toBe(
      "native",
    );
    expect(cacheMode({ "usenet.segment-cache.enabled": "true" })).toBe("segment");
    expect(cacheMode({ "usenet.segment-cache.enabled": "false" })).toBe("off");
  });
  it("rejects nested folders and unsafe quotas", () => {
    const first = {
      id: "one",
      name: "Disk",
      path: "/cache",
      maxBytes: 50e12,
      minFreeBytes: 1e9,
      maxAgeDays: 0,
      priority: 0,
      enabled: true,
      readOnly: false,
      storageType: "nas" as const,
    };
    expect(validateNativeFolders([first])).toBeNull();
    expect(validateNativeFolders([first, { ...first, id: "two", path: "/cache/sub" }])).toMatch(
      /overlap/i,
    );
    expect(validateNativeFolders([{ ...first, maxBytes: -1 }])).toMatch(/quota/i);
    expect(validateNativeFolders([{ ...first, highWaterPercent: 60, lowWaterPercent: 60 }])).toMatch(/watermark/i);
    expect(validateNativeFolders([{ ...first, highWaterPercent: 101, lowWaterPercent: 50 }])).toMatch(/watermark/i);
    expect(validateNativeFolders([{ ...first, highWaterPercent: 75, lowWaterPercent: 50 }])).toBeNull();
  });
  it("does not silently replace malformed saved folders with an empty list", () => {
    expect(() => parseNativeFolders("not json")).toThrow();
  });
});
