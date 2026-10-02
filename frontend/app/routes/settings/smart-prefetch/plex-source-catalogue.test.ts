import { describe, expect, it } from "vitest";
import type { PlexLibrary, PlexSource } from "../plex/plex-api";
import type { DisabledPlexLibrary, PrefetchSource } from "./smart-prefetch-model";
import {
  buildPlexSourceCatalogue,
  catalogueSourceKey,
  persistedSourceKey,
  recommendationFor,
} from "./plex-source-catalogue";

const libraries: PlexLibrary[] = [
  { id: "1", title: "Movies", type: "movie" },
  { id: "3", title: "Cinema", type: "movie" },
  { id: "2", title: "TV", type: "show" },
  { id: "4", title: "Music", type: "artist" },
];

function source(overrides: Partial<PlexSource>): PlexSource {
  return {
    serverId: "server",
    libraryId: "1",
    kind: "hub",
    id: "home.recentlyAdded",
    key: "/hubs/recently-added",
    title: "Recently Added",
    type: "movie",
    ...overrides,
  };
}

function saved(overrides: Partial<PrefetchSource>): PrefetchSource {
  return {
    ServerId: "server",
    LibraryId: "1",
    Kind: "hub",
    Key: "/hubs/recently-added",
    Title: "Recently Added",
    Type: "movie",
    Enabled: true,
    Limit: 10,
    ExcludedShows: [],
    ...overrides,
  };
}

describe("Plex source catalogue", () => {
  it("groups supported libraries in Plex order and deduplicates catalogue records", () => {
    const duplicate = source({});
    const catalogue = buildPlexSourceCatalogue({
      serverId: "server",
      libraries,
      sources: [
        source({ id: "zebra", key: "/hubs/zebra", title: "Zebra" }),
        duplicate,
        duplicate,
        source({ id: "alpha", key: "/hubs/alpha", title: "Alpha" }),
        source({
          libraryId: "1",
          kind: "collection",
          id: "collection-2",
          key: "/library/collections/2/children",
          title: "Zulu Collection",
          type: "collection",
        }),
        source({
          libraryId: "1",
          kind: "collection",
          id: "collection-1",
          key: "/library/collections/1/children",
          title: "Anime Collection",
          type: "collection",
        }),
        source({ libraryId: "4", id: "music", key: "/hubs/music", type: "artist" }),
        source({ libraryId: null, id: "clips", key: "/hubs/clips", type: "clip" }),
      ],
      savedSources: [],
      disabledLibraries: [],
    });

    expect(catalogue.movie.map((library) => library.title)).toEqual(["Movies", "Cinema"]);
    expect(catalogue.show.map((library) => library.title)).toEqual(["TV"]);
    expect(catalogue.movie[0]?.hubs.map((hub) => hub.title)).toEqual([
      "Recently Added",
      "Alpha",
      "Zebra",
    ]);
    expect(catalogue.movie[0]?.collections.map((collection) => collection.title)).toEqual([
      "Anime Collection",
      "Zulu Collection",
    ]);
    expect(catalogue.movie.flatMap((library) => library.hubs)).toHaveLength(3);
  });

  it("keeps global movie and TV groups distinct and preserves unavailable saved sources", () => {
    const missing = saved({
      LibraryId: "2",
      Key: "/hubs/missing",
      Title: "Missing On Deck",
      Type: "show",
      Enabled: false,
      Limit: 25,
    });
    const catalogue = buildPlexSourceCatalogue({
      serverId: "server",
      libraries,
      sources: [
        source({ libraryId: null, id: "global.movie", key: "/hubs/global-movie" }),
        source({
          libraryId: null,
          id: "global.tv",
          key: "/hubs/global-tv",
          type: "show",
        }),
      ],
      savedSources: [missing],
      disabledLibraries: [],
    });

    expect(catalogue.movie.at(-1)?.identity).toEqual({
      serverId: "server",
      libraryId: "",
      type: "movie",
    });
    expect(catalogue.show.at(-1)?.identity).toEqual({
      serverId: "server",
      libraryId: "",
      type: "show",
    });
    expect(
      catalogue.show.find((library) => library.identity.libraryId === "2")?.unavailable,
    ).toEqual([missing]);
  });

  it("derives configured and disabled library state without rewriting sources", () => {
    const configured = saved({ LibraryId: "2", Type: "show", Key: "/hubs/on-deck" });
    const disabled: DisabledPlexLibrary[] = [{ ServerId: "server", LibraryId: "2", Type: "show" }];
    const catalogue = buildPlexSourceCatalogue({
      serverId: "server",
      libraries,
      sources: [source({ libraryId: "2", type: "show", key: "/hubs/on-deck" })],
      savedSources: [configured],
      disabledLibraries: disabled,
    });

    expect(catalogue.movie[0]?.configured).toBe(false);
    expect(catalogue.movie[0]?.enabled).toBe(false);
    expect(catalogue.show[0]?.configured).toBe(true);
    expect(catalogue.show[0]?.enabled).toBe(false);
  });

  it("uses catalogue identity for rendering and backend identity for persistence", () => {
    const entry = source({ libraryId: "2", key: "/hubs/shared" });
    expect(catalogueSourceKey(entry)).toBe("server\n2\nhub\n/hubs/shared");
    expect(persistedSourceKey(entry.serverId, entry.kind, entry.key)).toBe(
      "server\nhub\n/hubs/shared",
    );
  });

  it("recognizes smart-default hubs from stable identity before title fallback", () => {
    expect(recommendationFor(source({ id: "home.recentlyAdded", title: "Neu" }))).toBe(
      "recently-added",
    );
    expect(recommendationFor(source({ id: "home.onDeck", title: "Weiter" }))).toBe("on-deck");
    expect(recommendationFor(source({ id: "home.continueWatching", title: "Suite" }))).toBe(
      "continue-watching",
    );
    expect(
      recommendationFor(source({ id: "other", key: "/hubs/other", title: "Recently Added" })),
    ).toBe("recently-added");
  });
});
