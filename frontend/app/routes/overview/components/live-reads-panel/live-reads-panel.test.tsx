// @vitest-environment jsdom
import { cleanup, render } from "@testing-library/react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { LiveReadsPanel, LiveReadsPanelContent, type LiveReadRow } from "./live-reads-panel";
import { LiveTiles } from "../live-tiles/live-tiles";
import type {
  AuthoritativePlaybackSession,
  CurrentPlaybackActivity,
  CurrentTransportActivity,
  PlaybackAuthoritySnapshot,
} from "./current-activity";

const NOW = "2026-09-18T04:00:00.000Z";

function fixtureRead(
  id: string,
  fileName: string,
  overrides: Partial<CurrentTransportActivity> = {},
): CurrentTransportActivity {
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

function row(read: CurrentTransportActivity, rate = 7_200_000): LiveReadRow {
  return { read, rate, history: [rate * 0.8, rate, rate * 1.1] };
}

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
    expect(container.textContent).toContain("1 MB/s");
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

  it("falls back to the release folder name for obfuscated transport leaves", () => {
    const read = fixtureRead("read-1", "9f2c7a1e4b.mkv", {
      path: "/completed-symlinks/movies/Interstellar.2014.1080p.BluRay.x264-GRP/9f2c7a1e4b.mkv",
    });
    const markup = renderToStaticMarkup(<LiveReadsPanelContent playback={[]} rows={[row(read)]} />);

    expect(markup).toContain("Interstellar.2014.1080p.BluRay.x264-GRP.mkv");
    expect(markup).not.toContain("9f2c7a1e4b.mkv</span>");
  });

  it("does not lock height until the first snapshot is ready", () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(rectWithHeight(240));

    const { container } = render(
      <LiveReadsPanelContent
        playback={[]}
        rows={[row(fixtureRead("read-1", "Read.mkv"))]}
        snapshotReady={false}
      />,
    );
    expect(container.querySelector("section")?.style.height).toBe("");
  });

  it("locks the first snapshot height when more activity arrives", () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(rectWithHeight(240));
    const first = row(fixtureRead("read-1", "Read.mkv"));
    const second = row(fixtureRead("read-2", "Another.mkv"));

    const { container, rerender } = render(
      <LiveReadsPanelContent playback={[]} rows={[first]} snapshotReady />,
    );
    const section = container.querySelector("section");
    expect(section?.style.height).toBe("240px");
    expect(section?.className).toContain("overflow-hidden");

    rerender(<LiveReadsPanelContent playback={[]} rows={[first, second]} snapshotReady />);
    expect(section?.style.height).toBe("240px");
    expect(container.querySelector("ul")?.className).toContain("overflow-y-auto");
  });
});

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
