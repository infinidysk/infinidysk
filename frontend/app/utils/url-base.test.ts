import { describe, expect, it } from "vitest";
import { normalizeUrlBase, withUrlBase } from "./url-base";

describe("normalizeUrlBase", () => {
  it("treats unset, empty, and bare-slash values as root", () => {
    expect(normalizeUrlBase(undefined)).toBe("");
    expect(normalizeUrlBase("")).toBe("");
    expect(normalizeUrlBase("/")).toBe("");
    expect(normalizeUrlBase("  ")).toBe("");
  });

  it("forces a single leading slash and strips trailing slashes", () => {
    expect(normalizeUrlBase("nzbdav")).toBe("/nzbdav");
    expect(normalizeUrlBase("/nzbdav")).toBe("/nzbdav");
    expect(normalizeUrlBase("/nzbdav/")).toBe("/nzbdav");
    expect(normalizeUrlBase("/nzbdav///")).toBe("/nzbdav");
    expect(normalizeUrlBase(" /nzbdav ")).toBe("/nzbdav");
  });

  it("preserves multi-segment prefixes", () => {
    expect(normalizeUrlBase("/tools/nzbdav/")).toBe("/tools/nzbdav");
  });
});

describe("withUrlBase", () => {
  // __URL_BASE__ is not defined under vitest, so URL_BASE resolves to "" here;
  // these assert the path-joining contract rather than a specific prefix.
  it("always emits a leading slash", () => {
    expect(withUrlBase("/api/foo")).toBe("/api/foo");
    expect(withUrlBase("api/foo")).toBe("/api/foo");
  });

  it("leaves query strings intact", () => {
    expect(withUrlBase("/api?mode=queue")).toBe("/api?mode=queue");
  });
});
