import { describe, expect, it } from "vitest";
import { isStreamingSettingsUpdated, isStreamingSettingsValid } from "./streaming";

const validConfig = {
    "usenet.max-download-connections": "0",
    "usenet.max-download-connections-per-stream": "false",
    "usenet.max-download-connections-per-stream-preset": "high",
    "usenet.streaming-priority": "80",
    "usenet.streaming-segment-timeout-seconds": "8",
    "usenet.streaming-read-timeout-seconds": "30",
    "usenet.streaming-segment-retries": "3",
    "usenet.article-buffer-size": "40",
    "usenet.in-flight-article-budget-mb": "",
    "usenet.idle-connection-timeout-seconds": "60",
    "usenet.pipelined-body-requests": "true",
    "usenet.container-aware-fill": "false",
    "usenet.segment-cache.enabled": "false",
    "usenet.segment-cache.path": "/config/segment-cache",
    "usenet.segment-cache.max-gb": "10",
};

describe("Streaming settings", () => {
    it("detects changes to every owned setting", () => {
        for (const key of Object.keys(validConfig)) {
            expect(isStreamingSettingsUpdated(validConfig, {
                ...validConfig,
                [key]: validConfig[key as keyof typeof validConfig] === "true" ? "false" : "changed",
            })).toBe(true);
        }
        expect(isStreamingSettingsUpdated(validConfig, { ...validConfig })).toBe(false);
    });

    it("accepts the default configuration and validation boundaries", () => {
        expect(isStreamingSettingsValid(validConfig)).toBe(true);
        expect(isStreamingSettingsValid({
            ...validConfig,
            "usenet.max-download-connections": "1",
            "usenet.streaming-priority": "0",
            "usenet.streaming-segment-timeout-seconds": "40",
            "usenet.streaming-read-timeout-seconds": "120",
            "usenet.streaming-segment-retries": "0",
            "usenet.in-flight-article-budget-mb": "8192",
            "usenet.idle-connection-timeout-seconds": "15",
        })).toBe(true);
    });

    it("rejects invalid limits and enabled cache settings", () => {
        expect(isStreamingSettingsValid({
            ...validConfig,
            "usenet.streaming-priority": "101",
        })).toBe(false);
        expect(isStreamingSettingsValid({
            ...validConfig,
            "usenet.streaming-read-timeout-seconds": "4",
        })).toBe(false);
        expect(isStreamingSettingsValid({
            ...validConfig,
            "usenet.segment-cache.enabled": "true",
            "usenet.segment-cache.path": " ",
        })).toBe(false);
    });
});
