import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { normalizeUrlBase, urlBaseFromEnv, withUrlBase } from "./url-base";

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

  it("rejects characters Express 5 would misparse as route patterns", () => {
    // ":" and "*" mount successfully in path-to-regexp v8 but silently turn
    // the prefix into a pattern; "(" and "{" crash at boot with a raw stack.
    for (const bad of [
      "/nzb:dav",
      "/nzb*dav",
      "/nzb(dav",
      "/nzb{dav",
      "/nzb dav",
      "/nzb%64av",
      "/nzb?x=1",
    ]) {
      expect(() => normalizeUrlBase(bad), bad).toThrow(/Invalid URL base/);
    }
  });

  it("allows the full unreserved path charset", () => {
    expect(normalizeUrlBase("/My.App_v2~x-1")).toBe("/My.App_v2~x-1");
  });
});

describe("urlBaseFromEnv", () => {
  it("prefers NZBDAV_URL_BASE over bare URL_BASE", () => {
    expect(urlBaseFromEnv({ NZBDAV_URL_BASE: "/a", URL_BASE: "/b" })).toBe("/a");
  });

  it("falls back to bare URL_BASE", () => {
    expect(urlBaseFromEnv({ URL_BASE: "/b" })).toBe("/b");
  });

  it("returns root when neither is set", () => {
    expect(urlBaseFromEnv({})).toBe("");
  });
});

describe("withUrlBase", () => {
  // __URL_BASE__ is not defined under vitest, so URL_BASE resolves from the
  // (unset) test environment; these assert the path-joining contract.
  it("always emits a leading slash", () => {
    expect(withUrlBase("/api/foo")).toBe("/api/foo");
    expect(withUrlBase("api/foo")).toBe("/api/foo");
  });

  it("leaves query strings intact", () => {
    expect(withUrlBase("/api?mode=queue")).toBe("/api?mode=queue");
  });
});

describe("server.ts mirror parity", () => {
  // server.ts is compiled by tsc into dist-node without the app graph, so it
  // carries a hand-synced copy of the normalizer. Assert the copies cannot
  // drift: the same charset guard and the same normalization steps must appear
  // verbatim in both files.
  const here = readFileSync(new URL("./url-base.ts", import.meta.url), "utf8");
  const server = readFileSync(new URL("../../server.ts", import.meta.url), "utf8");

  it.each([
    ["const SAFE_URL_BASE = /^[A-Za-z0-9._~\\-/]+$/;"],
    ['if (trimmed === "" || trimmed === "/") return "";'],
    ['const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;'],
    ['return withLeading.replace(/\\/+$/, "");'],
  ])("both copies contain %s", (line) => {
    expect(here).toContain(line);
    expect(server).toContain(line);
  });

  it("server.ts reads NZBDAV_URL_BASE with URL_BASE fallback", () => {
    expect(server).toContain('process.env["NZBDAV_URL_BASE"] ?? process.env["URL_BASE"]');
  });
});
