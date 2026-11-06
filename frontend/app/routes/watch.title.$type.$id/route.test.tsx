// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Route } from "./+types/route";
import WatchTitleRoute, { loader } from "./route";
import type { CatalogTitleResponse } from "~/clients/catalog-contract";

const mocks = vi.hoisted(() => ({
  getCatalogTitle: vi.fn(),
  createCatalogPlayback: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: { getCatalogTitle: mocks.getCatalogTitle },
}));
vi.mock("~/clients/catalog-client", () => ({
  createCatalogPlayback: mocks.createCatalogPlayback,
  fetchCatalogSearch: vi.fn(),
  reportWatchHistoryProgress: vi.fn(),
}));

const movieTitle: CatalogTitleResponse = {
  status: true,
  title: {
    type: "movie",
    tmdbId: 10331,
    title: "Night of the Living Dead",
    year: 1968,
    overview: "Dead walk.",
    rating: 7.6,
    runtimeMinutes: 96,
    genres: ["Horror"],
    posterUrl: "https://image.tmdb.org/t/p/w500/poster.jpg",
    backdropUrl: "https://image.tmdb.org/t/p/w1280/back.jpg",
  },
  seasons: [],
  episodes: [],
  selectedSeason: null,
};

const seriesTitle: CatalogTitleResponse = {
  status: true,
  title: {
    type: "series",
    tmdbId: 1930,
    title: "The Beverly Hillbillies",
    year: 1962,
    overview: null,
    rating: 6.8,
    runtimeMinutes: 25,
    genres: ["Comedy"],
    posterUrl: null,
    backdropUrl: null,
  },
  seasons: [
    { seasonNumber: 0, name: "Specials", episodeCount: 2 },
    { seasonNumber: 1, name: "Season 1", episodeCount: 36 },
  ],
  episodes: [
    {
      seasonNumber: 1,
      episodeNumber: 1,
      name: "The Clampetts Strike Oil",
      overview: "Oil!",
      airDate: "1962-09-26",
      runtimeMinutes: 25,
      stillUrl: "https://image.tmdb.org/t/p/w500/still.jpg",
    },
  ],
  selectedSeason: 1,
};

function loaderArgs(path: string, type = "movie", id = "10331"): Route.LoaderArgs {
  return {
    request: new Request(`http://localhost${path}`),
    params: { type, id },
  } as unknown as Route.LoaderArgs;
}

type TitleLoaderData = Awaited<ReturnType<typeof loader>>;

function renderTitle(data: Pick<TitleLoaderData, "catalogTitle"> & Partial<TitleLoaderData>) {
  const loaderData: TitleLoaderData = { resumeEpisode: null, ...data };
  const router = createMemoryRouter(
    [{ path: "/watch/title/:type/:id", element: <WatchTitleRoute />, loader: () => loaderData }],
    { initialEntries: ["/watch/title/movie/10331"] },
  );
  return render(<RouterProvider router={router} />);
}

beforeEach(() => {
  vi.clearAllMocks();
  // jsdom has no layout, so the resume row's scroll-into-view is a no-op stub.
  Element.prototype.scrollIntoView = vi.fn();
  mocks.getCatalogTitle.mockResolvedValue(movieTitle);
  mocks.createCatalogPlayback.mockResolvedValue({
    status: true,
    playbackId: "11111111-2222-3333-4444-555555555555",
    directUrl: "http://localhost:3000/api/search/token/play/play.mkv",
    hlsUrl:
      "http://localhost:3000/api/catalog/playback/11111111-2222-3333-4444-555555555555/playlist.m3u8",
    releaseTitle: "Night.of.the.Living.Dead.1968.1080p.WEB-DL.x264",
  });

  // jsdom has no media pipeline and no modal dialog implementation.
  Object.defineProperty(HTMLMediaElement.prototype, "play", {
    configurable: true,
    value: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
  });
  Object.defineProperty(HTMLMediaElement.prototype, "load", {
    configurable: true,
    value: vi.fn<() => void>(),
  });
  Object.defineProperty(HTMLDialogElement.prototype, "showModal", {
    configurable: true,
    value(this: HTMLDialogElement) {
      this.setAttribute("open", "");
    },
  });
  Object.defineProperty(HTMLDialogElement.prototype, "close", {
    configurable: true,
    value(this: HTMLDialogElement) {
      this.removeAttribute("open");
    },
  });
});

afterEach(cleanup);

describe("watch title loader", () => {
  it("passes the title type, id, and season through", async () => {
    await loader(loaderArgs("/watch/title/series/1930?season=1", "series", "1930"));

    expect(mocks.getCatalogTitle).toHaveBeenCalledWith("series", 1930, 1);
  });

  it("carries the resume episode from the deep link", async () => {
    const data = await loader(
      loaderArgs("/watch/title/series/1930?season=1&episode=2", "series", "1930"),
    );

    expect(data.resumeEpisode).toBe(2);
    expect(mocks.getCatalogTitle).toHaveBeenCalledWith("series", 1930, 1);
  });

  it("rejects an episode without a season and an out-of-range episode", async () => {
    await expect(
      loader(loaderArgs("/watch/title/series/1930?episode=2", "series", "1930")),
    ).rejects.toMatchObject({ init: { status: 404 } });
    await expect(
      loader(loaderArgs("/watch/title/series/1930?season=1&episode=0", "series", "1930")),
    ).rejects.toMatchObject({ init: { status: 404 } });
  });

  it("rejects invalid types and ids as 404s", async () => {
    await expect(
      loader(loaderArgs("/watch/title/person/1930", "person", "1930")),
    ).rejects.toMatchObject({
      init: { status: 404 },
    });
    await expect(
      loader(loaderArgs("/watch/title/movie/abc", "movie", "abc")),
    ).rejects.toMatchObject({
      init: { status: 404 },
    });
    await expect(
      loader(loaderArgs("/watch/title/movie/10331?season=-1", "movie", "10331")),
    ).rejects.toMatchObject({ init: { status: 404 } });
    expect(mocks.getCatalogTitle).not.toHaveBeenCalled();
  });
});

describe("watch title view", () => {
  it("plays a movie through the returned direct and HLS sources", async () => {
    renderTitle({ catalogTitle: movieTitle });

    expect(await screen.findByText("96 min")).toBeTruthy();
    expect(screen.getByText("Horror")).toBeTruthy();

    (await screen.findByRole("button", { name: /^play$/i })).click();

    await waitFor(() => expect(mocks.createCatalogPlayback).toHaveBeenCalledTimes(1));
    expect(mocks.createCatalogPlayback.mock.calls[0]![0]).toEqual({
      type: "movie",
      tmdbId: 10331,
    });

    await waitFor(() => expect(document.querySelector("video")).not.toBeNull());
    const video = document.querySelector("video")!;
    expect(screen.getByText(/Night\.of\.the\.Living\.Dead\.1968/)).toBeTruthy();

    // Compatibility mode is the default, so the dialog offers the direct stream and
    // switching to it proves both returned URLs reached the player.
    (await screen.findByRole("button", { name: /direct stream/i })).click();
    await waitFor(() =>
      expect(video.getAttribute("src")).toBe(
        "http://localhost:3000/api/search/token/play/play.mkv",
      ),
    );
  });

  it("reports an empty release list with a retry action", async () => {
    mocks.createCatalogPlayback.mockRejectedValue(
      new Error("No Usenet releases found for this title."),
    );
    renderTitle({ catalogTitle: movieTitle });

    (await screen.findByRole("button", { name: /^play$/i })).click();

    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toContain(
        "No Usenet releases found for this title.",
      ),
    );
    expect(screen.getByRole("button", { name: /retry/i })).toBeTruthy();
  });

  it("marks the resume episode and plays it from the primary action", async () => {
    const firstEpisode = seriesTitle.episodes[0]!;
    renderTitle({
      catalogTitle: {
        ...seriesTitle,
        episodes: [firstEpisode, { ...firstEpisode, episodeNumber: 2, name: "Getting Settled" }],
      },
      resumeEpisode: 2,
    });

    const resumeButton = await screen.findByRole("button", { name: /resume/i });
    expect(resumeButton.textContent).toContain("Resume");
    expect(screen.getByText(/Resume · S01E02/)).toBeTruthy();

    resumeButton.click();
    await waitFor(() =>
      expect(mocks.createCatalogPlayback).toHaveBeenLastCalledWith(
        { type: "series", tmdbId: 1930, season: 1, episode: 2 },
        expect.anything(),
      ),
    );
  });

  it("carries the season lineup into the player so episodes can be stepped", async () => {
    const firstEpisode = seriesTitle.episodes[0]!;
    renderTitle({
      catalogTitle: {
        ...seriesTitle,
        episodes: [firstEpisode, { ...firstEpisode, episodeNumber: 2, name: "Getting Settled" }],
      },
    });

    const playButtons = await screen.findAllByRole("button", { name: /^play$/i });
    playButtons[0]!.click();
    await waitFor(() => expect(mocks.createCatalogPlayback).toHaveBeenCalledTimes(1));

    fireEvent.click(await screen.findByRole("button", { name: "Next episode" }));

    await waitFor(() =>
      expect(mocks.createCatalogPlayback).toHaveBeenLastCalledWith(
        { type: "series", tmdbId: 1930, season: 1, episode: 2 },
        expect.anything(),
      ),
    );
  });

  it("plays an episode with its season and episode coordinates", async () => {
    renderTitle({ catalogTitle: seriesTitle });

    expect(await screen.findByRole("combobox", { name: /season/i })).toBeTruthy();
    expect(screen.getByText(/The Clampetts Strike Oil/)).toBeTruthy();

    screen.getByRole("button", { name: /^play$/i }).click();

    await waitFor(() => expect(mocks.createCatalogPlayback).toHaveBeenCalledTimes(1));
    expect(mocks.createCatalogPlayback.mock.calls[0]![0]).toEqual({
      type: "series",
      tmdbId: 1930,
      season: 1,
      episode: 1,
    });
  });
});
