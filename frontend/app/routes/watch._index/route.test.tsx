// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Route } from "./+types/route";
import WatchRoute, { loader } from "./route";
import type { CatalogRow, CatalogSearchPage } from "~/clients/catalog-contract";

const mocks = vi.hoisted(() => ({
  getCatalogHome: vi.fn(),
  searchCatalog: vi.fn(),
  fetchCatalogSearch: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: { getCatalogHome: mocks.getCatalogHome, searchCatalog: mocks.searchCatalog },
}));
vi.mock("~/clients/catalog-client", () => ({
  fetchCatalogSearch: mocks.fetchCatalogSearch,
  createCatalogPlayback: vi.fn(),
}));

const trendingRow: CatalogRow = {
  id: "trending",
  title: "Trending Now",
  items: [
    {
      type: "movie",
      tmdbId: 10331,
      title: "Night of the Living Dead",
      year: 1968,
      overview: "Dead walk.",
      rating: 7.6,
      posterUrl: "https://image.tmdb.org/t/p/w500/poster.jpg",
      backdropUrl: "https://image.tmdb.org/t/p/w1280/back.jpg",
    },
    {
      type: "series",
      tmdbId: 1930,
      title: "The Beverly Hillbillies",
      year: 1962,
      overview: null,
      rating: null,
      posterUrl: null,
      backdropUrl: null,
    },
  ],
};

const popularRow: CatalogRow = {
  id: "popular-movies",
  title: "Popular Movies",
  items: [trendingRow.items[0]!],
};

function loaderArgs(path: string): Route.LoaderArgs {
  return { request: new Request(`http://localhost${path}`) } as unknown as Route.LoaderArgs;
}

function renderWatch(data: Awaited<ReturnType<typeof loader>>) {
  const router = createMemoryRouter(
    [{ path: "/watch", element: <WatchRoute />, loader: () => data }],
    { initialEntries: ["/watch"] },
  );
  return render(<RouterProvider router={router} />);
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.getCatalogHome.mockResolvedValue({ status: true, rows: [trendingRow, popularRow] });
  mocks.searchCatalog.mockResolvedValue({
    status: true,
    page: 1,
    totalPages: 1,
    hasMore: false,
    results: [trendingRow.items[0]!],
  } satisfies CatalogSearchPage);
});

afterEach(cleanup);

describe("watch catalog loader", () => {
  it("loads the four home rows when no query is set", async () => {
    const data = await loader(loaderArgs("/watch"));

    expect(data.mode).toBe("home");
    expect(data.rows.map((row) => row.id)).toEqual(["trending", "popular-movies"]);
    expect(mocks.searchCatalog).not.toHaveBeenCalled();
  });

  it("searches page one for a query", async () => {
    const data = await loader(loaderArgs("/watch?q=night"));

    expect(data.mode).toBe("search");
    expect(data.page?.results).toHaveLength(1);
    expect(mocks.searchCatalog).toHaveBeenCalledWith("night", 1);
  });

  it("reports provider failures inline instead of throwing", async () => {
    mocks.getCatalogHome.mockRejectedValue(new Error("The TMDB service is unavailable."));

    const data = await loader(loaderArgs("/watch"));

    expect(data.rows).toHaveLength(0);
    expect(data.error).toBe("The TMDB service is unavailable.");
  });

  it("rejects over-long queries without calling the provider", async () => {
    const data = await loader(loaderArgs(`/watch?q=${"a".repeat(101)}`));

    expect(data.error).toBe("Search queries are limited to 100 characters.");
    expect(mocks.searchCatalog).not.toHaveBeenCalled();
  });
});

describe("watch catalog view", () => {
  it("renders the hero, poster artwork, and keyboard-scrollable rows", async () => {
    const { container } = renderWatch({
      mode: "home",
      query: "",
      rows: [trendingRow, popularRow],
      page: null,
      error: null,
    });

    const details = await screen.findByRole("link", { name: /view details/i });
    expect(details.getAttribute("href")).toBe("/watch/title/movie/10331");
    const posters = [...container.querySelectorAll("img")].map((img) => img.getAttribute("src"));
    expect(posters).toContain(trendingRow.items[0]!.posterUrl);
    expect(screen.getByRole("button", { name: /scroll trending now left/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /scroll trending now right/i })).toBeTruthy();
    // Missing artwork falls back to a titled placeholder, not a broken image.
    expect(screen.getAllByText("The Beverly Hillbillies").length).toBeGreaterThan(0);
  });

  it("shows an empty-search message", async () => {
    renderWatch({
      mode: "search",
      query: "nothing",
      rows: [],
      page: { status: true, page: 1, totalPages: 1, hasMore: false, results: [] },
      error: null,
    });

    expect(await screen.findByText("No titles found")).toBeTruthy();
  });

  it("shows a retry control for provider failures", async () => {
    renderWatch({
      mode: "home",
      query: "",
      rows: [],
      page: null,
      error: "The TMDB service is unavailable.",
    });

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("The TMDB service is unavailable.");
    expect(screen.getByRole("button", { name: /retry/i })).toBeTruthy();
  });
});
