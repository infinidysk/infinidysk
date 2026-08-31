import { describe, expect, it, vi } from "vitest";
import {
  buildHistoryRetryUrl,
  canRetryHistorySlot,
  retryHistoryItem,
  retryHistoryItems,
  shouldAcceptRetryClick,
} from "./history-retry";

describe("canRetryHistorySlot", () => {
  it("allows retry only for failed rows with an NZB blob", () => {
    expect(canRetryHistorySlot({ status: "Failed", nzb_blob_id: "abc" })).toBe(true);
    expect(canRetryHistorySlot({ status: "Failed", nzb_blob_id: null })).toBe(false);
    expect(canRetryHistorySlot({ status: "Failed" })).toBe(false);
    expect(canRetryHistorySlot({ status: "Completed", nzb_blob_id: "abc" })).toBe(false);
  });
});

describe("shouldAcceptRetryClick", () => {
  it("rejects duplicate clicks while retrying or removing", () => {
    expect(shouldAcceptRetryClick(false, false)).toBe(true);
    expect(shouldAcceptRetryClick(true, false)).toBe(false);
    expect(shouldAcceptRetryClick(false, true)).toBe(false);
  });
});

describe("buildHistoryRetryUrl", () => {
  it("builds the SAB-compatible retry URL", () => {
    expect(buildHistoryRetryUrl("nzo-123")).toBe("/api?mode=retry&value=nzo-123");
    expect(buildHistoryRetryUrl("a b")).toBe("/api?mode=retry&value=a%20b");
  });
});

describe("retryHistoryItem", () => {
  it("posts to the retry endpoint and returns success", async () => {
    const fetchImpl = vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({ status: true, nzo_id: "new-id" }),
    });

    const result = await retryHistoryItem("old-id", fetchImpl as typeof fetch);

    expect(fetchImpl).toHaveBeenCalledWith("/api?mode=retry&value=old-id", { method: "POST" });
    expect(result).toEqual({ ok: true, nzoId: "new-id" });
  });

  it("returns the API error when retry fails", async () => {
    const fetchImpl = vi.fn().mockResolvedValue({
      ok: false,
      json: () => Promise.resolve({ status: false, error: "The NZB file could not be found." }),
    });

    const result = await retryHistoryItem("old-id", fetchImpl as typeof fetch);

    expect(result).toEqual({
      ok: false,
      error: "The NZB file could not be found.",
    });
  });

  it("returns a fallback error when the request throws", async () => {
    const fetchImpl = vi.fn().mockRejectedValue(new Error("network"));

    const result = await retryHistoryItem("old-id", fetchImpl as typeof fetch);

    expect(result).toEqual({ ok: false, error: "Failed to retry history item." });
  });
});

describe("retryHistoryItems", () => {
  it("returns succeeded and failed ids when the bulk retry is a partial success", async () => {
    const fetchImpl = vi.fn().mockResolvedValue({
      ok: true,
      json: () =>
        Promise.resolve({
          status: true,
          nzo_ids: ["ok-1"],
          failed: [{ nzo_id: "bad-1", error: "The NZB file could not be found." }],
        }),
    });

    const result = await retryHistoryItems(["ok-1", "bad-1"], fetchImpl as typeof fetch);

    expect(fetchImpl).toHaveBeenCalledWith("/api?mode=retry", {
      method: "POST",
      headers: { "Content-Type": "application/json;charset=UTF-8" },
      body: JSON.stringify({ nzo_ids: ["ok-1", "bad-1"] }),
    });
    expect(result).toEqual({
      ok: true,
      succeeded: ["ok-1"],
      failed: [{ nzoId: "bad-1", error: "The NZB file could not be found." }],
    });
  });
});
