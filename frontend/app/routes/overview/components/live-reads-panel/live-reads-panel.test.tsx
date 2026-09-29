// @vitest-environment jsdom
import { cleanup, fireEvent, render } from "@testing-library/react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { LiveReadsPanel, LiveReadsPanelContent, type LiveReadRow } from "./live-reads-panel";
import { LiveTiles } from "../live-tiles/live-tiles";
import type {
  AuthoritativePlaybackSession,
  CurrentPlaybackActivity,
  PlaybackAuthoritySnapshot,
} from "./current-activity";

const NOW = "2026-09-18T04:00:00.000Z";

function fixtureRead(
  id: string,
  fileName: string,
  overrides: Partial<LiveReadRow["read"]> = {},
): LiveReadRow["read"] {
  return {
    id,
    davItemId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    fileName,
    path: `/completed-symlinks/movies/${fileName}`,
    startedAt: "2026-09-18T03:40:00.000Z",
    lastActivityAt: NOW,
    bytesRead: 1_100_000_000,
    bytesFetched: 900_000_000,
    sourceOffset: 1_100_000_000,
    fileSize: 3_800_000_000,
    clientIp: "192.168.1.10",
    clientUserAgent: "rclone/1.73",
    playerSession: null,
    correlationScope: "None",
    matchingPlaybackSessionCount: 0,
    shared: false,
    providers: [{ host: "news.eweka.nl", nickname: "Eweka", segments: 17 }],
    ...overrides,
  };
}

function fixtureSession(
  nativeSessionId: string,
  title: string,
  overrides: Partial<AuthoritativePlaybackSession> = {},
): AuthoritativePlaybackSession {
  return {
    key: {
      sourceInstanceId: "11111111-1111-1111-1111-111111111111",
      nativeSessionId,
    },
    sourceInstanceName: "Plex Home",
    sourceType: "Plex",
    nativeSessionId,
    userName: "alice",
    clientName: "Plex",
    deviceName: "Living Room Shield",
    itemId: "plex-item-1",
    title,
    mediaType: "movie",
    seriesName: null,
    seasonNumber: null,
    episodeNumber: null,
    state: "Playing",
    positionMs: 4_462_000,
    durationMs: 9_948_000,
    deliveryMethod: "DirectPlay",
    mediaSourceId: "source-1",
    mediaSourcePath: "/movies/Dune Part Two.mkv",
    davItemId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    lastConfirmedAt: NOW,
    freshness: "Fresh",
    ...overrides,
  };
}

function playback(
  nativeSessionId: string,
  title: string,
  overrides: Partial<CurrentPlaybackActivity> = {},
): CurrentPlaybackActivity {
  return {
    session: fixtureSession(nativeSessionId, title),
    transportReadIds: [],
    hasSharedFileTransport: false,
    ...overrides,
  };
}

function row(read: LiveReadRow["read"], rate = 7_200_000): LiveReadRow {
  return { read, rate, history: [rate * 0.8, rate, rate * 1.1] };
}

const fixtureRows: LiveReadRow[] = [
  row(fixtureRead("read-1", "Read.One.mkv")),
  row(fixtureRead("read-2", "Read.Two.mkv")),
  row(fixtureRead("read-3", "Read.Three.mkv")),
  row(fixtureRead("read-4", "Read.Four.mkv")),
  row(fixtureRead("read-5", "9f2c7a1e4b.mkv", {
    path: "/completed-symlinks/movies/Interstellar.2014.1080p.BluRay.x264-GRP/9f2c7a1e4b.mkv",
  })),
];

describe("LiveReadsPanel", () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it("renders a truthful empty state", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanel />);

    expect(markup).toContain("Right now");
    expect(markup).toContain("No confirmed playback or active InfiniDysk reads right now.");
  });

  it("renders authoritative playback before transport-only reads with typed counts", () => {
    const playing = playback("plex-1", "Dune: Part Two");
    const paused = playback("plex-2", "Arrival", {
      session: fixtureSession("plex-2", "Arrival", { state: "Paused" }),
    });
    const read = row(fixtureRead("read-1", "Scanner.Read.mkv"));

    const markup = renderToStaticMarkup(
      <LiveReadsPanelContent playback={[paused, playing]} rows={[read]} />,
    );

    expect(markup).toContain("1 playing · 1 paused · 1 other reads");
    expect(markup.indexOf("Dune: Part Two")).toBeLessThan(markup.indexOf("Scanner.Read.mkv"));
    expect(markup).toContain("PLAYING");
    expect(markup).toContain("PAUSED");
    expect(markup).toContain("READ");
  });

  it("groups live totals and authoritative activity in one Right now card", () => {
    const tiles = {
      activeReads: 5,
      articlesPerMinute: 120,
      errorsPerMinute: 0,
      bytesServedPerMinute: 60_000_000,
    };
    const liveRead = row(fixtureRead("read-live", "Live.Read.mkv"));
    const { container, getByRole, rerender } = render(
      <LiveReadsPanelContent
        playback={[]}
        rows={[liveRead]}
        summary={<LiveTiles tiles={tiles} />}
      />,
    );

    expect(container.querySelectorAll("section.card")).toHaveLength(1);
    expect(getByRole("region", { name: "Live status" }).closest("section")).toBe(
      getByRole("list").closest("section"),
    );
    expect(container.textContent).toContain("1.0 MB/s");
    expect(container.textContent).not.toContain("1 other reads");
    expect(container.textContent).not.toContain("5 active");

    rerender(
      <LiveReadsPanelContent
        playback={[playback("plex-live", "Live Movie")]}
        rows={[liveRead]}
        summary={<LiveTiles tiles={tiles} />}
      />,
    );
    expect(container.textContent).toContain("1 playing · 1 other reads");

    rerender(
      <LiveReadsPanelContent
        playback={[]}
        rows={[]}
        summary={<LiveTiles tiles={{ ...tiles, activeReads: 0, bytesServedPerMinute: 0 }} />}
      />,
    );
    expect(container.textContent).toContain("0 B/s");
    expect(container.textContent).toContain(
      "No confirmed playback or active InfiniDysk reads right now.",
    );
  });

  it("uses authoritative viewer position rather than WebDAV source offset", () => {
    const transport = row(
      fixtureRead("read-1", "Dune.Part.Two.mkv", {
        matchingPlaybackSessionCount: 1,
        correlationScope: "File",
        sourceOffset: 3_500_000_000,
        fileSize: 3_800_000_000,
      }),
      38_000_000,
    );
    const activity = playback("plex-1", "Dune: Part Two", {
      transportReadIds: ["read-1"],
    });

    const markup = renderToStaticMarkup(
      <LiveReadsPanelContent playback={[activity]} rows={[transport]} />,
    );

    expect(markup).toContain("1:14:22 / 2:45:48");
    expect(markup).toContain("InfiniDysk source: 38.0 MB/s");
    expect(markup).not.toContain("source 3.5 GB");
  });

  it("keeps a paused playback row when there is no current transport", () => {
    const activity = playback("plex-1", "Dune: Part Two", {
      session: fixtureSession("plex-1", "Dune: Part Two", {
        state: "Paused",
        positionMs: 4_505_000,
      }),
    });

    const markup = renderToStaticMarkup(<LiveReadsPanelContent playback={[activity]} rows={[]} />);

    expect(markup).toContain("PAUSED");
    expect(markup).toContain("1:15:05 / 2:45:48");
    expect(markup).toContain("InfiniDysk source: idle");
  });

  it("never labels an unattributed transport read as playback", () => {
    const markup = renderToStaticMarkup(
      <LiveReadsPanelContent playback={[]} rows={[row(fixtureRead("read-1", "Scan.mkv"))]} />,
    );

    expect(markup).toContain("READ");
    expect(markup).toContain("No matching playback session");
    expect(markup).not.toContain("PLAYING");
    expect(markup).not.toContain("PAUSED");
    expect(markup).toContain("source 1.1 GB");
  });

  it("marks shared file transport instead of assigning it to one viewer", () => {
    const sharedRead = row(
      fixtureRead("read-shared", "Shared.Movie.mkv", {
        matchingPlaybackSessionCount: 2,
        correlationScope: "File",
        shared: true,
      }),
    );
    const first = playback("plex-a", "Shared Movie", {
      transportReadIds: ["read-shared"],
      hasSharedFileTransport: true,
    });
    const second = playback("plex-b", "Shared Movie", {
      session: fixtureSession("plex-b", "Shared Movie", { userName: "bob" }),
      transportReadIds: ["read-shared"],
      hasSharedFileTransport: true,
    });

    const markup = renderToStaticMarkup(
      <LiveReadsPanelContent playback={[first, second]} rows={[sharedRead]} />,
    );

    expect(markup.match(/shared\/file-level transport/g)).toHaveLength(2);
    expect(markup).not.toContain("No matching playback session");
  });

  it("surfaces stale or unavailable playback authority without clearing known rows", () => {
    const authority: PlaybackAuthoritySnapshot = {
      sourceInstanceId: "11111111-1111-1111-1111-111111111111",
      sourceInstanceName: "Plex Home",
      sourceType: "Plex",
      available: false,
      isStale: true,
      lastSuccessfulPollAt: "2026-09-18T03:59:00.000Z",
      lastFailureAt: NOW,
      lastErrorKind: "timeout",
    };
    const activity = playback("plex-1", "Dune: Part Two", {
      session: fixtureSession("plex-1", "Dune: Part Two", { freshness: "Stale" }),
    });

    const markup = renderToStaticMarkup(
      <LiveReadsPanelContent playback={[activity]} rows={[]} authorities={[authority]} />,
    );

    expect(markup).toContain("Plex Home: playback status stale/unavailable");
    expect(markup).toContain("STALE");
    expect(markup).toContain("Dune: Part Two");
  });

  it("shows source telemetry and providers only as transport detail", () => {
    const transport = row(
      fixtureRead("read-1", "Dune.Part.Two.mkv", {
        matchingPlaybackSessionCount: 1,
        correlationScope: "File",
        bytesFetched: 1_200_000_000,
      }),
      7_200_000,
    );
    const activity = playback("plex-1", "Dune: Part Two", {
      transportReadIds: ["read-1"],
    });

    const markup = renderToStaticMarkup(
      <LiveReadsPanelContent playback={[activity]} rows={[transport]} />,
    );

    expect(markup).toContain("InfiniDysk source: 7.2 MB/s · fetched 1.2 GB");
    expect(markup).toContain("Eweka");
    expect(markup).toContain("Direct Play");
    expect(markup).toContain("Living Room Shield");
  });

  it("prefixes the parent folder while preserving the actual filename", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent playback={[]} rows={fixtureRows} />);

    expect(markup).toContain("Interstellar.2014.1080p.BluRay.x264-GRP/9f2c7a1e4b.mkv</span>");
  });

  it.each([
    ["d.mkv", "Movie Title/d.mkv"],
    ["MOVIE TITLE.mkv", "MOVIE TITLE.mkv"],
  ])("uses resolved parent metadata for ID reads of %s", (fileName, expected) => {
    const row = fixtureRows[0]!;
    const rows = [
      {
        ...row,
        read: { ...row.read, fileName, path: "/.ids/id", parentDirectoryName: "Movie Title" },
      },
    ];
    const markup = renderToStaticMarkup(<LiveReadsPanelContent playback={[]} rows={rows} />);
    expect(markup).toContain(`${expected}</span>`);
  });

  it("keeps playback and transport rows live under the scroll lock", () => {
    let cardTop = 80;
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (
      this: HTMLElement,
    ) {
      const top = this.tagName === "MAIN" ? 64 : cardTop;
      const height = this.tagName === "MAIN" ? 600 : 240;
      return { ...rectWithHeight(height), top, bottom: top + height };
    });
    const activity = playback("plex-paused", "Paused Movie", {
      session: fixtureSession("plex-paused", "Paused Movie", { state: "Paused" }),
    });
    const view = (rows: LiveReadRow[], paused = false) => (
      <main>
        <LiveReadsPanelContent playback={[activity]} rows={rows} paused={paused} />
      </main>
    );
    const { container, rerender } = render(view([]));
    const card = container.querySelector("section")!;
    const scrollRoot = container.querySelector("main")!;
    expect(card.style.height).toBe("");
    expect(container.textContent).toContain("1 paused");

    cardTop = 63;
    fireEvent.scroll(scrollRoot);
    expect(card.style.height).toBe("240px");
    rerender(view(fixtureRows));
    expect(card.style.height).toBe("240px");
    expect(container.querySelectorAll("li")).toHaveLength(6);
    expect(container.textContent).toContain("1 paused · 5 other reads");
    expect(container.querySelector("ul")?.className).toContain("max-h-80");
    expect(container.querySelector(".card-body")?.className).toContain("overflow-y-auto");

    rerender(view(fixtureRows, true));
    expect(card.style.height).toBe("");
    expect(container.textContent).toContain("PAUSED");
  });

  it("follows the live read count instead of freezing the first snapshot height", () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(rectWithHeight(240));

    const { container, rerender } = render(
      <LiveReadsPanelContent playback={[]} rows={fixtureRows.slice(0, 4)} />,
    );
    const section = container.querySelector("section");
    expect(section?.style.height).toBe("");
    expect(container.querySelectorAll("li")).toHaveLength(4);

    rerender(<LiveReadsPanelContent playback={[]} rows={fixtureRows.slice(0, 3)} />);
    expect(section?.style.height).toBe("");
    expect(container.querySelectorAll("li")).toHaveLength(3);

    rerender(<LiveReadsPanelContent playback={[]} rows={fixtureRows} />);
    expect(section?.style.height).toBe("");
    expect(container.querySelectorAll("li")).toHaveLength(5);
  });

  it("does not keep the empty-state height once reads start", () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(rectWithHeight(120));

    const { container, rerender } = render(<LiveReadsPanelContent playback={[]} rows={[]} />);
    const section = container.querySelector("section");
    expect(section?.style.height).toBe("");

    rerender(<LiveReadsPanelContent playback={[]} rows={fixtureRows.slice(0, 2)} />);
    expect(section?.style.height).toBe("");
    expect(container.querySelectorAll("li")).toHaveLength(2);

    rerender(<LiveReadsPanelContent playback={[]} rows={[]} />);
    expect(section?.style.height).toBe("");
    expect(container.textContent).toContain("No confirmed playback or active InfiniDysk reads right now.");
  });

  it("caps the read list height so extra sessions scroll inside the card", () => {
    const { container } = render(<LiveReadsPanelContent playback={[]} rows={fixtureRows} />);

    const list = container.querySelector("ul");
    expect(list?.className).toContain("max-h-80");
    expect(list?.className).toContain("overflow-y-auto");
    expect(list?.className).toContain("yes-scrollbar");
  });

  it("locks on partial scroll-out while reads and totals stay live, then unlocks on return", () => {
    let cardTop = 80;
    let naturalHeight = 240.25;
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (
      this: HTMLElement,
    ) {
      const top = this.tagName === "MAIN" ? 64 : cardTop;
      const height = this.tagName === "MAIN" ? 600 : naturalHeight;
      return { ...rectWithHeight(height), top, bottom: top + height };
    });
    const view = (rows: LiveReadRow[], total: string) => (
      <main>
        <LiveReadsPanelContent playback={[]} rows={rows} summary={<span>{total}</span>} />
      </main>
    );
    const { container, rerender } = render(view(fixtureRows.slice(0, 2), "Initial total"));
    const card = container.querySelector("section")!;
    const scrollRoot = container.querySelector("main")!;
    expect(card.style.height).toBe("");

    cardTop = 64;
    fireEvent.scroll(scrollRoot);
    expect(card.style.height).toBe("");

    cardTop = 63;
    fireEvent.scroll(scrollRoot);
    expect(Number.parseFloat(card.style.height)).toBeGreaterThanOrEqual(240.25);
    const lockedHeight = card.style.height;
    naturalHeight = 400;
    rerender(view(fixtureRows, "Updated total"));
    expect(card.style.height).toBe(lockedHeight);
    expect(container.querySelectorAll("li")).toHaveLength(5);
    expect(container.textContent).toContain("Updated total");

    rerender(view([], "Idle total"));
    expect(card.style.height).toBe(lockedHeight);
    expect(container.textContent).toContain("No confirmed playback or active InfiniDysk reads right now.");

    cardTop = 64;
    fireEvent.scroll(scrollRoot);
    expect(card.style.height).toBe("");
  });

  it.each([-100, 700])("does not capture an initial off-screen height at top=%s", (initialTop) => {
    const panel = renderScrollingPanel(initialTop);
    expect(panel.card.style.height).toBe("");
    panel.geometry.top = 63;
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("");

    panel.geometry.top = 80;
    panel.update(fixtureRows);
    panel.geometry.top = 63;
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("240px");
  });

  it("uses the last visible height if data arrives before the scroll event", () => {
    const panel = renderScrollingPanel();
    panel.geometry.top = 63;
    panel.geometry.height = 400;
    panel.update(fixtureRows);
    expect(panel.card.style.height).toBe("240px");
    expect(panel.card.querySelectorAll("li")).toHaveLength(5);
  });

  it("locks the unscaled CSS height rather than a zoomed rectangle", () => {
    vi.spyOn(window, "getComputedStyle").mockReturnValue({
      height: "192.2px",
    } as CSSStyleDeclaration);
    const panel = renderScrollingPanel(80, 240.25);
    panel.geometry.top = 63;
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("193px");
  });

  it("remeasures on width changes but not on live content height changes", () => {
    let notifyResize: (() => void) | undefined;
    const disconnect = vi.fn();
    vi.stubGlobal(
      "ResizeObserver",
      class {
        constructor(callback: () => void) {
          notifyResize = callback;
        }
        observe = vi.fn();
        disconnect = disconnect;
      },
    );
    const panel = renderScrollingPanel();
    panel.geometry.top = 63;
    fireEvent.scroll(panel.scrollRoot);
    panel.geometry.height = 400;
    notifyResize?.();
    expect(panel.card.style.height).toBe("240px");

    panel.geometry.width = 300;
    notifyResize?.();
    expect(panel.card.style.height).toBe("400px");
    panel.unmount();
    expect(disconnect).toHaveBeenCalledOnce();
    expect(panel.card.style.height).toBe("");
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("");
  });

  it("releases the lock in layout edit mode and waits for visibility before locking again", () => {
    const panel = renderScrollingPanel();
    panel.geometry.top = 63;
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("240px");
    panel.update(fixtureRows, true);
    expect(panel.card.style.height).toBe("");
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("");
    panel.update(fixtureRows, false);
    expect(panel.card.style.height).toBe("");
    panel.geometry.top = 80;
    fireEvent.scroll(panel.scrollRoot);
    panel.geometry.top = 63;
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("240px");
  });

  it("does not capture zero-sized geometry", () => {
    const panel = renderScrollingPanel(80, 0);
    panel.geometry.top = 63;
    panel.geometry.height = 240;
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("");
    panel.geometry.top = 80;
    fireEvent.scroll(panel.scrollRoot);
    panel.geometry.top = 63;
    fireEvent.scroll(panel.scrollRoot);
    expect(panel.card.style.height).toBe("240px");
  });
});

function renderScrollingPanel(top = 80, height = 240) {
  const geometry = { top, height, width: 400 };
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (
    this: HTMLElement,
  ) {
    if (this.tagName === "MAIN") return { ...rectWithHeight(600), top: 64, bottom: 664 };
    return {
      ...rectWithHeight(geometry.height),
      ...geometry,
      bottom: geometry.top + geometry.height,
    };
  });
  const view = (rows: LiveReadRow[], paused = false) => (
    <main>
      <LiveReadsPanelContent playback={[]} rows={rows} paused={paused} />
    </main>
  );
  const rendered = render(view(fixtureRows.slice(0, 2)));
  return {
    geometry,
    card: rendered.container.querySelector("section")!,
    scrollRoot: rendered.container.querySelector("main")!,
    update: (rows: LiveReadRow[], paused = false) => rendered.rerender(view(rows, paused)),
    unmount: rendered.unmount,
  };
}

function rectWithHeight(height: number): DOMRect {
  return {
    x: 0,
    y: 0,
    width: 400,
    height,
    top: 0,
    left: 0,
    right: 400,
    bottom: height,
    toJSON: () => ({}),
  };
}