import { describe, expect, it } from "vitest";
import { mediaTypeFromFileName } from "./media-type";

describe("mediaTypeFromFileName", () => {
  it("classifies season-episode tokens as episodes", () => {
    expect(mediaTypeFromFileName("The.Last.of.Us.S01E03.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv")).toBe(
      "episode",
    );
    expect(mediaTypeFromFileName("Severance.S02E01.2160p.ATVP.WEB-DL.DDP5.1.H.265-GRP.mkv")).toBe(
      "episode",
    );
    expect(mediaTypeFromFileName("Some Show s2e11 720p.mkv")).toBe("episode");
    expect(mediaTypeFromFileName("Some.Show.1x02.1080p.mkv")).toBe("episode");
  });

  it("classifies year-plus-quality names as movies", () => {
    expect(mediaTypeFromFileName("The.Prestige.2006.1080p.BluRay.x264-GRP.mkv")).toBe("movie");
    expect(
      mediaTypeFromFileName("Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.H.265-GRP.mkv"),
    ).toBe("movie");
    expect(mediaTypeFromFileName("Some Movie (1999) 720p.mkv")).toBe("movie");
  });

  it("does not mistake codec tokens for episode markers", () => {
    expect(mediaTypeFromFileName("Movie.Title.2010.BluRay.x264-GRP.mkv")).toBe("movie");
  });

  it("returns null for names that do not look like media", () => {
    expect(mediaTypeFromFileName("backup-2024-01-01.zip")).toBeNull();
    expect(mediaTypeFromFileName("README.txt")).toBeNull();
    expect(mediaTypeFromFileName("2006.mkv")).toBeNull();
  });
});
