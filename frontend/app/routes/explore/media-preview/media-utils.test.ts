import { describe, expect, it, vi } from "vitest";
import {
  appendQueryParam,
  backoffMs,
  bufferedAhead,
  buildMediaSrc,
  classifyMediaError,
  formatClock,
  formatTimeRanges,
  networkStateLabel,
  probeSource,
  readyStateLabel,
  type TimeRangesLike,
} from "./media-utils";

describe("backoffMs", () => {
  it("doubles per attempt and stays bounded", () => {
    expect(backoffMs(0)).toBe(1000);
    expect(backoffMs(1)).toBe(2000);
    expect(backoffMs(2)).toBe(4000);
    expect(backoffMs(10)).toBe(16_000);
  });
});

describe("classifyMediaError", () => {
  it("ignores self-inflicted aborts", () => {
    expect(classifyMediaError(1)).toBe("aborted");
  });

  it("retries network and unknown failures", () => {
    expect(classifyMediaError(2)).toBe("retry");
    expect(classifyMediaError(null)).toBe("retry");
  });

  it("does not retry decode or unsupported-source failures", () => {
    expect(classifyMediaError(3)).toBe("unsupported");
    expect(classifyMediaError(4)).toBe("unsupported");
  });

  it("retries a decode failure that arrives after playback progressed", () => {
    expect(classifyMediaError(3, true)).toBe("retry");
    // The demuxer never accepted the source, so progress cannot rescue it.
    expect(classifyMediaError(4, true)).toBe("unsupported");
  });
});

describe("probeSource", () => {
  const headerMap = (entries: Record<string, string>) =>
    ({ get: (name: string) => entries[name.toLowerCase()] ?? null }) as Headers;

  it("asks for a single byte and reports an OK response as served", async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue({ ok: true } as Response);
    await expect(probeSource("/view/a.mp4?downloadKey=k", fetchFn)).resolves.toEqual({
      kind: "served",
    });
    const [url, init] = fetchFn.mock.calls[0]!;
    expect(url).toBe("/view/a.mp4?downloadKey=k");
    expect(init?.headers).toEqual({ Range: "bytes=0-0" });
  });

  it("recognizes the backend's typed missing-payload 404", async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue({
      ok: false,
      status: 404,
      headers: headerMap({ "x-infinidysk-stream-error": "missing-file-payload" }),
    } as Response);
    await expect(probeSource("/view/a.mp4", fetchFn)).resolves.toEqual({ kind: "missing-payload" });
  });

  it("treats other 4xx as refused, preserving the status", async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue({
      ok: false,
      status: 403,
      headers: headerMap({}),
    } as Response);
    await expect(probeSource("/view/a.mp4", fetchFn)).resolves.toEqual({
      kind: "denied",
      status: 403,
    });
  });

  it("treats a plain 404 without the marker header as refused", async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue({
      ok: false,
      status: 404,
      headers: headerMap({}),
    } as Response);
    await expect(probeSource("/view/a.mp4", fetchFn)).resolves.toEqual({
      kind: "denied",
      status: 404,
    });
  });

  it("treats 5xx as a server error worth retrying", async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue({
      ok: false,
      status: 500,
      headers: headerMap({}),
    } as Response);
    await expect(probeSource("/view/a.mp4", fetchFn)).resolves.toEqual({
      kind: "server-error",
      status: 500,
    });
  });

  it("treats thrown fetches (network down, timeout) as server errors", async () => {
    const fetchFn = vi.fn<typeof fetch>().mockRejectedValue(new TypeError("network down"));
    await expect(probeSource("/view/a.mp4", fetchFn)).resolves.toEqual({
      kind: "server-error",
      status: null,
    });
  });
});

describe("buildMediaSrc", () => {
  it("appends playerSession to an already-signed url", () => {
    expect(buildMediaSrc("/view/a.mkv?downloadKey=abc", "session-1")).toBe(
      "/view/a.mkv?downloadKey=abc&playerSession=session-1",
    );
  });

  it("encodes the session value", () => {
    expect(buildMediaSrc("/view/a.mkv?downloadKey=abc", "a b")).toContain("playerSession=a%20b");
  });
});

describe("appendQueryParam", () => {
  it("uses ? when the url has no query string", () => {
    expect(appendQueryParam("/view/a.mkv", "download", "true")).toBe("/view/a.mkv?download=true");
  });

  it("uses & when the url already has a query string", () => {
    expect(appendQueryParam("/view/a.mkv?downloadKey=k", "download", "true")).toBe(
      "/view/a.mkv?downloadKey=k&download=true",
    );
  });
});

describe("formatClock", () => {
  it("formats h:mm:ss / m:ss", () => {
    expect(formatClock(0)).toBe("0:00");
    expect(formatClock(65)).toBe("1:05");
    expect(formatClock(3661)).toBe("1:01:01");
    expect(formatClock(Number.NaN)).toBe("—");
    expect(formatClock(-5)).toBe("—");
  });
});

function ranges(pairs: [number, number][]): TimeRangesLike {
  return {
    length: pairs.length,
    start: (i) => pairs[i]![0],
    end: (i) => pairs[i]![1],
  };
}

describe("formatTimeRanges", () => {
  it("formats each range and handles empty", () => {
    expect(
      formatTimeRanges(
        ranges([
          [0, 30],
          [60, 90],
        ]),
      ),
    ).toBe("0:00–0:30, 1:00–1:30");
    expect(formatTimeRanges(ranges([]))).toBe("—");
  });
});

describe("bufferedAhead", () => {
  it("returns seconds to the end of the containing range", () => {
    expect(bufferedAhead(ranges([[0, 100]]), 40)).toBe(60);
    expect(
      bufferedAhead(
        ranges([
          [0, 10],
          [20, 100],
        ]),
        50,
      ),
    ).toBe(50);
  });

  it("returns 0 when the playhead is outside all ranges", () => {
    expect(bufferedAhead(ranges([[50, 100]]), 10)).toBe(0);
    expect(bufferedAhead(ranges([]), 10)).toBe(0);
  });
});

describe("state labels", () => {
  it("labels known states and falls back for unknown", () => {
    expect(readyStateLabel(4)).toBe("HAVE_ENOUGH_DATA");
    expect(networkStateLabel(2)).toBe("NETWORK_LOADING");
    expect(readyStateLabel(99)).toBe("unknown (99)");
  });
});
