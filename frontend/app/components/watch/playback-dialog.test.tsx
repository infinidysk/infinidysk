// @vitest-environment jsdom
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PlaybackDialog, type PlaybackTarget } from "./playback-dialog";

const mocks = vi.hoisted(() => ({
  createCatalogPlayback: vi.fn(),
  fetchCatalogSeason: vi.fn(),
  reportWatchHistoryProgress: vi.fn(),
  mediaRef: { current: null as HTMLMediaElement | null },
}));

vi.mock("~/clients/catalog-client", () => ({
  createCatalogPlayback: mocks.createCatalogPlayback,
  fetchCatalogSeason: mocks.fetchCatalogSeason,
  reportWatchHistoryProgress: mocks.reportWatchHistoryProgress,
}));

vi.mock("~/components/media-player/use-media-player", () => ({
  useMediaPlayer: () => ({
    mediaRef: mocks.mediaRef,
    handlers: {},
    setMediaEl: (element: HTMLMediaElement | null) => {
      mocks.mediaRef.current = element;
    },
    status: "playing",
    mode: "hls",
    hlsAvailable: true,
    buffering: false,
    error: null,
  }),
}));

// A stand-in for the OSD that exposes exactly the episode hooks under test.
vi.mock("~/components/watch/player/player-overlay", () => ({
  PlayerOverlay: ({
    title,
    episodeNav,
  }: {
    title: string;
    episodeNav?: { onPrevious: (() => void) | null; onNext: (() => void) | null } | undefined;
  }) => (
    <div>
      <h1>{title}</h1>
      <button
        type="button"
        disabled={!episodeNav?.onPrevious}
        onClick={() => episodeNav?.onPrevious?.()}
      >
        Previous episode
      </button>
      <button type="button" disabled={!episodeNav?.onNext} onClick={() => episodeNav?.onNext?.()}>
        Next episode
      </button>
    </div>
  ),
}));

const lineup = [
  { season: 1, episode: 1, name: "The Clampetts Strike Oil" },
  { season: 1, episode: 2, name: "Getting Settled" },
  { season: 1, episode: 3, name: "Meanwhile, Back at the Cabin" },
];

function seriesTarget(episode: number): PlaybackTarget {
  const entry = lineup.find((candidate) => candidate.episode === episode)!;
  return {
    type: "series",
    tmdbId: 1930,
    season: entry.season,
    episode: entry.episode,
    label: `S01E0${entry.episode} · ${entry.name}`,
    lineup,
    seasons: [1, 2],
  };
}

/** jsdom has no media pipeline: the reporter only reads position and duration. */
function endedVideo() {
  const video = document.createElement("video");
  Object.defineProperty(video, "currentTime", { value: 1400, writable: true, configurable: true });
  Object.defineProperty(video, "duration", { value: 1500, configurable: true });
  return video;
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.mediaRef.current = endedVideo();
  // jsdom has no modal dialog implementation.
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
  mocks.reportWatchHistoryProgress.mockResolvedValue(undefined);
  mocks.createCatalogPlayback.mockImplementation((request: { episode?: number }) =>
    Promise.resolve({
      status: true,
      playbackId: `playback-e${request.episode}`,
      directUrl: "http://localhost:3000/api/search/token/play/play.mkv",
      hlsUrl: "http://localhost:3000/playlist.m3u8",
      releaseTitle: "release",
    }),
  );
});

afterEach(cleanup);

describe("PlaybackDialog episode navigation", () => {
  it("opens on the requested episode", async () => {
    render(<PlaybackDialog target={seriesTarget(2)} onClose={vi.fn()} />);

    expect(await screen.findByRole("heading", { name: /S01E02 · Getting Settled/ })).toBeTruthy();
    expect(mocks.createCatalogPlayback).toHaveBeenCalledWith(
      { type: "series", tmdbId: 1930, season: 1, episode: 2 },
      expect.anything(),
    );
  });

  it("closes out the finished episode and autoplays the next one", async () => {
    render(<PlaybackDialog target={seriesTarget(2)} onClose={vi.fn()} />);
    await screen.findByRole("heading", { name: /S01E02/ });
    // The player commits inside a promise callback, so React defers its passive effects
    // to the next act tick: flush them before the media event.
    await act(async () => {});

    act(() => {
      mocks.mediaRef.current!.dispatchEvent(new Event("ended"));
    });

    // The finished write belongs to the episode that ended, and it is not aborted by
    // the switch: the reporter passes no signal for that report.
    expect(mocks.reportWatchHistoryProgress).toHaveBeenCalledWith(
      expect.objectContaining({ episode: 2, position: 1400, finished: true }),
      undefined,
    );

    await waitFor(() =>
      expect(mocks.createCatalogPlayback).toHaveBeenLastCalledWith(
        { type: "series", tmdbId: 1930, season: 1, episode: 3 },
        expect.anything(),
      ),
    );
    expect(await screen.findByRole("heading", { name: /S01E03/ })).toBeTruthy();
  });

  it("steps backwards and forwards through the season", async () => {
    render(<PlaybackDialog target={seriesTarget(2)} onClose={vi.fn()} />);
    await screen.findByRole("heading", { name: /S01E02/ });

    screen.getByRole("button", { name: "Previous episode" }).click();
    await waitFor(() =>
      expect(mocks.createCatalogPlayback).toHaveBeenLastCalledWith(
        expect.objectContaining({ episode: 1 }),
        expect.anything(),
      ),
    );

    await screen.findByRole("heading", { name: /S01E01/ });
    screen.getByRole("button", { name: "Next episode" }).click();
    await waitFor(() =>
      expect(mocks.createCatalogPlayback).toHaveBeenLastCalledWith(
        expect.objectContaining({ episode: 2 }),
        expect.anything(),
      ),
    );
  });

  it("rolls a season finale into the next season's first episode", async () => {
    mocks.fetchCatalogSeason.mockResolvedValue({
      status: true,
      title: null,
      seasons: [{ seasonNumber: 2, name: "Season 2", episodeCount: 1 }],
      selectedSeason: 2,
      episodes: [
        {
          seasonNumber: 2,
          episodeNumber: 1,
          name: "The Clampetts in New York",
          overview: null,
          airDate: null,
          runtimeMinutes: null,
          stillUrl: null,
        },
      ],
    });

    render(<PlaybackDialog target={seriesTarget(3)} onClose={vi.fn()} />);
    await screen.findByRole("heading", { name: /S01E03/ });
    await act(async () => {});

    // The finale still offers "next": the following season exists.
    expect(screen.getByRole<HTMLButtonElement>("button", { name: "Next episode" }).disabled).toBe(
      false,
    );

    act(() => {
      mocks.mediaRef.current!.dispatchEvent(new Event("ended"));
    });

    await waitFor(() =>
      expect(mocks.fetchCatalogSeason).toHaveBeenCalledWith(1930, 2, expect.anything()),
    );
    await waitFor(() =>
      expect(mocks.createCatalogPlayback).toHaveBeenLastCalledWith(
        { type: "series", tmdbId: 1930, season: 2, episode: 1 },
        expect.anything(),
      ),
    );
    expect(
      await screen.findByRole("heading", { name: /S02E01 · The Clampetts in New York/ }),
    ).toBeTruthy();
  });

  it("stops at the end of the last season instead of autoplaying", async () => {
    render(<PlaybackDialog target={{ ...seriesTarget(3), seasons: [1] }} onClose={vi.fn()} />);
    await screen.findByRole("heading", { name: /S01E03/ });
    await act(async () => {});

    expect(screen.getByRole<HTMLButtonElement>("button", { name: "Next episode" }).disabled).toBe(
      true,
    );

    act(() => {
      mocks.mediaRef.current!.dispatchEvent(new Event("ended"));
    });

    expect(mocks.createCatalogPlayback).toHaveBeenCalledTimes(1);
  });

  it("offers no episode stepping for a movie", async () => {
    render(
      <PlaybackDialog
        target={{ type: "movie", tmdbId: 10331, label: "Night of the Living Dead" }}
        onClose={vi.fn()}
      />,
    );
    await screen.findByRole("heading", { name: "Night of the Living Dead" });

    expect(
      screen.getByRole<HTMLButtonElement>("button", { name: "Previous episode" }).disabled,
    ).toBe(true);
    expect(screen.getByRole<HTMLButtonElement>("button", { name: "Next episode" }).disabled).toBe(
      true,
    );
  });
});
