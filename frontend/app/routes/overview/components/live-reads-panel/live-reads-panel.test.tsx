import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { ActiveRead } from "~/clients/backend-client.server";
import { LiveReadsPanel, LiveReadsPanelContent, type LiveReadRow } from "./live-reads-panel";

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
  it("renders the empty state when there are no active reads", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanel />);

    expect(markup).toContain("Right now");
    expect(markup).toContain("No files are being read right now.");
  });

  it("renders each active read as a full-width row", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    expect(markup).toContain("5 active");
    expect(markup).toContain("The.Prestige.2006.1080p.BluRay.x264-GRP.mkv");
    expect(markup).toContain("Severance.S02E01.2160p.ATVP.WEB-DL.DDP5.1.H.265-GRP.mkv");
    // Speed, progress, and computed time left
    expect(markup).toContain("7.2 MB/s");
    expect(markup).toContain("3.2 GB");
    expect(markup).toContain("/ 8.4 GB");
    expect(markup).toContain("12m left");
    expect(markup).toContain("6m left");
    // Meta line: client, provider badges, session id
    expect(markup).toContain("Plex");
    expect(markup).toContain("192.168.1.20");
    expect(markup).toContain("Eweka");
    expect(markup).toContain("a1b2c3d4");
  });

  it("labels media rows with MOVIE / EPISODE badges", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    expect(markup.match(/MOVIE/g)).toHaveLength(3);
    expect(markup.match(/EPISODE/g)).toHaveLength(2);
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
  });

  it("notes total bytes served when the player is scrubbing", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    // Dune row: 2.9 GB served vs 1.1 GB current position.
    expect(markup).toContain("2.9 GB served");
    // Linear rows (bytesRead == currentOffset) get no such note.
    expect(markup.match(/served</g)).toHaveLength(1);
  });

  it("falls back to the release folder name for obfuscated file names", () => {
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={fixtureRows} />);

    expect(markup).toContain("Interstellar.2014.1080p.BluRay.x264-GRP.mkv");
    expect(markup).not.toContain("9f2c7a1e4b.mkv</span>");
  });

  it("renders an em dash for time left when the rate stalls", () => {
    const stalled: LiveReadRow[] = [{ ...fixtureRows[0]!, rate: 0 }];
    const markup = renderToStaticMarkup(<LiveReadsPanelContent rows={stalled} />);

    expect(markup).toContain("—");
    expect(markup).not.toContain("left</span>");
  });
});
