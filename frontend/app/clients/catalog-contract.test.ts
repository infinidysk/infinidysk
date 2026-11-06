import { describe, expect, it } from "vitest";
import {
  catalogHistoryItemSchema,
  catalogHistoryResponseSchema,
  catalogRowSchema,
} from "./catalog-contract";

const summary = {
  type: "movie",
  tmdbId: 10331,
  title: "Night of the Living Dead",
  year: 1968,
  overview: null,
  rating: 7.6,
  posterUrl: null,
  backdropUrl: null,
};

describe("catalog row contract", () => {
  it("accepts a title row and a people row", () => {
    expect(
      catalogRowSchema.safeParse({
        id: "trending",
        title: "Trending Now",
        items: [summary],
        people: null,
      }).success,
    ).toBe(true);
    expect(
      catalogRowSchema.safeParse({
        id: "trending-people",
        title: "Trending People",
        items: null,
        people: [{ personId: "525", name: "Christopher Nolan", profileUrl: null }],
      }).success,
    ).toBe(true);
  });

  it("rejects a row that carries neither items nor people", () => {
    const parsed = catalogRowSchema.safeParse({
      id: "continue-watching",
      title: "Continue Watching",
      items: null,
      people: null,
    });

    expect(parsed.success).toBe(false);
  });
});

describe("watch history contract", () => {
  const item = {
    id: 7,
    type: "series",
    tmdbId: 1930,
    season: 1,
    episode: 2,
    title: "The Beverly Hillbillies",
    subtitle: "S1E2 · Getting Settled",
    posterUrl: null,
    positionSeconds: 300,
    durationSeconds: 1500,
    updatedAtEpoch: 1_790_000_000,
    finished: false,
  };

  it("parses a history item", () => {
    expect(catalogHistoryItemSchema.safeParse(item).success).toBe(true);
    expect(catalogHistoryResponseSchema.safeParse({ status: true, items: [item] }).success).toBe(
      true,
    );
  });

  it("rejects a negative position", () => {
    expect(catalogHistoryItemSchema.safeParse({ ...item, positionSeconds: -1 }).success).toBe(
      false,
    );
  });
});
