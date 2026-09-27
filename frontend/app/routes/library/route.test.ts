import { describe, expect, it, vi, beforeEach } from "vitest";
import { loader } from "./route";
import { backendClient } from "~/clients/backend-client.server";
import { installFrontendRuntimeConfig } from "../../../server/runtime-config";

vi.mock("~/clients/backend-client.server", async (importOriginal) => {
  const actual = await importOriginal<typeof import("~/clients/backend-client.server")>();
  return {
    ...actual,
    backendClient: {
      ...actual.backendClient,
      getConfig: vi.fn(),
      getLibraryBrowse: vi.fn(),
      getNativeCacheStatus: vi.fn(),
    },
  };
});

beforeEach(() => {
  configMock().mockReset();
  configMock().mockResolvedValue([]);
  installFrontendRuntimeConfig({ frontendBackendApiKey: "test-api-key" });
  browseMock().mockReset();
  browseMock().mockResolvedValue({
    groups: [],
    totalGroups: 0,
    page: 1,
    pageSize: 12,
    totalItems: 0,
    healthyItems: 0,
    attentionItems: 0,
    unmatchedItems: 0,
    plexStatus: { ready: false, syncedAt: null, entryCount: 0, warning: null, syncing: false },
    expandedGroup: null,
  });
  nativeCacheMock().mockReset();
  nativeCacheMock().mockResolvedValue({ activeMode: "native" });
});

function browseMock() {
  // eslint-disable-next-line @typescript-eslint/unbound-method
  return vi.mocked(backendClient.getLibraryBrowse);
}

function configMock() {
  // eslint-disable-next-line @typescript-eslint/unbound-method
  return vi.mocked(backendClient.getConfig);
}

function nativeCacheMock() {
  // eslint-disable-next-line @typescript-eslint/unbound-method
  return vi.mocked(backendClient.getNativeCacheStatus);
}

function requestFor(path: string): Request {
  return new Request(`http://localhost${path}`);
}

describe("library browse loader", () => {
  it("loads the configured library root for full mapping paths", async () => {
    configMock().mockResolvedValue([
      { configName: "media.library-dir", configValue: "/mnt/plex/" },
    ]);
    const result = await loader({ request: requestFor("/library"), params: {} } as never);
    if (result instanceof Response) throw new Error("Expected library data.");
    expect(result.libraryRoot).toBe("/mnt/plex/");
    expect(configMock()).toHaveBeenCalledWith(["media.library-enabled", "media.library-dir"]);
  });

  it("redirects to settings when Media Library is disabled", async () => {
    configMock().mockResolvedValue([
      {
        configName: "media.library-enabled",
        configValue: "false",
      },
    ]);
    const result = await loader({ request: requestFor("/library"), params: {} } as never);
    expect(result).toBeInstanceOf(Response);
    expect((result as Response).headers.get("Location")).toBe("/settings?tab=library");
    expect(browseMock()).not.toHaveBeenCalled();
  });
  it("passes category, search, mapping filter, and group paging", async () => {
    await loader({
      request: requestFor(
        "/library?view=groups&category=movies&q=dune&type=broken&quality=4k&cache=any&page=2&group=movies%2FDune&groupPage=3",
      ),
      params: {},
    } as never);

    expect(browseMock()).toHaveBeenCalledWith({
      q: "dune",
      category: "movies",
      view: "groups",
      match: "all",
      type: "broken",
      quality: "4k",
      cache: "any",
      page: 2,
      group: "movies/Dune",
      groupPage: 3,
    });
  });

  it("clamps invalid category and page inputs to safe defaults", async () => {
    await loader({
      request: requestFor("/library?category=invalid&type=invalid&page=-1&groupPage=0"),
      params: {},
    } as never);

    expect(browseMock()).toHaveBeenCalledWith({
      category: "all",
      view: "files",
      match: "all",
      type: "all",
      quality: "all",
      cache: "all",
      page: 1,
      groupPage: 1,
    });
  });

  it("does not turn an empty season field into Season 0 when searching or clearing search", async () => {
    const searched = await loader({
      request: requestFor("/library?view=files&category=all&q=Aladdin&season="),
      params: {},
    } as never);
    if (searched instanceof Response) throw new Error("Expected library data.");
    expect(searched.query.season).toBeNull();
    expect(browseMock()).toHaveBeenLastCalledWith(expect.not.objectContaining({ season: 0 }));

    const cleared = await loader({
      request: requestFor("/library?view=files&category=all&q=&season="),
      params: {},
    } as never);
    if (cleared instanceof Response) throw new Error("Expected library data.");
    expect(cleared.query.q).toBe("");
    expect(cleared.query.season).toBeNull();
    expect(browseMock()).toHaveBeenLastCalledWith(
      expect.not.objectContaining({ season: 0, q: "Aladdin" }),
    );
  });

  it("signs preview URLs for files in the expanded group", async () => {
    browseMock().mockResolvedValue({
      groups: [
        {
          key: "movies/Film",
          title: "Film",
          category: "movies",
          itemCount: 1,
          healthyCount: 1,
          attentionCount: 0,
          quality: "1080p",
          cachePercentage: 75,
        },
      ],
      totalGroups: 1,
      page: 1,
      pageSize: 12,
      totalItems: 1,
      healthyItems: 1,
      attentionItems: 0,
      unmatchedItems: 0,
      plexStatus: {
        ready: true,
        syncedAt: "2026-09-23T00:00:00Z",
        entryCount: 1,
        warning: null,
        syncing: false,
      },
      expandedGroup: {
        key: "movies/Film",
        page: 1,
        pageSize: 50,
        totalItems: 1,
        items: [
          {
            item: {
              kind: "internal",
              davItemId: "11111111-1111-1111-1111-111111111111",
              displayName: "film.mkv",
              contentPath: "/content/film.mkv",
              size: 100,
              mappingCount: 1,
              health: "healthy",
              mappings: [],
            },
            season: null,
            episode: null,
            quality: "1080p",
            cachePercentage: 75,
          },
        ],
      },
    });

    const data = await loader({
      request: requestFor("/library?group=movies%2FFilm"),
      params: {},
    } as never);

    if (data instanceof Response) throw new Error("Expected Media Library browse data.");

    expect(data.previewUrls["11111111-1111-1111-1111-111111111111"]).toMatch(
      /^\/view\/content\/film\.mkv\?downloadKey=.+/,
    );
    expect(data.nativeCacheActive).toBe(true);
  });
});
