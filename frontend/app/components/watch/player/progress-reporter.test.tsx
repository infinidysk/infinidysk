// @vitest-environment jsdom
import { render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { MediaPlayer } from "~/components/media-player/use-media-player";
import { ProgressReporter } from "./progress-reporter";

const mocks = vi.hoisted(() => ({
  reportWatchHistoryProgress: vi.fn().mockResolvedValue(undefined),
}));

vi.mock("~/clients/catalog-client", () => ({
  reportWatchHistoryProgress: mocks.reportWatchHistoryProgress,
}));

function fakePlayer(currentTime: number, duration: number): MediaPlayer {
  const element = document.createElement("video");
  Object.defineProperty(element, "currentTime", { value: currentTime, writable: true });
  Object.defineProperty(element, "duration", { value: duration, writable: true });
  return { mediaRef: { current: element } } as unknown as MediaPlayer;
}

afterEach(() => {
  vi.clearAllMocks();
});

describe("ProgressReporter", () => {
  it("reports the final position when the player unmounts", () => {
    const player = fakePlayer(42, 600);

    const { unmount } = render(
      <ProgressReporter player={player} target={{ type: "movie", tmdbId: 10331 }} />,
    );
    unmount();

    expect(mocks.reportWatchHistoryProgress).toHaveBeenCalledTimes(1);
    expect(mocks.reportWatchHistoryProgress).toHaveBeenCalledWith(
      expect.objectContaining({ type: "movie", tmdbId: 10331, position: 42, duration: 600 }),
      undefined,
    );
  });

  it("carries episode coordinates and skips an unplayed position", () => {
    const watched = fakePlayer(0, 1500);
    const first = render(
      <ProgressReporter
        player={watched}
        target={{ type: "series", tmdbId: 1930, season: 1, episode: 2 }}
      />,
    );
    first.unmount();
    expect(mocks.reportWatchHistoryProgress).not.toHaveBeenCalled();

    watched.mediaRef.current!.currentTime = 120;
    const second = render(
      <ProgressReporter
        player={watched}
        target={{ type: "series", tmdbId: 1930, season: 1, episode: 2 }}
      />,
    );
    second.unmount();

    expect(mocks.reportWatchHistoryProgress).toHaveBeenCalledWith(
      expect.objectContaining({ type: "series", season: 1, episode: 2, position: 120 }),
      undefined,
    );
  });

  it("marks the end of the media as finished and hands over to the next-episode hook", () => {
    vi.useFakeTimers();
    const player = fakePlayer(595, 600);
    const onEnded = vi.fn();
    const view = render(
      <ProgressReporter
        player={player}
        target={{ type: "movie", tmdbId: 10331 }}
        onEnded={onEnded}
      />,
    );

    vi.advanceTimersByTime(10_000);
    player.mediaRef.current!.currentTime = 600;
    player.mediaRef.current!.dispatchEvent(new Event("ended"));

    // No abort signal on the finished write: the switch to the next episode unmounts
    // this reporter right away and the request still has to land.
    expect(mocks.reportWatchHistoryProgress).toHaveBeenCalledWith(
      expect.objectContaining({ position: 600, finished: true }),
      undefined,
    );
    expect(onEnded).toHaveBeenCalledTimes(1);

    view.unmount();
    vi.useRealTimers();
  });
});
