// @vitest-environment jsdom
import { cleanup, fireEvent, render } from "@testing-library/react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ActiveRead } from "~/clients/backend-client.server";
import { LiveReadsPanel, LiveReadsPanelContent, type LiveReadRow } from "./live-reads-panel";
import { LiveTiles } from "../live-tiles/live-tiles";

function fixtureRead(
  id: string,
  fileName: string,
  path: string,
  currentOffset: number,
  fileSize: number,
  clientUserAgent: string,
  clientIp: string,
  providers: ActiveRead["providers"],
  overrides?: { startedMinutesAgo?: number; bytesRead?: number; bytesFetched?: number },
): ActiveRead {
  const now = Date.now();
  return {
    id,
    fileName,
    path,
    startedAt: now - (overrides?.startedMinutesAgo ?? 20) * 60_000,
    lastActivityAt: now,
    bytesRead: overrides?.bytesRead ?? currentOffset,
    bytesFetched: overrides?.bytesFetched ?? 0,
    currentOffset,
    fileSize,
    clientUserAgent,
    clientIp,
    providers,
  };
}

function historyAround(rate: number, samples = 45): number[] {
  return Array.from({ length: samples }, (_, i) =>
    Math.max(0, rate * (0.72 + 0.55 * Math.abs(Math.sin(i / 4)))),
  );
}

const fixtureRows: LiveReadRow[] = [
  {
    read: fixtureRead(
      "a1b2c3d4-0001-4000-8000-000000000001",
      "The.Prestige.2006.1080p.BluRay.x264-GRP.mkv",
      "/completed-symlinks/movies/The.Prestige.2006.1080p.BluRay.x264-GRP.mkv",
      3_200_000_000,
      8_400_000_000,
      "Plex/1.107.0",
      "192.168.1.20",
      [
        { host: "news.eweka.nl", nickname: "Eweka", segments: 41 },
        { host: "news.newshosting.com", nickname: "Newshosting", segments: 18 },
      ],
      { startedMinutesAgo: 84, bytesFetched: 3_800_000_000 },
    ),
    rate: 7_200_000,
    history: historyAround(7_200_000),
  },
  {
    read: fixtureRead(
      "a1b2c3d4-0002-4000-8000-000000000002",
      "The.Last.of.Us.S01E03.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv",
      "/completed-symlinks/tv/The.Last.of.Us.S01E03.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv",
      412_000_000,
      1_100_000_000,
      "Infuse/8.0",
      "192.168.1.34",
      [{ host: "news.eweka.nl", nickname: "Eweka", segments: 22 }],
      { startedMinutesAgo: 20, bytesFetched: 600_000_000 },
    ),
    rate: 4_100_000,
    history: historyAround(4_100_000),
  },
  {
    read: fixtureRead(
      "a1b2c3d4-0003-4000-8000-000000000003",
      "Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.H.265-GRP.mkv",
      "/completed-symlinks/movies/Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.H.265-GRP.mkv",
      1_100_000_000,
      3_800_000_000,
      "VLC/3.0.20",
      "192.168.1.51",
      [
        { host: "news.newshosting.com", nickname: "Newshosting", segments: 17 },
        { host: "news.usenetserver.com", nickname: "UsenetServer", segments: 9 },
      ],
      { startedMinutesAgo: 33, bytesRead: 2_900_000_000, bytesFetched: 2_950_000_000 },
    ),
    rate: 2_800_000,
    history: historyAround(2_800_000),
  },
  {
    read: fixtureRead(
      "a1b2c3d4-0004-4000-8000-000000000004",
      "Severance.S02E01.2160p.ATVP.WEB-DL.DDP5.1.H.265-GRP.mkv",
      "/completed-symlinks/tv/Severance.S02E01.2160p.ATVP.WEB-DL.DDP5.1.H.265-GRP.mkv",
      218_000_000,
      890_000_000,
      "rclone/1.68",
      "192.168.1.10",
      [{ host: "news.usenetserver.com", nickname: "UsenetServer", segments: 13 }],
      { startedMinutesAgo: 5, bytesFetched: 300_000_000 },
    ),
    rate: 1_900_000,
    history: historyAround(1_900_000),
  },
  {
    read: fixtureRead(
      "a1b2c3d4-0005-4000-8000-000000000005",
      "9f2c7a1e4b.mkv",
      "/completed-symlinks/movies/Interstellar.2014.1080p.BluRay.x264-GRP/9f2c7a1e4b.mkv",
      900_000_000,
      5_200_000_000,
      "Kodi/21.0",
      "192.168.1.64",
      [{ host: "news.eweka.nl", nickname: "Eweka", segments: 30 }],
      { startedMinutesAgo: 12, bytesFetched: 1_200_000_000 },
    ),
    rate: 5_400_000,
    history: historyAround(5_400_000),
  },
];

describe("LiveReadsPanel", () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("renders the empty state when there are no active reads", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanel />);

    expect(markup).toContain("Right now");
    expect(markup).toContain("No files are being read right now.");
  });

  it("renders each active read as a full-width row", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    expect(markup).toContain("5 active");
    expect(markup).not.toContain("h-[21rem]");
    expect(markup).not.toContain("sm:h-[30rem]");
    expect(markup).toContain("overflow-x-hidden");
    expect(markup).toContain("pr-4");
    expect(markup).toContain("The.Prestige.2006.1080p.BluRay.x264-GRP.mkv");
    expect(markup).toContain("Severance.S02E01.2160p.ATVP.WEB-DL.DDP5.1.H.265-GRP.mkv");
    // Speed, progress, and computed time left
    expect(markup).toContain("7.2 MB/s");
    expect(markup).toContain("text-secondary");
    expect(markup).toContain("3.2 GB");
    expect(markup).toContain("/ 8.4 GB");
    expect(markup).toContain("12m left");
    expect(markup).toContain("6m left");
    // Meta line: client and provider badges
    expect(markup).toContain("Plex");
    expect(markup).toContain("192.168.1.20");
    expect(markup).toContain("hidden font-mono text-base-content/40 sm:inline");
    expect(markup).toContain("Eweka");
    expect(markup).not.toContain("Copy session id");
    expect(markup).not.toContain("a1b2c3d4");
  });

  it("groups live totals and read rows in one card and updates the totals", () => {
    const tiles = {
      activeReads: 5,
      articlesPerMinute: 120,
      errorsPerMinute: 0,
      bytesServedPerMinute: 60_000_000,
    };
    const { container, getByRole, rerender } = render(
      <LiveReadsPanelContent rows={fixtureRows} summary={<LiveTiles tiles={tiles} />} />,
    );

    expect(container.querySelectorAll("section.card")).toHaveLength(1);
    expect(getByRole("region", { name: "Live status" }).closest("section")).toBe(
      getByRole("list").closest("section"),
    );
    expect(container.textContent).toContain("1 MB/s");
    expect(container.textContent).not.toContain("5 active");

    rerender(
      <LiveReadsPanelContent
        rows={[]}
        summary={<LiveTiles tiles={{ ...tiles, activeReads: 0, bytesServedPerMinute: 0 }} />}
      />,
    );
    expect(container.textContent).toContain("0 B/s");
    expect(container.textContent).toContain("No files are being read right now.");
  });

  it("places the newest read first", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    expect(markup.indexOf("Severance.S02E01.2160p.ATVP.WEB-DL.DDP5.1.H.265-GRP.mkv")).toBeLessThan(
      markup.indexOf("The.Prestige.2006.1080p.BluRay.x264-GRP.mkv"),
    );
  });

  it("does not label rows as MOVIE or EPISODE", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    expect(markup).not.toContain("MOVIE");
    expect(markup).not.toContain("EPISODE");
    expect(markup).toContain("font-bold");
    expect(markup).toContain("w-20 shrink-0");
    expect(markup).toContain("lg:w-28");
  });

  it("renders a speed sparkline per row", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    expect(markup.match(/<svg/g)?.length).toBeGreaterThanOrEqual(5);
  });

  it("shows session age and Usenet-fetched bytes in the meta line", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    expect(markup).toContain("1h 24m in");
    expect(markup).toContain("5m in");
    expect(markup).toContain("fetched 3.8 GB");
    expect(markup).toContain("max-sm:hidden");
  });

  it("notes total bytes served when the player is scrubbing", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    // Dune row: 2.9 GB served vs 1.1 GB current position.
    expect(markup).toContain("2.9 GB served");
    // Linear rows (bytesRead == currentOffset) get no such note.
    expect(markup.match(/served</g)).toHaveLength(1);
  });

  it("prefixes the parent folder while preserving the actual filename", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

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
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={rows} />);
    expect(markup).toContain(`${expected}</span>`);
  });

  it("renders an em dash for time left when the rate stalls", () => {
    const stalled: LiveReadRow[] = [{ ...fixtureRows[0]!, rate: 0 }];
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={stalled} />);

    expect(markup).toContain("—");
    expect(markup).not.toContain("left</span>");
  });

  it("follows the live read count instead of freezing the first snapshot height", () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(rectWithHeight(240));

    const { container, rerender } = render(
      <LiveReadsPanelContent rows={fixtureRows.slice(0, 4)} />,
    );
    const section = container.querySelector("section");
    expect(section?.style.height).toBe("");
    expect(container.querySelectorAll("li")).toHaveLength(4);

    rerender(<LiveReadsPanelContent rows={fixtureRows.slice(0, 3)} />);
    expect(section?.style.height).toBe("");
    expect(container.querySelectorAll("li")).toHaveLength(3);

    rerender(<LiveReadsPanelContent rows={fixtureRows} />);
    expect(section?.style.height).toBe("");
    expect(container.querySelectorAll("li")).toHaveLength(5);
  });

  it("does not keep the empty-state height once reads start", () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(rectWithHeight(120));

    const { container, rerender } = render(<LiveReadsPanelContent rows={[]} />);
    const section = container.querySelector("section");
    expect(section?.style.height).toBe("");

    rerender(<LiveReadsPanelContent rows={fixtureRows.slice(0, 2)} />);
    expect(section?.style.height).toBe("");
    expect(container.querySelectorAll("li")).toHaveLength(2);

    rerender(<LiveReadsPanelContent rows={[]} />);
    expect(section?.style.height).toBe("");
    expect(container.textContent).toContain("No files are being read right now.");
  });

  it("caps the read list height so extra sessions scroll inside the card", () => {
    const { container } = render(<LiveReadsPanelContent rows={fixtureRows} />);

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
        <LiveReadsPanelContent rows={rows} summary={<span>{total}</span>} />
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
    expect(container.textContent).toContain("No files are being read right now.");

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
      <LiveReadsPanelContent rows={rows} paused={paused} />
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
