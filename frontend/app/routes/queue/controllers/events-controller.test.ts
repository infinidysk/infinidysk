import { describe, expect, it } from "vitest";
import { adjustTotalCount } from "./events-controller";

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
