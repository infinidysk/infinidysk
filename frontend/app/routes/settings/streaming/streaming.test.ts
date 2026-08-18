// @vitest-environment jsdom
/* global HTMLInputElement, HTMLSelectElement */
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createElement, useState } from "react";
import { afterEach, describe, expect, it } from "vitest";
import {
    isStreamingSettingsUpdated,
    isStreamingSettingsValid,
    StreamingSettings,
} from "./streaming";

const validConfig = {
    "usenet.max-download-connections": "0",
    "usenet.max-download-connections-per-stream": "false",
    "usenet.max-download-connections-per-stream-preset": "high",
    "usenet.streaming-priority": "80",
    "usenet.streaming-segment-timeout-seconds": "8",
    "usenet.streaming-read-timeout-seconds": "30",
    "usenet.streaming-write-timeout-seconds": "60",
    "usenet.streaming-segment-retries": "3",
    "usenet.article-buffer-size": "40",
    "usenet.in-flight-article-budget-mb": "",
    "usenet.idle-connection-timeout-seconds": "60",
    "usenet.pipelined-body-requests": "true",
    "usenet.container-aware-fill": "true",
    "usenet.segment-cache.enabled": "true",
    "usenet.segment-cache.path": "/config/segment-cache",
    "usenet.segment-cache.max-gb": "10",
};

afterEach(cleanup);

function StreamingHarness() {
    const [config, setConfig] = useState<Record<string, string>>(validConfig);
    return createElement(StreamingSettings, { config, setNewConfig: setConfig });
}

describe("Streaming settings", () => {
    it("updates connection allocation and conditional controls", async () => {
        const user = userEvent.setup();
        render(createElement(StreamingHarness));

        await user.click(screen.getByRole("checkbox", {
            name: /Auto — use all Pool provider connections/,
        }));
        const maxConnections = document.getElementById(
            "max-download-connections-input",
        ) as HTMLInputElement;
        expect(maxConnections.value).toBe("15");
        await user.click(maxConnections);
        await user.keyboard("{Control>}a{/Control}24");
        expect(maxConnections.value).toBe("24");

        await user.click(screen.getByRole("checkbox", {
            name: "Apply limit per stream",
        }));
        const performance = screen.getByRole<HTMLSelectElement>("combobox", {
            name: "Per-stream performance",
        });
        await user.selectOptions(performance, "max");
        expect(performance.value).toBe("max");

        const priority = screen.getByRole<HTMLInputElement>("textbox", {
            name: "Streaming Priority (vs Queue)",
        });
        await user.clear(priority);
        await user.type(priority, "90");
        expect(priority.value).toBe("90");
    });

    it("updates caching, timeout, buffering, and fallback controls", async () => {
        const user = userEvent.setup();
        render(createElement(StreamingHarness));

        expect(screen.getByText(/Segment Cache is enabled by default/i)).toBeTruthy();
        expect(screen.getByText(/cannot automatically determine/i)).toBeTruthy();
        const segmentCache = screen.getByRole<HTMLInputElement>("checkbox", {
            name: "Enable Segment Cache (fast storage)",
        });
        expect(segmentCache.checked).toBe(true);
        const cachePath = screen.getByRole<HTMLInputElement>("textbox", {
            name: "Cache path",
        });
        await user.clear(cachePath);
        await user.type(cachePath, "/tmp/cache");
        expect(cachePath.value).toBe("/tmp/cache");

        const cacheSize = screen.getByRole<HTMLInputElement>("textbox", {
            name: "Maximum size (GB)",
        });
        await user.clear(cacheSize);
        await user.type(cacheSize, "25");
        expect(cacheSize.value).toBe("25");

        await user.click(segmentCache);
        expect(segmentCache.checked).toBe(false);

        const numericUpdates: Array<[string, string]> = [
            ["Streaming Segment Timeout", "10"],
            ["Streaming Read Timeout", "45"],
            ["Streaming Segment Retries", "4"],
            ["Article Buffer Size", "60"],
            ["In-flight article budget (MiB)", "256"],
            ["Idle connection timeout (seconds)", "90"],
        ];
        for (const [name, value] of numericUpdates) {
            const input = screen.getByRole<HTMLInputElement>("textbox", { name });
            await user.clear(input);
            await user.type(input, value);
            expect(input.value).toBe(value);
        }

        const pipelining = screen.getByRole<HTMLInputElement>("checkbox", {
            name: "Pipelined article downloads",
        });
        await user.click(pipelining);
        expect(pipelining.checked).toBe(false);

        const gapFill = screen.getByRole<HTMLInputElement>("checkbox", {
            name: /Container-aware gap fill/,
        });
        await user.click(gapFill);
        expect(gapFill.checked).toBe(false);
    });

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
