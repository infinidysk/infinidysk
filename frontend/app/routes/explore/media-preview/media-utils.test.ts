import { describe, expect, it } from "vitest";
import {
    appendQueryParam,
    backoffMs,
    bufferedAhead,
    buildMediaSrc,
    classifyMediaError,
    formatClock,
    formatTimeRanges,
    networkStateLabel,
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
});

describe("buildMediaSrc", () => {
    it("appends playerSession to an already-signed url", () => {
        expect(buildMediaSrc("/view/a.mkv?downloadKey=abc", "session-1"))
            .toBe("/view/a.mkv?downloadKey=abc&playerSession=session-1");
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
        expect(appendQueryParam("/view/a.mkv?downloadKey=k", "download", "true"))
            .toBe("/view/a.mkv?downloadKey=k&download=true");
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
        start: i => pairs[i]![0],
        end: i => pairs[i]![1],
    };
}

describe("formatTimeRanges", () => {
    it("formats each range and handles empty", () => {
        expect(formatTimeRanges(ranges([[0, 30], [60, 90]]))).toBe("0:00–0:30, 1:00–1:30");
        expect(formatTimeRanges(ranges([]))).toBe("—");
    });
});

describe("bufferedAhead", () => {
    it("returns seconds to the end of the containing range", () => {
        expect(bufferedAhead(ranges([[0, 100]]), 40)).toBe(60);
        expect(bufferedAhead(ranges([[0, 10], [20, 100]]), 50)).toBe(50);
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
