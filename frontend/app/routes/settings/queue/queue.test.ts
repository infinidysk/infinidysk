import { describe, expect, it } from "vitest";
import { isQueueSettingsUpdated, isQueueSettingsValid } from "./queue";

const validConfig = {
    "queue.worker-count": "1",
    "usenet.max-queue-connections": "",
    "queue.max-items": "0",
    "queue.resume-threshold": "0",
};

describe("Queue settings", () => {
    it("detects changes to every owned setting", () => {
        for (const key of Object.keys(validConfig)) {
            expect(isQueueSettingsUpdated(validConfig, {
                ...validConfig,
                [key]: "2",
            })).toBe(true);
        }
        expect(isQueueSettingsUpdated(validConfig, { ...validConfig })).toBe(false);
    });

    it("accepts supported concurrency and queue limits", () => {
        expect(isQueueSettingsValid(validConfig)).toBe(true);
        expect(isQueueSettingsValid({
            ...validConfig,
            "queue.worker-count": "4",
            "usenet.max-queue-connections": "20",
            "queue.max-items": "50",
            "queue.resume-threshold": "25",
        })).toBe(true);
    });

    it("rejects out-of-range workers and invalid admission thresholds", () => {
        expect(isQueueSettingsValid({
            ...validConfig,
            "queue.worker-count": "5",
        })).toBe(false);
        expect(isQueueSettingsValid({
            ...validConfig,
            "queue.max-items": "10",
            "queue.resume-threshold": "11",
        })).toBe(false);
        expect(isQueueSettingsValid({
            ...validConfig,
            "usenet.max-queue-connections": "0",
        })).toBe(false);
    });
});
