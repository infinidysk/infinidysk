import { describe, expect, it } from "vitest";
import { displayNameForRead, isProbablyObfuscated } from "./display-name";

describe("isProbablyObfuscated", () => {
  it("flags classic obfuscated names", () => {
    expect(isProbablyObfuscated("b082fa0beaa644d3aa01045d5b8d0b36.mkv")).toBe(true);
    expect(isProbablyObfuscated("abc.xyz.a4c567edbcbf27.mkv")).toBe(true);
    expect(isProbablyObfuscated("9f2c7a1e4b.mkv")).toBe(true);
  });

  it("passes clear release names through", () => {
    expect(isProbablyObfuscated("The.Prestige.2006.1080p.BluRay.x264-GRP.mkv")).toBe(false);
    expect(isProbablyObfuscated("Severance.S02E01.2160p.ATVP.WEB-DL.DDP5.1.H.265-GRP.mkv")).toBe(
      false,
    );
    expect(isProbablyObfuscated("Some Movie (1999) 720p.mkv")).toBe(false);
  });
});

describe("displayNameForRead", () => {
  it("keeps clear leaf names", () => {
    const result = displayNameForRead(
      "The.Prestige.2006.1080p.BluRay.x264-GRP.mkv",
      "/completed-symlinks/movies/The.Prestige.2006.1080p.BluRay.x264-GRP.mkv",
    );
    expect(result).toEqual({
      name: "The.Prestige.2006.1080p.BluRay.x264-GRP.mkv",
      isReleaseFallback: false,
    });
  });

  it("falls back to the release folder for obfuscated leaves", () => {
    const result = displayNameForRead(
      "9f2c7a1e4b.mkv",
      "/completed-symlinks/movies/Interstellar.2014.1080p.BluRay.x264-GRP/9f2c7a1e4b.mkv",
    );
    expect(result).toEqual({
      name: "Interstellar.2014.1080p.BluRay.x264-GRP.mkv",
      isReleaseFallback: true,
    });
  });

  it("keeps the obfuscated leaf when the path has no useful parent", () => {
    const result = displayNameForRead("9f2c7a1e4b.mkv", "/.ids/9f2c7a1e-4b2c");
    expect(result).toEqual({ name: "9f2c7a1e4b.mkv", isReleaseFallback: false });
  });
});
