import type { ActiveRead } from "~/clients/backend-client.server";

type MockLiveReadRow = {
  read: ActiveRead;
  rate: number;
  history: number[];
};

function fixtureRead(
  id: string,
  fileName: string,
  path: string,
  currentOffset: number,
  fileSize: number | null,
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

/** Local-preview rows for `?mockReads=7` — not served in production. */
export const MOCK_LIVE_READ_ROWS: MockLiveReadRow[] = [
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
  {
    read: fixtureRead(
      "a1b2c3d4-0006-4000-8000-000000000006",
      "Andor.S02E04.2160p.DSNP.WEB-DL.DDP5.1.Atmos.H.265-GRP.mkv",
      "/completed-symlinks/tv/Andor.S02E04.2160p.DSNP.WEB-DL.DDP5.1.Atmos.H.265-GRP.mkv",
      86_000_000,
      2_140_000_000,
      "Jellyfin/10.9.11",
      "192.168.1.88",
      [{ host: "news.eweka.nl", nickname: "Eweka", segments: 8 }],
      { startedMinutesAgo: 2, bytesFetched: 140_000_000 },
    ),
    rate: 12_400_000,
    history: historyAround(12_400_000),
  },
  {
    read: fixtureRead(
      "a1b2c3d4-0007-4000-8000-000000000007",
      "The.Bear.S03E01.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv",
      "/completed-symlinks/tv/The.Bear.S03E01.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv",
      42_000_000,
      null,
      "Emby/4.8",
      "10.0.0.14",
      [
        { host: "news.eweka.nl", nickname: "Eweka", segments: 4 },
        { host: "news.blocknews.net", nickname: "Blocknews", segments: 2 },
      ],
      { startedMinutesAgo: 0, bytesFetched: 48_000_000 },
    ),
    rate: 3_100_000,
    history: historyAround(3_100_000, 8),
  },
];

export function mockReadsRequested(): number | null {
  if (typeof window === "undefined") return null;
  if (!import.meta.env.DEV) return null;
  const raw = new URLSearchParams(window.location.search).get("mockReads");
  if (raw == null) return null;
  if (raw === "") return MOCK_LIVE_READ_ROWS.length;
  const n = Number.parseInt(raw, 10);
  if (!Number.isFinite(n) || n < 1) return MOCK_LIVE_READ_ROWS.length;
  return Math.min(n, MOCK_LIVE_READ_ROWS.length);
}

export function mockLiveReadRows(count: number): MockLiveReadRow[] {
  return MOCK_LIVE_READ_ROWS.slice(0, count);
}
