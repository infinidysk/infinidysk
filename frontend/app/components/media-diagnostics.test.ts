import { describe, expect, it } from "vitest";
import { buildDiagnosticsSnapshot, type MediaDiagnosticsProps } from "./media-diagnostics";
import type { ActiveRead } from "~/clients/backend-client.server";
import type { MediaPlayer } from "./use-media-player";

function fakePlayer(): MediaPlayer {
  return {
    status: "playing",
    buffering: false,
    attempts: 1,
    maxAttempts: 3,
    error: null,
    startupMs: 420,
    events: [{ at: 1_000, kind: "play" }],
    lastGoodTimeRef: { current: 12.5 },
    lastProgressAtRef: { current: 2_000 },
  } as unknown as MediaPlayer;
}

function fakeProps(): MediaDiagnosticsProps {
  return {
    player: fakePlayer(),
    getMedia: () => null,
    playerSession: "player-session-1",
    fileName: "movie.mkv",
    filePath: "content/movies/movie.mkv",
    mimeType: "video/x-matroska",
    sizeBytes: 1_000_000,
  };
}

const fakeRead: ActiveRead = {
  id: "read-session-id",
  fileName: "movie.mkv",
  path: "content/movies/movie.mkv",
  startedAt: 1_000,
  lastActivityAt: 2_000,
  bytesRead: 100,
  bytesFetched: 200,
  currentOffset: 100,
  fileSize: 1_000_000,
  clientIp: "203.0.113.10",
  clientUserAgent: "Mozilla/5.0",
  playerSession: "player-session-1",
  providers: [{ host: "news.example.com", nickname: "Main", segments: 12 }],
};

describe("buildDiagnosticsSnapshot", () => {
  it("never includes the download key, client IP, or raw segment ids", () => {
    const snapshot = buildDiagnosticsSnapshot(fakeProps(), fakeRead, null, null, null, [
      {
        seq: 7,
        at: 1_000,
        kind: "Segment",
        provider: "abc",
        status: "Ok",
        segmentId: "full-message-id@usenet.example",
      },
    ]);
    const json = JSON.stringify(snapshot);

    expect(json).not.toContain("downloadKey");
    expect(json).not.toContain("203.0.113.10");
    expect(json).not.toContain("full-message-id@usenet.example");
    expect(json).toContain("full-message"); // truncated prefix retained
    expect(snapshot.backendRead?.sessionId).toBe("read-session-id");
    expect(snapshot.backendRead?.bytesFetched).toBe(200);
    expect(snapshot.playerSession).toBe("player-session-1");
  });

  it("handles missing backend read and trace data", () => {
    const snapshot = buildDiagnosticsSnapshot(fakeProps(), null, null, null, null, null);
    expect(snapshot.backendRead).toBeNull();
    expect(snapshot.traceSummary).toBeNull();
    expect(snapshot.recentTraceEvents).toBeNull();
  });
});
