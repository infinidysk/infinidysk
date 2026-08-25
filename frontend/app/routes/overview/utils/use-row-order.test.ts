import { describe, expect, it } from "vitest";
import { mergeRowOrder } from "./use-row-order";

const DEFAULT = ["liveTiles", "rightNow", "throughput", "providers"] as const;

describe("mergeRowOrder", () => {
  it("returns the default order when there is no saved layout", () => {
    expect(mergeRowOrder(DEFAULT, null)).toEqual([...DEFAULT]);
  });

  it("keeps the saved relative order", () => {
    expect(mergeRowOrder(DEFAULT, ["providers", "liveTiles", "rightNow", "throughput"])).toEqual([
      "providers",
      "liveTiles",
      "rightNow",
      "throughput",
    ]);
  });

  it("drops saved ids that no longer exist", () => {
    expect(
      mergeRowOrder(DEFAULT, ["providers", "retired", "liveTiles", "rightNow", "throughput"]),
    ).toEqual(["providers", "liveTiles", "rightNow", "throughput"]);
  });

  it("inserts a new row at its default position instead of appending it", () => {
    // A saved layout from before the rightNow row existed still places it
    // directly under liveTiles.
    expect(mergeRowOrder(DEFAULT, ["providers", "liveTiles", "throughput"])).toEqual([
      "providers",
      "liveTiles",
      "rightNow",
      "throughput",
    ]);
  });

  it("appends a new row when none of its default-successors are saved", () => {
    expect(mergeRowOrder(DEFAULT, ["liveTiles"])).toEqual([...DEFAULT]);
  });

  it("dedupes repeated saved ids, keeping the first occurrence", () => {
    expect(
      mergeRowOrder(DEFAULT, ["providers", "liveTiles", "providers", "rightNow", "throughput"]),
    ).toEqual(["providers", "liveTiles", "rightNow", "throughput"]);
  });

  it("ignores non-array or malformed saved values", () => {
    expect(mergeRowOrder(DEFAULT, "nope")).toEqual([...DEFAULT]);
    expect(
      mergeRowOrder(DEFAULT, [1, {}, "liveTiles", "rightNow", "throughput", "providers"]),
    ).toEqual([...DEFAULT]);
  });
});
