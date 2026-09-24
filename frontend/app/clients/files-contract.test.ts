import { describe, expect, it } from "vitest";
import {
  defaultFilesFilters,
  parseFilesFilters,
  serializeFilesFilters,
  parseFilesParameters,
} from "./files-contract";
import { filesResourcePageSchema } from "./files-contract";
import { makeFileRow, makeFilesPage } from "../routes/explore/files-fixtures";

describe("Files filters", () => {
  it("acceptsStaticDirectoryGuidsAndExplicitNulls", () => {
    const row = makeFileRow({
      id: "00000000-0000-0000-0000-000000000002",
      key: "00000000-0000-0000-0000-000000000002",
      health: null,
      scanState: null,
      libraryState: null,
    });
    expect(filesResourcePageSchema.parse(makeFilesPage([row])).rows[0]?.health).toBeNull();
  });
  it("rejectsMalformedPagesInsteadOfShowingEmptySuccess", () => {
    const row = makeFileRow();
    expect(() => filesResourcePageSchema.parse(makeFilesPage([row, row]))).toThrow();
    expect(() =>
      filesResourcePageSchema.parse(makeFilesPage([row], { observedAt: "2026-09-23T12:00:00" })),
    ).toThrow();
    expect(() => filesResourcePageSchema.parse({ status: true, rows: [] })).toThrow();
  });
  it("roundTripsEveryFilterWithoutLosingFalsyValues", () => {
    const filters = {
      ...defaultFilesFilters,
      q: "literal %2C",
      minSize: 0,
      maxSize: 200,
      hasNzb: false,
      subType: 201 as const,
      repairAction: 0,
      health: ["degraded" as const],
      category: "a,b",
      indexer: "synthetic",
      addedAfter: 0,
      playedBefore: 200,
    };
    expect(parseFilesFilters(serializeFilesFilters(filters))).toEqual(filters);
  });
  it("rejectsInvalidRangesAndUnknownEnums", () => {
    for (const query of [
      "minSize=2&maxSize=1",
      "health=green",
      "hasNzb=0",
      "offset=1junk",
      "limit=201",
      "q=a&q=b",
      "subType=101",
    ])
      expect(() => parseFilesParameters(new URLSearchParams(query))).toThrow();
  });
});
