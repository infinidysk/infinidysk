// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { PlayerOverlay } from "./player-overlay";
import type { MediaPlayer } from "~/components/media-player/use-media-player";

function fakePlayer(overrides: Record<string, unknown> = {}): MediaPlayer {
  const mediaRef = { current: null as HTMLMediaElement | null };
  return {
    mediaRef,
    setMediaEl: (el: HTMLMediaElement | null) => {
      mediaRef.current = el;
      return undefined;
    },
    handlers: {},
    status: "playing",
    mode: "hls",
    hlsAvailable: true,
    buffering: false,
    attempts: 0,
    maxAttempts: 3,
    error: null,
    startupMs: null,
    events: [],
    unavailableStatus: null,
    retry: vi.fn(),
    activateCompatibilityMode: vi.fn(),
    activateDirectMode: vi.fn(),
    lastGoodTimeRef: { current: 0 },
    lastProgressAtRef: { current: null },
    ...overrides,
  } as unknown as MediaPlayer;
}

/** jsdom has no media pipeline: stand in for the parts the OSD reads. */
function installMedia(el: HTMLVideoElement, options: { duration?: number; paused?: boolean } = {}) {
  let currentTime = 0;
  let paused = options.paused ?? true;
  Object.defineProperty(el, "currentTime", {
    configurable: true,
    get: () => currentTime,
    set: (value: number) => {
      currentTime = value;
    },
  });
  Object.defineProperty(el, "duration", {
    configurable: true,
    get: () => options.duration ?? 600,
  });
  Object.defineProperty(el, "paused", { configurable: true, get: () => paused });
  return {
    video: el,
    getTime: () => currentTime,
    setPaused: (next: boolean) => {
      paused = next;
    },
    emit: (name: string) => {
      act(() => {
        el.dispatchEvent(new Event(name));
      });
    },
  };
}

function renderOverlay(overrides: Record<string, unknown> = {}) {
  const player = fakePlayer(overrides);
  const onClose = vi.fn();
  const view = render(
    <PlayerOverlay player={player} title="Night of the Living Dead" releaseTitle="Night.1968.1080p" onClose={onClose} />,
  );
  return { ...view, player, onClose, media: installMedia(view.container.querySelector("video")!) };
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("PlayerOverlay", () => {
  it("shows the title, release and transport controls", () => {
    renderOverlay();

    expect(screen.getByText("Night of the Living Dead")).toBeTruthy();
    expect(screen.getByText("Night.1968.1080p")).toBeTruthy();
    expect(screen.getByLabelText("Seek")).toBeTruthy();
    expect(screen.getByLabelText("Volume")).toBeTruthy();
    expect(screen.getByLabelText("Fullscreen")).toBeTruthy();
    expect(screen.getByLabelText("Back 10 seconds")).toBeTruthy();
    expect(screen.getByLabelText("Forward 30 seconds")).toBeTruthy();
  });

  it("seeks with the keyboard and the timeline", () => {
    const { media } = renderOverlay();
    media.emit("durationchange");

    fireEvent.keyDown(window, { key: "ArrowRight" });
    expect(media.getTime()).toBe(30);

    fireEvent.keyDown(window, { key: "j" });
    expect(media.getTime()).toBe(20);

    fireEvent.change(screen.getByLabelText("Seek"), { target: { value: "420" } });
    expect(media.getTime()).toBe(420);
  });

  it("hides the chrome while playing and restores it on activity", () => {
    vi.useFakeTimers();
    const { container, media } = renderOverlay();
    const shell = container.querySelector("[data-osd]")!;
    expect(shell.getAttribute("data-osd")).toBe("visible");

    media.setPaused(false);
    media.emit("play");
    expect(shell.getAttribute("data-osd")).toBe("visible");

    act(() => {
      vi.advanceTimersByTime(2600);
    });
    expect(shell.getAttribute("data-osd")).toBe("hidden");

    fireEvent.pointerMove(shell);
    expect(shell.getAttribute("data-osd")).toBe("visible");
  });

  it("shows playback data on demand", () => {
    renderOverlay();

    fireEvent.click(screen.getByLabelText("Playback data"));
    const panel = screen.getByTestId("playback-data");
    expect(panel.textContent).toContain("Playback data");
    expect(panel.textContent).toContain("Buffer");
    expect(panel.textContent).toContain("Frames");

    fireEvent.click(screen.getByLabelText("Playback data"));
    expect(screen.queryByTestId("playback-data")).toBeNull();
  });

  it("toggles playback data with the i key", () => {
    renderOverlay();

    fireEvent.keyDown(window, { key: "i" });
    expect(screen.getByTestId("playback-data")).toBeTruthy();

    fireEvent.keyDown(window, { key: "i" });
    expect(screen.queryByTestId("playback-data")).toBeNull();
  });

  it("dismisses the speed menu on an outside click", () => {
    renderOverlay();

    fireEvent.click(screen.getByLabelText(/playback speed/i));
    expect(screen.getByRole("menu")).toBeTruthy();

    fireEvent.pointerDown(document.body);
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("closes on Escape and on the exit control", () => {
    const { onClose } = renderOverlay();

    fireEvent.keyDown(window, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);

    screen.getByLabelText("Exit player").click();
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("keeps the failure banner and its retry action", () => {
    const { player } = renderOverlay({
      status: "unsupported",
      error: { code: 4, message: "DEMUXER_ERROR_NO_SUPPORTED_STREAMS" },
    });

    expect(screen.getByRole("alert").textContent).toContain("DEMUXER_ERROR_NO_SUPPORTED_STREAMS");
    screen.getByRole("button", { name: /retry/i }).click();
    expect(player.retry).toHaveBeenCalledTimes(1);
  });
});
