import { describe, expect, it } from "vitest";
import {
  combinedListWindow,
  statusAppliesToHistory,
  statusAppliesToQueue,
} from "./combined-window";

describe("combinedListWindow", () => {
  it("fills page 1 with queue items first, then history", () => {
    expect(combinedListWindow(5, 1, 100)).toEqual({
      queueStart: 0,
      queueLimit: 5,
      historyStart: 0,
      historyLimit: 95,
    });
  });

  it("keeps a large queue on page 1 without history", () => {
    expect(combinedListWindow(150, 1, 100)).toEqual({
      queueStart: 0,
      queueLimit: 100,
      historyStart: 0,
      historyLimit: 0,
    });
  });

  it("spills leftover queue onto page 2 then fills with history", () => {
    expect(combinedListWindow(150, 2, 100)).toEqual({
      queueStart: 100,
      queueLimit: 50,
      historyStart: 0,
      historyLimit: 50,
    });
  });

  it("pages through history after the queue is exhausted", () => {
    expect(combinedListWindow(150, 3, 100)).toEqual({
      queueStart: 200,
      queueLimit: 0,
      historyStart: 50,
      historyLimit: 100,
    });
  });

  it("treats an empty queue as history-only pagination", () => {
    expect(combinedListWindow(0, 2, 100)).toEqual({
      queueStart: 100,
      queueLimit: 0,
      historyStart: 100,
      historyLimit: 100,
    });
  });
});

describe("statusAppliesToQueue / statusAppliesToHistory", () => {
  it("includes both sides when no status filter is set", () => {
    expect(statusAppliesToQueue("")).toBe(true);
    expect(statusAppliesToHistory("")).toBe(true);
  });

  it("scopes active statuses to the queue", () => {
    expect(statusAppliesToQueue("Downloading")).toBe(true);
    expect(statusAppliesToHistory("Downloading")).toBe(false);
  });

  it("scopes finished statuses to history", () => {
    expect(statusAppliesToQueue("Completed")).toBe(false);
    expect(statusAppliesToHistory("Failed")).toBe(true);
  });
});
