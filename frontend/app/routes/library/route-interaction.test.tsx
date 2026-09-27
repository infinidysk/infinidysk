// @vitest-environment jsdom
import { afterEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import Library, { type LibraryPageData } from "./route";

const item = {
  kind: "internal" as const,
  davItemId: "11111111-1111-1111-1111-111111111111",
  displayName: "Film.1080p.mkv",
  contentPath: "/content/Film.1080p.mkv",
  size: 1024,
  mappingCount: 1,
  health: "healthy",
  mappings: [],
};

const page: LibraryPageData = {
  query: {
    q: "",
    category: "movies",
    view: "groups",
    match: "all",
    season: null,
    type: "all",
    quality: "all",
    cache: "all",
    page: 1,
    group: null,
    groupPage: 1,
  },
  browse: {
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
    expandedGroup: null,
    plexStatus: {
      ready: true,
      syncedAt: "2026-09-23T00:00:00Z",
      entryCount: 1,
      warning: null,
      syncing: false,
    },
  },
  previewUrls: {},
  nativeCacheActive: true,
};

describe("Library accordion", () => {
  afterEach(cleanup);

  it("shows the file table with a visible movie switch and title column", async () => {
    const tablePage: LibraryPageData = {
      ...page,
      query: { ...page.query, view: "files", category: "all" },
      browse: {
        ...page.browse,
        groups: [],
        totalGroups: 0,
        pageSize: 50,
        totalFiles: 1,
        files: [
          {
            item,
            title: "Film",
            season: null,
            episode: null,
            category: "movies",
            quality: "1080p",
            cachePercentage: 75,
          },
        ],
      },
    };
    const router = createMemoryRouter(
      [
        {
          path: "/library",
          loader: () => tablePage,
          element: <Library {...({ loaderData: tablePage } as Parameters<typeof Library>[0])} />,
        },
      ],
      { initialEntries: ["/library?view=files&category=all"] },
    );
    render(<RouterProvider router={router} />);
    expect(await screen.findByRole("columnheader", { name: "Show / movie title" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Movies" })).toBeTruthy();
    expect(screen.getByRole("cell", { name: "Film" })).toBeTruthy();
    expect(screen.getAllByRole("cell", { name: "—" })).toHaveLength(2);
  });

  it("loads files inline without changing the location", async () => {
    const router = createMemoryRouter(
      [
        {
          path: "/library",
          loader: () => ({
            ...page,
            browse: {
              ...page.browse,
              expandedGroup: {
                key: "movies/Film",
                page: 1,
                pageSize: 50,
                totalItems: 1,
                items: [
                  { item, season: null, episode: null, quality: "1080p", cachePercentage: 75 },
                ],
              },
            },
          }),
          element: <Library {...({ loaderData: page } as Parameters<typeof Library>[0])} />,
        },
      ],
      { initialEntries: ["/library?category=movies"] },
    );

    render(<RouterProvider router={router} />);
    fireEvent.click(await screen.findByRole("button", { name: /Film/i }));
    expect(await screen.findByText("Film.1080p.mkv")).toBeTruthy();
    expect(router.state.location.search).toBe("?category=movies");
    expect(screen.getAllByText("Cache 75%").length).toBeGreaterThanOrEqual(1);
  });
});
