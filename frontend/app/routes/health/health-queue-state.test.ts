import { describe, expect, it } from "vitest";
import type { HealthCheckQueueItem } from "~/clients/backend-client.server";
import { completeHealthCheck, type HealthQueueState } from "./health-queue-state";

function queueItem(id: string, nextHealthCheck: string | null): HealthCheckQueueItem {
  return {
    id,
    name: `${id}.mkv`,
    path: `/content/${id}.mkv`,
    releaseDate: null,
    lastHealthCheck: null,
    nextHealthCheck,
    progress: 0,
  };
}

describe("completeHealthCheck", () => {
  it("decrements the pending count for a never-checked item", () => {
    const state: HealthQueueState = {
      items: [queueItem("initial", null)],
      uncheckedCount: 10,
    };

    expect(completeHealthCheck(state, "initial")).toEqual({
      items: [],
      uncheckedCount: 9,
    });
  });

  it("does not decrement the pending count for a recheck", () => {
    const state: HealthQueueState = {
      items: [queueItem("recheck", "2026-07-31T12:00:00Z")],
      uncheckedCount: 10,
    };

    expect(completeHealthCheck(state, "recheck")).toEqual({
      items: [],
      uncheckedCount: 10,
    });
  });

  it("ignores duplicate or unknown completion events", () => {
    const state: HealthQueueState = {
      items: [queueItem("other", null)],
      uncheckedCount: 10,
    };

    expect(completeHealthCheck(state, "missing")).toBe(state);
  });

  it("never decrements the pending count below zero", () => {
    const state: HealthQueueState = {
      items: [queueItem("initial", null)],
      uncheckedCount: 0,
    };

    expect(completeHealthCheck(state, "initial").uncheckedCount).toBe(0);
  });
});
