// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Route } from "./+types/route";
import WatchRoute, { loader } from "./route";
import type { CatalogHistoryItem, CatalogRow, CatalogSearchPage } from "~/clients/catalog-contract";

const mocks = vi.hoisted(() => ({
  getCatalogHome: vi.fn(),
  getCatalogHistory: vi.fn(),
  searchCatalog: vi.fn(),
  fetchCatalogSearch: vi.fn(),
  removeCatalogHistoryItem: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    getCatalogHome: mocks.getCatalogHome,
    getCatalogHistory: mocks.getCatalogHistory,
    searchCatalog: mocks.searchCatalog,
  },
}));
vi.mock("~/clients/catalog-client", () => ({
  fetchCatalogSearch: mocks.fetchCatalogSearch,
  createCatalogPlayback: vi.fn(),
  removeCatalogHistoryItem: mocks.removeCatalogHistoryItem,
}));

const nightOfTheLivingDead = {
  type: "movie" as const,
  tmdbId: 10331,
  title: "Night of the Living Dead",
  year: 1968,
  overview: "Dead walk.",
  rating: 7.6,
  posterUrl: "https://image.tmdb.org/t/p/w500/poster.jpg",
  backdropUrl: "https://image.tmdb.org/t/p/w1280/back.jpg",
};

const hillbillies = {
  type: "series" as const,
  tmdbId: 1930,
  title: "The Beverly Hillbillies",
  year: 1962,
  overview: null,
  rating: null,
  posterUrl: null,
  backdropUrl: null,
};

const trendingRow: CatalogRow = {
  id: "trending",
  title: "Trending Now",
  items: [nightOfTheLivingDead, hillbillies],
  people: null,
};

const popularRow: CatalogRow = {
  id: "popular-movies",
  title: "Popular Movies",
  items: [nightOfTheLivingDead],
  people: null,
};

const peopleRow: CatalogRow = {
  id: "trending-people",
  title: "Trending People",
  items: null,
  people: [
    {
      personId: "525",
      name: "Christopher Nolan",
      profileUrl: "https://image.tmdb.org/t/p/w500/nolan.jpg",
    },
  ],
};

const resumeItem: CatalogHistoryItem = {
  id: 7,
  type: "series",
  tmdbId: 1930,
  season: 1,
  episode: 2,
  title: "The Beverly Hillbillies",
  subtitle: "S1E2 · Getting Settled",
  posterUrl: "https://image.tmdb.org/t/p/w500/poster.jpg",
  positionSeconds: 300,
  durationSeconds: 1500,
  updatedAtEpoch: 1_790_000_000,
  finished: false,
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
  mocks.getCatalogHistory.mockResolvedValue({ status: true, items: [] });
  mocks.removeCatalogHistoryItem.mockResolvedValue(undefined);
  mocks.searchCatalog.mockResolvedValue({
    status: true,
    page: 1,
    totalPages: 1,
    hasMore: false,
    results: [nightOfTheLivingDead],
  } satisfies CatalogSearchPage);
});

afterEach(cleanup);

describe("watch catalog loader", () => {
  it("loads the home rows and watch history when no query is set", async () => {
    mocks.getCatalogHistory.mockResolvedValue({ status: true, items: [resumeItem] });

    const data = await loader(loaderArgs("/watch"));

    expect(data.mode).toBe("home");
    expect(data.rows.map((row) => row.id)).toEqual(["trending", "popular-movies"]);
    expect(data.history).toEqual([resumeItem]);
    expect(mocks.getCatalogHistory).toHaveBeenCalledWith(20);
    expect(mocks.searchCatalog).not.toHaveBeenCalled();
  });

  it("keeps the catalog when only the watch history fails", async () => {
    mocks.getCatalogHistory.mockRejectedValue(new Error("history unavailable"));

    const data = await loader(loaderArgs("/watch"));

    expect(data.rows).toHaveLength(2);
    expect(data.history).toEqual([]);
    expect(data.error).toBeNull();
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
      history: [],
      page: null,
      error: null,
    });

    const details = await screen.findByRole("link", { name: /view details/i });
    expect(details.getAttribute("href")).toBe("/watch/title/movie/10331");
    const posters = [...container.querySelectorAll("img")].map((img) => img.getAttribute("src"));
    expect(posters).toContain(nightOfTheLivingDead.posterUrl);
    expect(screen.getByRole("button", { name: /scroll trending now left/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /scroll trending now right/i })).toBeTruthy();
    // Missing artwork falls back to a titled placeholder, not a broken image.
    expect(screen.getAllByText("The Beverly Hillbillies").length).toBeGreaterThan(0);
  });

  it("renders Continue Watching first with a progress bar and an episode resume link", async () => {
    const { container } = renderWatch({
      mode: "home",
      query: "",
      rows: [trendingRow, popularRow],
      history: [resumeItem],
      page: null,
      error: null,
    });

    const heading = await screen.findByRole("heading", { name: "Continue Watching" });
    const row = heading.closest("section");
    expect(row).not.toBeNull();
    // Continue Watching precedes the first provider row.
    const sections = [...container.querySelectorAll("section")];
    expect(sections.indexOf(row!)).toBeLessThan(
      sections.indexOf(screen.getByRole("heading", { name: "Trending Now" }).closest("section")!),
    );

    const resume = within(row as HTMLElement).getByRole("link", {
      name: /the beverly hillbillies/i,
    });
    expect(resume.getAttribute("href")).toBe("/watch/title/series/1930?season=1&episode=2");
    const progress = within(row as HTMLElement).getByRole("progressbar", { name: /progress/i });
    expect(progress.getAttribute("aria-valuenow")).toBe("20");
  });


  it("removes a title from Continue Watching", async () => {
    renderWatch({
      mode: "home",
      query: "",
      rows: [trendingRow],
      history: [resumeItem],
      page: null,
      error: null,
    });

    const remove = await screen.findByRole("button", {
      name: /remove the beverly hillbillies · s1e2 · getting settled from continue watching/i,
    });
    // The control is hidden until its card is hovered or focused.
    fireEvent.pointerEnter(remove.parentElement!);
    fireEvent.click(remove);

    await waitFor(() => expect(mocks.removeCatalogHistoryItem).toHaveBeenCalledWith(7));
  });

  it("renders one card when a finished row sits beside the live one", async () => {
    const finished: CatalogHistoryItem = {
      ...resumeItem,
      positionSeconds: 1500,
      finished: true,
      updatedAtEpoch: 1_790_000_000,
    };
    const live: CatalogHistoryItem = { ...resumeItem, updatedAtEpoch: 1_790_000_100 };

    renderWatch({
      mode: "home",
      query: "",
      rows: [trendingRow],
      history: [live, finished],
      page: null,
      error: null,
    });

    const heading = await screen.findByRole("heading", { name: "Continue Watching" });
    expect(within(heading.closest("section")!).getAllByRole("link")).toHaveLength(1);
  });

  it("renders trending people as person-page links", async () => {
    renderWatch({
      mode: "home",
      query: "",
      rows: [trendingRow, peopleRow],
      history: [],
      page: null,
      error: null,
    });

    const person = await screen.findByRole("link", { name: "Christopher Nolan" });
    expect(person.getAttribute("href")).toBe("/watch/person/525");
  });

  it("shows an empty-search message", async () => {
    renderWatch({
      mode: "search",
      query: "nothing",
      rows: [],
      history: [],
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
      history: [],
      page: null,
      error: "The TMDB service is unavailable.",
    });

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("The TMDB service is unavailable.");
    expect(screen.getByRole("button", { name: /retry/i })).toBeTruthy();
  });
});
