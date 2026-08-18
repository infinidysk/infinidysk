import { describe, expect, it } from "vitest";
import { adjustTotalCount, applyQueueProvidersMessage, parseQueueProvidersPayload } from "./events-controller";

describe("adjustTotalCount", () => {
    it("increments on add", () => {
        expect(adjustTotalCount(0, 1)).toBe(1);
        expect(adjustTotalCount(5, 1)).toBe(6);
    });

    it("increments even when the visible page is already full", () => {
        // Totals track the full queue, not the pageSize window.
        const pageSize = 25;
        expect(adjustTotalCount(pageSize, 1)).toBe(pageSize + 1);
        expect(adjustTotalCount(pageSize * 4, 1)).toBe(pageSize * 4 + 1);
    });

    it("decrements on remove by id count", () => {
        expect(adjustTotalCount(3, -1)).toBe(2);
        expect(adjustTotalCount(10, -3)).toBe(7);
    });

    it("clamps at zero on over-remove", () => {
        expect(adjustTotalCount(0, -1)).toBe(0);
        expect(adjustTotalCount(2, -5)).toBe(0);
    });

    it("supports history-style add then remove", () => {
        let total = 0;
        total = adjustTotalCount(total, 1);
        total = adjustTotalCount(total, 1);
        expect(total).toBe(2);
        total = adjustTotalCount(total, -1);
        expect(total).toBe(1);
        total = adjustTotalCount(total, -1);
        expect(total).toBe(0);
        total = adjustTotalCount(total, -1);
        expect(total).toBe(0);
    });
});

describe("parseQueueProvidersPayload", () => {
    it("parses host=segments pairs and sorts by segments descending", () => {
        expect(parseQueueProvidersPayload("news.example.com=3,news.other.com=12")).toEqual([
            { host: "news.other.com", segments: 12 },
            { host: "news.example.com", segments: 3 },
        ]);
    });

    it("returns an empty list for an empty payload", () => {
        expect(parseQueueProvidersPayload("")).toEqual([]);
    });
});

describe("applyQueueProvidersMessage", () => {
    it("preserves nicknames from the previous provider list", () => {
        const result = applyQueueProvidersMessage(
            "slot-1|news.newsgroup.ninja=12",
            [{ host: "news.newsgroup.ninja", nickname: "Newsgroup Ninja", segments: 5 }],
        );
        expect(result).toEqual({
            nzo_id: "slot-1",
            providers: [{ host: "news.newsgroup.ninja", nickname: "Newsgroup Ninja", segments: 12 }],
        });
    });

    it("matches hosts case-insensitively when preserving nicknames", () => {
        const result = applyQueueProvidersMessage(
            "slot-1|NEWS.NEWSGROUP.NINJA=4",
            [{ host: "news.newsgroup.ninja", nickname: "Newsgroup Ninja", segments: 0 }],
        );
        expect(result?.providers[0]?.nickname).toBe("Newsgroup Ninja");
    });

    it("leaves nicknames unset when the previous list had none", () => {
        const result = applyQueueProvidersMessage(
            "slot-1|news.newsgroup.ninja=12",
            [{ host: "news.newsgroup.ninja", segments: 5 }],
        );
        expect(result?.providers).toEqual([{ host: "news.newsgroup.ninja", segments: 12 }]);
    });

    it("clears providers when the payload is empty", () => {
        const result = applyQueueProvidersMessage("slot-1|", [{ host: "news.example.com", nickname: "Main", segments: 1 }]);
        expect(result).toEqual({ nzo_id: "slot-1", providers: [] });
    });

    it("returns null for malformed messages", () => {
        expect(applyQueueProvidersMessage("no-separator")).toBeNull();
    });
});
