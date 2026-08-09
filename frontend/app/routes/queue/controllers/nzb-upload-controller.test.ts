import type React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { UploadingFile } from "../route";
import {
    buildAddFileUploadUrl,
    getXhrErrorMessage,
    processUploadQueue,
} from "./nzb-upload-controller";

type XhrScenario = (xhr: FakeXmlHttpRequest) => void;

class FakeXmlHttpRequest {
    static scenarios: XhrScenario[] = [];
    static urls: string[] = [];

    status = 0;
    statusText = "";
    response: unknown = null;
    responseText = "";
    responseType = "";
    timeout = 0;
    upload = {
        addEventListener: (_name: string, _listener: () => void) => undefined,
    };
    private listeners = new Map<string, (() => void)[]>();

    addEventListener(name: string, listener: () => void) {
        this.listeners.set(name, [...(this.listeners.get(name) ?? []), listener]);
    }

    open(_method: string, url: string) {
        FakeXmlHttpRequest.urls.push(url);
    }

    send(_body: FormData) {
        const scenario = FakeXmlHttpRequest.scenarios.shift();
        if (!scenario) throw new Error("Missing fake XHR scenario");
        scenario(this);
    }

    dispatch(name: string) {
        for (const listener of this.listeners.get(name) ?? []) listener();
    }
}

function createUploadingFile(id: string): UploadingFile {
    return {
        file: new File(["nzb"], `${id}.nzb`),
        queueSlot: {
            nzo_id: id,
            priority: "0",
            filename: `${id}.nzb`,
            cat: "uncategorized",
            percentage: "0",
            true_percentage: "0",
            status: "queued",
            mb: "0",
            mbleft: "0",
        },
    };
}

function createState(files: UploadingFile[]) {
    let currentFiles = files;
    const setUploadingFiles = (update: React.SetStateAction<UploadingFile[]>) => {
        currentFiles = typeof update === "function" ? update(currentFiles) : update;
    };

    return {
        get files() {
            return currentFiles;
        },
        setUploadingFiles,
    };
}

function successfulResponse(xhr: FakeXmlHttpRequest) {
    xhr.status = 200;
    xhr.response = { status: true };
    xhr.dispatch("load");
}

describe("buildAddFileUploadUrl", () => {
    it("serializes manual categories as individual query parameters", () => {
        const url = new URL(buildAddFileUploadUrl("tv & movies=a#100% C++ 日本語"), "https://nzbdav.test");

        expect(url.pathname).toBe("/api");
        expect(Object.fromEntries(url.searchParams)).toEqual({
            mode: "addfile",
            cat: "tv & movies=a#100% C++ 日本語",
            priority: "0",
            pp: "0",
        });
    });
});

describe("getXhrErrorMessage", () => {
    it("uses a structured JSON error when present", () => {
        expect(getXhrErrorMessage({
            response: { error: "Invalid NZB" },
            responseText: "",
            status: 400,
            statusText: "Bad Request",
        })).toBe("Invalid NZB");
    });

    it("falls back to a text proxy response", () => {
        expect(getXhrErrorMessage({
            response: null,
            responseText: "Bad Gateway",
            status: 502,
            statusText: "",
        })).toBe("Bad Gateway");
    });

    it("falls back to the HTTP status for an empty response", () => {
        expect(getXhrErrorMessage({
            response: null,
            responseText: "",
            status: 500,
            statusText: "",
        })).toBe("Upload failed with status 500");
    });
});

describe("processUploadQueue", () => {
    afterEach(() => {
        FakeXmlHttpRequest.scenarios = [];
        FakeXmlHttpRequest.urls = [];
        vi.unstubAllGlobals();
    });

    it.each([
        ["a plain-text HTTP failure", (xhr: FakeXmlHttpRequest) => {
            xhr.status = 502;
            xhr.responseText = "Bad Gateway";
            xhr.dispatch("load");
        }, "Bad Gateway"],
        ["a network error", (xhr: FakeXmlHttpRequest) => xhr.dispatch("error"), "Upload failed"],
        ["an aborted upload", (xhr: FakeXmlHttpRequest) => xhr.dispatch("abort"), "Upload aborted"],
        ["a timed out upload", (xhr: FakeXmlHttpRequest) => xhr.dispatch("timeout"), "Upload timed out"],
    ])("continues after %s", async (_description, failedResponse, errorMessage) => {
        vi.stubGlobal("XMLHttpRequest", FakeXmlHttpRequest);
        FakeXmlHttpRequest.scenarios = [failedResponse, successfulResponse];

        const first = createUploadingFile("first");
        const second = createUploadingFile("second");
        const state = createState([first, second]);
        const queueRef = { current: [first, second] } as React.RefObject<UploadingFile[]>;
        const isUploadingRef = { current: false } as React.RefObject<boolean>;

        await processUploadQueue(isUploadingRef, queueRef, state.setUploadingFiles);

        // the queue run above guarantees the file entry exists
        expect(state.files[0]!.queueSlot).toMatchObject({
            status: "upload failed",
            error: errorMessage,
        });
        expect(FakeXmlHttpRequest.urls).toHaveLength(2);
        expect(queueRef.current).toEqual([]);
        expect(isUploadingRef.current).toBe(false);
    });
});
