// @vitest-environment jsdom
import { act, cleanup, render, waitFor } from "@testing-library/react";
import { useEffect } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useMediaPlayer, type MediaPlayer } from "./use-media-player";

type HlsHandler = (event: string, data: Record<string, unknown>) => void;

const hlsMock = vi.hoisted(() => {
  class FakeHls {
    static instances: FakeHls[] = [];
    static isSupported = (): boolean => true;
    static Events = { MANIFEST_PARSED: "hlsManifestParsed", ERROR: "hlsError" };
    static ErrorTypes = {
      NETWORK_ERROR: "networkError",
      MEDIA_ERROR: "mediaError",
      OTHER_ERROR: "otherError",
    };

    readonly handlers = new Map<string, HlsHandler>();
    readonly sources: string[] = [];
    attachCount = 0;
    destroyed = 0;

    constructor() {
      FakeHls.instances.push(this);
    }

    on(event: string, handler: HlsHandler) {
      this.handlers.set(event, handler);
    }

    attachMedia() {
      this.attachCount += 1;
    }

    loadSource(source: string) {
      this.sources.push(source);
    }

    destroy() {
      this.destroyed += 1;
    }

    emit(event: string, data: Record<string, unknown> = {}) {
      this.handlers.get(event)?.(event, data);
    }
  }

  return { FakeHls };
});

vi.mock("hls.js", () => ({ default: hlsMock.FakeHls }));

const DIRECT_SRC = "/api/search/token/play/play.mkv";
const HLS_SRC = "/api/catalog/playback/session/playlist.m3u8";

let playMock: ReturnType<typeof vi.fn>;
let loadMock: ReturnType<typeof vi.fn>;
let currentTimeWrites: number[];

beforeEach(() => {
  hlsMock.FakeHls.instances.length = 0;
  currentTimeWrites = [];
  playMock = vi.fn<() => Promise<void>>().mockResolvedValue(undefined);
  loadMock = vi.fn<() => void>();
  Object.defineProperty(HTMLMediaElement.prototype, "play", {
    configurable: true,
    value: playMock,
  });
  Object.defineProperty(HTMLMediaElement.prototype, "load", {
    configurable: true,
    value: loadMock,
  });
  // jsdom has no media pipeline: stand in for the parts the hook reads and writes.
  Object.defineProperty(HTMLMediaElement.prototype, "canPlayType", {
    configurable: true,
    value: () => "",
  });
  hlsMock.FakeHls.isSupported = () => true;
  // Code-4 handling probes the source: served bytes mean the browser is at fault.
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({ ok: true, status: 200, headers: new Headers() }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

type PlayerRef = { current: MediaPlayer | null };

function Harness({
  src,
  hlsFallbackSrc,
  playerRef,
}: {
  src: string;
  hlsFallbackSrc?: string;
  playerRef: PlayerRef;
}) {
  const player = useMediaPlayer({ src, ...(hlsFallbackSrc ? { hlsFallbackSrc } : {}) });
  useEffect(() => {
    playerRef.current = player;
  }, [player, playerRef]);
  return <video ref={player.setMediaEl} {...player.handlers} />;
}

function renderPlayer(options: { src?: string; hlsFallbackSrc?: string } = {}) {
  const playerRef: PlayerRef = { current: null };
  const view = render(
    <Harness
      src={options.src ?? DIRECT_SRC}
      {...(options.hlsFallbackSrc === undefined
        ? { hlsFallbackSrc: HLS_SRC }
        : { hlsFallbackSrc: options.hlsFallbackSrc })}
      playerRef={playerRef}
    />,
  );
  const video = view.container.querySelector("video")!;
  const media = installMediaProperties(video);
  return { ...view, video, playerRef, media };
}

function installMediaProperties(el: HTMLVideoElement, initialDuration = 600) {
  let duration = initialDuration;
  const setDuration = (next: number) => {
    duration = next;
  };
  let currentTime = 0;
  Object.defineProperty(el, "currentTime", {
    configurable: true,
    get: () => currentTime,
    set: (value: number) => {
      currentTime = value;
      currentTimeWrites.push(value);
    },
  });
  Object.defineProperty(el, "duration", { configurable: true, get: () => duration });
  Object.defineProperty(el, "paused", { configurable: true, get: () => false });
  return { setDuration };
}

function fireMediaError(el: HTMLMediaElement, code: number, message = "boom") {
  Object.defineProperty(el, "error", {
    configurable: true,
    get: () => ({ code, message }),
  });
  act(() => {
    el.dispatchEvent(new Event("error"));
  });
}

describe("useMediaPlayer playback modes", () => {
  const startDirect = async (playerRef: PlayerRef) => {
    act(() => {
      playerRef.current?.activateDirectMode();
    });
    await waitFor(() => expect(playerRef.current?.mode).toBe("direct"));
  };

  it("starts in compatibility mode when a fallback exists", async () => {
    const { playerRef } = renderPlayer();

    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    expect(playerRef.current?.mode).toBe("hls");
    expect(playerRef.current?.status).toBe("transcoding");
    expect(hlsMock.FakeHls.instances[0]!.sources).toEqual([HLS_SRC]);
  });

  it("stays direct when the caller supplies no compatibility source", async () => {
    const { video, playerRef } = renderPlayer({ hlsFallbackSrc: "" });

    fireMediaError(video, 4);

    await waitFor(() => expect(playerRef.current?.status).toBe("unsupported"));
    expect(playerRef.current?.mode).toBe("direct");
    expect(playerRef.current?.hlsAvailable).toBe(false);
    expect(hlsMock.FakeHls.instances).toHaveLength(0);
  });

  it("keeps recovering the direct stream on network errors", async () => {
    const { video, playerRef } = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    // Switch before installing fake timers: waitFor needs real ones.
    await startDirect(playerRef);

    vi.useFakeTimers();
    try {
      fireMediaError(video, 2, "network down");

      expect(playerRef.current?.status).toBe("recovering");
      expect(playerRef.current?.mode).toBe("direct");
    } finally {
      vi.useRealTimers();
    }
  });

  it("falls back to the direct stream when the browser cannot run HLS", async () => {
    hlsMock.FakeHls.isSupported = () => false;
    const { video, playerRef } = renderPlayer();

    await waitFor(() => expect(playerRef.current?.mode).toBe("direct"));
    expect(playerRef.current?.status).toBe("loading");
    expect(video.getAttribute("src")).toBe(DIRECT_SRC);
    expect(hlsMock.FakeHls.instances).toHaveLength(0);
  });

  it("reports a rejected direct stream instead of reverting on its own", async () => {
    const { video, playerRef } = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    await startDirect(playerRef);

    // The user asked for the original file and the browser refused it: say so, and
    // let them pick Compatibility mode again. Never bounce modes behind their back.
    fireMediaError(video, 4, "DEMUXER_ERROR_COULD_NOT_OPEN");

    await waitFor(() => expect(playerRef.current?.status).toBe("unsupported"));
    expect(playerRef.current?.mode).toBe("direct");
    expect(hlsMock.FakeHls.instances).toHaveLength(1);
  });

  it("preserves the position when switching modes manually", async () => {
    const { video, playerRef } = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    video.currentTime = 42;

    await startDirect(playerRef);
    expect(playerRef.current?.lastGoodTimeRef.current).toBe(42);

    act(() => {
      playerRef.current?.activateCompatibilityMode();
    });
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(2));
    expect(playerRef.current?.mode).toBe("hls");

    act(() => {
      video.dispatchEvent(new Event("loadedmetadata"));
    });

    expect(currentTimeWrites).toContain(42);
  });

  it("resumes at the last played position after a failed mode switch", async () => {
    const { video, playerRef } = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    video.currentTime = 87;

    await startDirect(playerRef);
    // The rejected direct load resets the element to the start.
    video.currentTime = 0;

    act(() => {
      playerRef.current?.activateCompatibilityMode();
    });
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(2));

    act(() => {
      video.dispatchEvent(new Event("loadedmetadata"));
    });

    expect(currentTimeWrites).toContain(87);
  });

  it("destroys the Hls instance when the source changes and on unmount", async () => {
    const first = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    const instance = hlsMock.FakeHls.instances[0]!;

    first.rerender(
      <Harness
        src="/api/search/token/play/other.mkv"
        hlsFallbackSrc={HLS_SRC}
        playerRef={first.playerRef}
      />,
    );
    await waitFor(() => expect(instance.destroyed).toBeGreaterThan(0));
    // The new source keeps the default mode, so the pipeline is rebuilt.
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(2));
    const rebuilt = hlsMock.FakeHls.instances[1]!;

    // Unmounting the preview releases the rebuilt pipeline too.
    first.unmount();
    expect(rebuilt.destroyed).toBeGreaterThan(0);

    const second = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(3));
    const mounted = hlsMock.FakeHls.instances[2]!;

    second.unmount();
    expect(mounted.destroyed).toBeGreaterThan(0);
  });

  it("recovers through the HLS stream instead of falling back to direct playback", async () => {
    const { playerRef } = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    const instance = hlsMock.FakeHls.instances[0]!;
    expect(instance.attachCount).toBe(1);

    act(() => {
      instance.emit("hlsError", { fatal: true, type: "networkError", details: "fragLoadError" });
    });

    expect(playerRef.current?.mode).toBe("hls");
    expect(playerRef.current?.status).toBe("recovering");
  });

  it("recovers a fatal HLS media error instead of ending playback", async () => {
    const { playerRef } = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));

    act(() => {
      hlsMock.FakeHls.instances[0]!.emit("hlsError", {
        fatal: true,
        type: "mediaError",
        details: "bufferAppendError",
      });
    });

    // A bad fragment is transient: the stream is re-attached in place, not abandoned.
    expect(playerRef.current?.status).toBe("recovering");
    expect(playerRef.current?.mode).toBe("hls");
  });

  it("resumes at the requested position once MSE reports a duration", async () => {
    const { video, playerRef, media } = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    media.setDuration(Number.NaN);
    video.currentTime = 42;
    currentTimeWrites.length = 0; // ignore the setup write above

    act(() => {
      playerRef.current?.activateCompatibilityMode();
    });

    // The manifest arrives while the duration is still unknown: no seek yet.
    act(() => {
      hlsMock.FakeHls.instances[0]!.emit("hlsManifestParsed");
    });
    expect(currentTimeWrites).not.toContain(42);

    // Re-attaching the stream resets the element to 0; the resume must restore 42.
    video.currentTime = 0;
    currentTimeWrites.length = 0;
    media.setDuration(600);
    act(() => {
      video.dispatchEvent(new Event("durationchange"));
    });

    expect(currentTimeWrites).toEqual([42]);
  });

  it("lets a user seek override a queued resume", async () => {
    const { video, playerRef, media } = renderPlayer();
    await waitFor(() => expect(hlsMock.FakeHls.instances.length).toBe(1));
    media.setDuration(Number.NaN);
    video.currentTime = 42;

    act(() => {
      playerRef.current?.activateCompatibilityMode();
    });

    // The user scrubs elsewhere before the queued resume could be applied.
    video.currentTime = 400;
    act(() => {
      video.dispatchEvent(new Event("seeking"));
    });
    const writesAfterScrub = currentTimeWrites.length;

    media.setDuration(600);
    act(() => {
      video.dispatchEvent(new Event("durationchange"));
    });

    expect(currentTimeWrites.slice(writesAfterScrub)).not.toContain(42);
  });
});
