import { describe, expect, it } from "vitest";
import type { PlexSource } from "../plex/plex-api";
import { parsePrefetchSettings } from "./smart-prefetch-model";
import type { PlexCatalogueLibrary } from "./plex-source-catalogue";
import {
  setLibraryEnabled,
  setMediaSectionEnabled,
  setSourceEnabled,
} from "./plex-source-selection";

function source(overrides: Partial<PlexSource> = {}): PlexSource {
  return {
    serverId: "server",
    libraryId: "2",
    kind: "hub",
    id: "home.onDeck",
    key: "/hubs/on-deck",
    title: "On Deck",
    type: "show",
    ...overrides,
  };
}

function library(
  type: "movie" | "show",
  overrides: Partial<PlexCatalogueLibrary> = {},
): PlexCatalogueLibrary {
  return {
    identity: { serverId: "server", libraryId: type === "movie" ? "1" : "2", type },
    title: type === "movie" ? "Movies" : "TV",
    hubs: [],
    collections: [],
    unavailable: [],
    configured: false,
    enabled: false,
    ...overrides,
  };
}

describe("Plex source selection", () => {
  it("toggles media sections without changing child choices", () => {
    const settings = {
      ...parsePrefetchSettings(undefined),
      Sources: [
        {
          ServerId: "server",
          LibraryId: "1",
          Kind: "hub",
          Key: "/hubs/recent",
          Title: "Recent",
          Type: "movie",
          Enabled: true,
          Limit: 25,
          ExcludedShows: ["keep"],
        },
      ],
    };

    const disabled = setMediaSectionEnabled(settings, "movie", false);
    const restored = setMediaSectionEnabled(disabled, "movie", true);

    expect(disabled.MovieEnabled).toBe(false);
    expect(restored.MovieEnabled).toBe(true);
    expect(restored.Sources).toEqual(settings.Sources);
  });

  it("persists library off independently and restores unchanged source choices", () => {
    const tv = library("show", { configured: true, enabled: true });
    const settings = {
      ...parsePrefetchSettings(undefined),
      Sources: [
        {
          ServerId: "server",
          LibraryId: "2",
          Kind: "hub",
          Key: "/hubs/on-deck",
          Title: "On Deck",
          Type: "show",
          Enabled: false,
          Limit: 42,
          ExcludedShows: ["7"],
        },
      ],
    };

    const disabled = setLibraryEnabled(settings, tv, false);
    const restored = setLibraryEnabled(disabled.settings, { ...tv, enabled: false }, true);

    expect(disabled.settings.DisabledLibraries).toEqual([
      { ServerId: "server", LibraryId: "2", Type: "show" },
    ]);
    expect(disabled.settings.Sources).toEqual(settings.Sources);
    expect(restored.settings.DisabledLibraries).toEqual([]);
    expect(restored.settings.Sources).toEqual(settings.Sources);
  });

  it("enables only Recently Added for a new movie library", () => {
    const movies = library("movie", {
      hubs: [
        source({
          libraryId: "1",
          id: "home.recentlyAdded",
          key: "/hubs/recent",
          title: "Recently Added",
          type: "movie",
        }),
        source({
          libraryId: "1",
          id: "popular",
          key: "/hubs/popular",
          title: "Popular",
          type: "movie",
        }),
      ],
    });

    const result = setLibraryEnabled(parsePrefetchSettings(undefined), movies, true);

    expect(result.error).toBeNull();
    expect(result.needsManualChoice).toBe(false);
    expect(result.settings.Sources.map((item) => item.Key)).toEqual(["/hubs/recent"]);
  });

  it("enables On Deck and Continue Watching but no collections for a new TV library", () => {
    const tv = library("show", {
      hubs: [
        source(),
        source({ id: "home.continueWatching", key: "/hubs/continue", title: "Continue Watching" }),
        source({ id: "popular", key: "/hubs/popular", title: "Popular" }),
      ],
      collections: [
        source({
          kind: "collection",
          id: "anime",
          key: "/library/collections/anime/children",
          title: "Anime",
          type: "collection",
        }),
      ],
    });

    const result = setLibraryEnabled(parsePrefetchSettings(undefined), tv, true);

    expect(result.settings.Sources.map((item) => item.Key)).toEqual([
      "/hubs/on-deck",
      "/hubs/continue",
    ]);
    expect(result.settings.Sources.every((item) => item.Kind === "hub")).toBe(true);
  });

  it("opens a library for manual choice when no recommendation exists", () => {
    const movies = library("movie", {
      hubs: [
        source({
          libraryId: "1",
          id: "popular",
          key: "/hubs/popular",
          title: "Popular",
          type: "movie",
        }),
      ],
    });

    const result = setLibraryEnabled(parsePrefetchSettings(undefined), movies, true);

    expect(result.needsManualChoice).toBe(true);
    expect(result.settings.Sources).toEqual([]);
  });

  it("retains source customization across off/on and clears the library gate", () => {
    const tv = library("show", { configured: true });
    const configured = {
      ...parsePrefetchSettings(undefined),
      DisabledLibraries: [{ ServerId: "server", LibraryId: "2", Type: "show" as const }],
      Sources: [
        {
          ServerId: "server",
          LibraryId: "2",
          Kind: "hub",
          Key: "/hubs/on-deck",
          Title: "On Deck",
          Type: "show",
          Enabled: true,
          Limit: 77,
          ExcludedShows: ["42"],
        },
      ],
    };

    const off = setSourceEnabled(configured, tv, source(), false);
    const on = setSourceEnabled(off.settings, tv, source(), true);

    expect(off.settings.Sources[0]).toMatchObject({
      Enabled: false,
      Limit: 77,
      ExcludedShows: ["42"],
    });
    expect(on.settings.Sources[0]).toMatchObject({
      Enabled: true,
      Limit: 77,
      ExcludedShows: ["42"],
    });
    expect(on.settings.DisabledLibraries).toEqual([]);
  });

  it("normalizes a newly selected collection to its owning media type", () => {
    const movies = library("movie");
    const collection = source({
      libraryId: "1",
      kind: "collection",
      key: "/library/collections/7/children",
      title: "Classics",
      type: "collection",
    });

    const result = setSourceEnabled(parsePrefetchSettings(undefined), movies, collection, true);

    expect(result.settings.Sources[0]).toMatchObject({ Kind: "collection", Type: "movie" });
  });

  it("returns an error instead of exceeding the source bound", () => {
    const settings = {
      ...parsePrefetchSettings(undefined),
      Sources: Array.from({ length: 128 }, (_, index) => ({
        ServerId: "server",
        LibraryId: "1",
        Kind: "hub",
        Key: `/hubs/${index}`,
        Title: String(index),
        Type: "movie",
        Enabled: true,
        Limit: 10,
        ExcludedShows: [],
      })),
    };

    const result = setSourceEnabled(
      settings,
      library("movie"),
      source({ libraryId: "1", key: "/hubs/new" }),
      true,
    );

    expect(result.error).toContain("128");
    expect(result.settings).toBe(settings);
  });
});
