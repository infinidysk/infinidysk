import { describe, expect, it } from "vitest";
import type { HealthCheckQueueItem } from "~/clients/backend-client.server";
import {
  completeHealthCheck,
  getVisibleHealthCheckItems,
  mergeHealthCheckQueue,
  parseHealthItemProgressMessage,
  parseHealthItemStatusMessage,
  type HealthQueueState,
  updateHealthCheckProgress,
} from "./health-queue-state";

function queueItem(id: string, nextHealthCheck: string | null): HealthCheckQueueItem {
  return {
    id,
    name: `${id}.mkv`,
    path: `/content/${id}.mkv`,
    releaseDate: null,
    lastHealthCheck: null,
    nextHealthCheck,
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

describe("updateHealthCheckProgress", () => {
  it("updates only the reporting item when checks progress out of order", () => {
    const state: HealthQueueState = {
      items: [queueItem("first", null), queueItem("second", null), queueItem("waiting", null)],
      uncheckedCount: 3,
    };
    const firstUpdate = updateHealthCheckProgress(state, "first", 75);

    expect(updateHealthCheckProgress(firstUpdate, "second", 25)).toEqual({
      items: [
        { ...queueItem("first", null), progress: 75 },
        { ...queueItem("second", null), progress: 25 },
        queueItem("waiting", null),
      ],
      uncheckedCount: 3,
    });
  });

  it("ignores progress for an item that is no longer displayed", () => {
    const state: HealthQueueState = {
      items: [queueItem("current", null)],
      uncheckedCount: 1,
    };

    expect(updateHealthCheckProgress(state, "completed", 100)).toBe(state);
  });
});

describe("mergeHealthCheckQueue", () => {
  it("preserves live progress while applying refreshed queue data", () => {
    const current: HealthQueueState = {
      items: [{ ...queueItem("active", null), progress: 45 }, queueItem("removed", null)],
      uncheckedCount: 2,
    };
    const refreshed: HealthQueueState = {
      items: [queueItem("active", "2026-08-27T12:00:00Z"), queueItem("new", null)],
      uncheckedCount: 7,
    };

    expect(mergeHealthCheckQueue(current, refreshed)).toEqual({
      items: [
        { ...queueItem("active", "2026-08-27T12:00:00Z"), progress: 45 },
        queueItem("new", null),
      ],
      uncheckedCount: 7,
    });
  });
});

describe("health websocket payload parsing", () => {
  it("accepts valid progress and status payloads", () => {
    expect(parseHealthItemProgressMessage("item-id|42")).toEqual({
      davItemId: "item-id",
      progress: 42,
    });
    expect(parseHealthItemStatusMessage("item-id|1|2")).toEqual({
      davItemId: "item-id",
      healthResult: 1,
      repairAction: 2,
    });
    expect(parseHealthItemProgressMessage(" item-id |42")).toEqual({
      davItemId: "item-id",
      progress: 42,
    });
    expect(parseHealthItemStatusMessage(" item-id |1|2")).toEqual({
      davItemId: "item-id",
      healthResult: 1,
      repairAction: 2,
    });
  });

  it("ignores malformed payloads", () => {
    expect(parseHealthItemProgressMessage("missing-progress")).toBeNull();
    expect(parseHealthItemProgressMessage("item-id|")).toBeNull();
    expect(parseHealthItemProgressMessage("item-id| ")).toBeNull();
    expect(parseHealthItemProgressMessage("item-id|NaN")).toBeNull();
    expect(parseHealthItemProgressMessage("item-id|101")).toBeNull();
    expect(parseHealthItemProgressMessage("item-id|done")).toBeNull();
    expect(parseHealthItemProgressMessage(" |42")).toBeNull();
    expect(parseHealthItemStatusMessage("item-id||2")).toBeNull();
    expect(parseHealthItemStatusMessage("item-id|1| ")).toBeNull();
    expect(parseHealthItemStatusMessage("item-id|not-a-result|2")).toBeNull();
    expect(parseHealthItemStatusMessage("item-id|3|2")).toBeNull();
    expect(parseHealthItemStatusMessage("item-id|1|5")).toBeNull();
    expect(parseHealthItemStatusMessage("|1|2")).toBeNull();
    expect(parseHealthItemStatusMessage(" |1|2")).toBeNull();
  });
});

describe("getVisibleHealthCheckItems", () => {
  it("surfaces progressing checks while retaining API rows without progress", () => {
    const items = Array.from({ length: 12 }, (_, index) => queueItem(`item-${index}`, null));
    items[11] = { ...items[11]!, progress: 35 };

    const visible = getVisibleHealthCheckItems(items);

    expect(visible).toHaveLength(10);
    expect(visible[0]!.id).toBe("item-11");
    expect(visible.map((item) => item.id)).not.toContain("item-9");
  });
});
