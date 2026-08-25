import { describe, expect, it } from "vitest";
import { formatDurationMs, formatSessionAge, formatTimeLeft } from "./format";

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

describe("formatTimeLeft", () => {
  it("returns null when the estimate is meaningless", () => {
    expect(formatTimeLeft(1_000, 0)).toBeNull();
    expect(formatTimeLeft(1_000, -5)).toBeNull();
    expect(formatTimeLeft(1_000, Number.NaN)).toBeNull();
    expect(formatTimeLeft(-1, 100)).toBeNull();
  });

  it("formats sub-minute, minute, and hour estimates", () => {
    expect(formatTimeLeft(59 * 100, 100)).toBe("<1m left");
    expect(formatTimeLeft(5_200_000_000, 7_200_000)).toBe("12m left");
    expect(formatTimeLeft(672_000_000, 1_900_000)).toBe("6m left");
    expect(formatTimeLeft(84 * 60 * 1_000, 1_000)).toBe("1h 24m left");
    expect(formatTimeLeft(2 * 3_600 * 1_000, 1_000)).toBe("2h 0m left");
  });
});

describe("formatSessionAge", () => {
  const now = 1_800_000_000_000;

  it("renders empty for missing or invalid starts", () => {
    expect(formatSessionAge(null, now)).toBe("");
    expect(formatSessionAge(undefined, now)).toBe("");
    expect(formatSessionAge(Number.NaN, now)).toBe("");
  });

  it("formats fresh, minute, and hour ages", () => {
    expect(formatSessionAge(now - 30_000, now)).toBe("just started");
    expect(formatSessionAge(now - 20 * 60_000, now)).toBe("20m in");
    expect(formatSessionAge(now - 65 * 60_000, now)).toBe("1h 5m in");
  });
});
