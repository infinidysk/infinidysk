import { describe, expect, it } from "vitest";
import { formatDurationMs } from "./format";

describe("formatDurationMs", () => {
  it("renders em dash for missing or invalid values", () => {
    expect(formatDurationMs(null)).toBe("—");
    expect(formatDurationMs(undefined)).toBe("—");
    expect(formatDurationMs(Number.NaN)).toBe("—");
    expect(formatDurationMs(-1)).toBe("—");
  });

  it("formats seconds, minutes, and hours", () => {
    expect(formatDurationMs(7_800)).toBe("7.8s");
    expect(formatDurationMs(21_000)).toBe("21s");
    expect(formatDurationMs(138_000)).toBe("2m 18s");
    expect(formatDurationMs(10_800_000)).toBe("3h");
    expect(formatDurationMs(3_660_000)).toBe("1h 1m");
    expect(formatDurationMs(60_000)).toBe("1m");
  });
});
