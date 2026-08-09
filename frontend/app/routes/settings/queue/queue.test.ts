// @vitest-environment jsdom
/* global HTMLInputElement, HTMLSelectElement */
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createElement, useState } from "react";
import { afterEach, describe, expect, it } from "vitest";
import { isQueueSettingsUpdated, isQueueSettingsValid, QueueSettings } from "./queue";

const validConfig = {
    "queue.worker-count": "1",
    "usenet.max-queue-connections": "",
    "queue.max-items": "0",
    "queue.resume-threshold": "0",
};

afterEach(cleanup);

function QueueHarness() {
    const [config, setConfig] = useState<Record<string, string>>(validConfig);
    return createElement(QueueSettings, { config, setNewConfig: setConfig });
}

describe("Queue settings", () => {
    it("updates processing capacity and queue admission controls", async () => {
        const user = userEvent.setup();
        render(createElement(QueueHarness));

        const workers = screen.getByRole<HTMLSelectElement>("combobox", {
            name: "Concurrent Queue Downloads",
        });
        await user.selectOptions(workers, "4");
        expect(workers.value).toBe("4");

        const connections = screen.getByRole<HTMLInputElement>("textbox", {
            name: "Queue Download Connections",
        });
        await user.type(connections, "20");
        expect(connections.value).toBe("20");

        const maximum = screen.getByRole<HTMLInputElement>("spinbutton", {
            name: "Maximum queued jobs",
        });
        await user.clear(maximum);
        await user.type(maximum, "10");
        expect(maximum.value).toBe("10");

        const threshold = screen.getByRole<HTMLInputElement>("spinbutton", {
            name: "Resume threshold",
        });
        expect(threshold.disabled).toBe(false);
        await user.clear(threshold);
        await user.type(threshold, "5");
        expect(threshold.value).toBe("5");
    });

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
            "queue.worker-count": "10",
            "usenet.max-queue-connections": "20",
            "queue.max-items": "50",
            "queue.resume-threshold": "25",
        })).toBe(true);
    });

    it("rejects out-of-range workers and invalid admission thresholds", () => {
        expect(isQueueSettingsValid({
            ...validConfig,
            "queue.worker-count": "11",
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
