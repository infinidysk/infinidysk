import { describe, expect, it, vi } from "vitest";
import {
    beginLatestRequest,
    canConnectMigration,
    canEditCategoryMappings,
    canEditReleaseSelection,
    canResetMigration,
    canStartScanMigration,
    connectFormWithDetectedPaths,
    connectFormWithStatusPaths,
    hasScanData,
    inferStandardAltmountRoot,
    isMigrationWorkActive,
    loadTableLatest,
    loadTableRetainingLastGood,
    requestAltmountPathDetection,
    requestSymlinkApply,
    runUiMutation,
    type SessionStatus,
} from "./use-altmount-migration";

describe("requestAltmountPathDetection", () => {
    it("asks the backend to inspect the trimmed container path", async () => {
        const originalFetch = globalThis.fetch;
        const response = {
            detected: true,
            root: "/altmount-data/config",
            metadataRoot: "/altmount-data/config/metadata",
            configPath: "/altmount-data/config/config.yaml",
            storeRoot: "/altmount-data/config",
            reason: null,
        };
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
            new Response(JSON.stringify(response), {
                status: 200,
                headers: { "content-type": "application/json" },
            }),
        );
        globalThis.fetch = fetchMock;
        try {
            await expect(requestAltmountPathDetection("  /altmount-data/config  "))
                .resolves.toEqual(response);

            expect(fetchMock).toHaveBeenCalledOnce();
            const [url, init] = fetchMock.mock.calls[0];
            expect(url).toBe("/api/migration/altmount/detect");
            expect(init?.method).toBe("POST");
            expect(JSON.parse(String(init?.body))).toEqual({ root: "/altmount-data/config" });
        } finally {
            globalThis.fetch = originalFetch;
        }
    });

    it("sends an explicit null root when requesting default detection", async () => {
        const originalFetch = globalThis.fetch;
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
            new Response(JSON.stringify({
                detected: false,
                root: "/altmount",
                metadataRoot: "/altmount/metadata",
                configPath: "/altmount/config.yaml",
                storeRoot: "/altmount",
                reason: "The selected directory does not match the standard Altmount layout.",
            }), {
                status: 200,
                headers: { "content-type": "application/json" },
            }),
        );
        globalThis.fetch = fetchMock;
        try {
            await requestAltmountPathDetection();

            const [, init] = fetchMock.mock.calls[0];
            expect(JSON.parse(String(init?.body))).toEqual({ root: null });
        } finally {
            globalThis.fetch = originalFetch;
        }
    });
});

describe("standard Altmount path helpers", () => {
    it("recognizes saved paths that share the basic-mode layout", () => {
        expect(inferStandardAltmountRoot({
            altmountMetadataRoot: "/altmount-data/config/metadata/",
            altmountConfigPath: "/altmount-data/config/config.yaml",
            altmountStoreRoot: "/altmount-data/config/",
        })).toBe("/altmount-data/config");

        expect(inferStandardAltmountRoot({
            altmountMetadataRoot: String.raw`C:\altmount\metadata`,
            altmountConfigPath: String.raw`C:\altmount\config.yaml`,
            altmountStoreRoot: String.raw`C:\altmount`,
        })).toBe(String.raw`C:\altmount`);
    });

    it("rejects split or incomplete advanced-mode paths", () => {
        expect(inferStandardAltmountRoot({
            altmountMetadataRoot: "/metadata",
            altmountConfigPath: "/config/config.yaml",
            altmountStoreRoot: "/stores",
        })).toBeNull();
        expect(inferStandardAltmountRoot({
            altmountMetadataRoot: "/altmount/metadata",
            altmountConfigPath: null,
            altmountStoreRoot: "/altmount",
        })).toBeNull();
    });

    it("applies detected paths without resetting queue tuning", () => {
        expect(connectFormWithDetectedPaths(
            {
                metadataRoot: "/old/metadata",
                configPath: "/old/config.yaml",
                storeRoot: "/old",
                maxQueueDepth: 73,
                submitWorkers: 4,
            },
            {
                detected: true,
                root: "/altmount-data/config",
                metadataRoot: "/altmount-data/config/metadata",
                configPath: "/altmount-data/config/config.yaml",
                storeRoot: "/altmount-data/config",
                reason: null,
            },
        )).toEqual({
            metadataRoot: "/altmount-data/config/metadata",
            configPath: "/altmount-data/config/config.yaml",
            storeRoot: "/altmount-data/config",
            maxQueueDepth: 73,
            submitWorkers: 4,
        });
    });

    it("restores status paths without resetting queue tuning", () => {
        const form = {
            metadataRoot: "/detected/metadata",
            configPath: "/detected/config.yaml",
            storeRoot: "/detected",
            maxQueueDepth: 73,
            submitWorkers: 4,
        };

        expect(connectFormWithStatusPaths(form, {
            altmountMetadataRoot: "/saved/metadata",
            altmountConfigPath: "/saved/config.yaml",
            altmountStoreRoot: "/saved",
        })).toEqual({
            metadataRoot: "/saved/metadata",
            configPath: "/saved/config.yaml",
            storeRoot: "/saved",
            maxQueueDepth: 73,
            submitWorkers: 4,
        });
        expect(connectFormWithStatusPaths(form, undefined)).toEqual({
            metadataRoot: "",
            configPath: "",
            storeRoot: "",
            maxQueueDepth: 73,
            submitWorkers: 4,
        });
    });
});

describe("requestSymlinkApply", () => {
    it.each([
        [undefined, false],
        [true, true],
    ] as const)("sends unreadable acknowledgement %s", async (input, expected) => {
        const originalFetch = globalThis.fetch;
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
            new Response(JSON.stringify({ status: true }), {
                status: 200,
                headers: { "content-type": "application/json" },
            }),
        );
        globalThis.fetch = fetchMock;
        try {
            await requestSymlinkApply(input);

            expect(fetchMock).toHaveBeenCalledOnce();
            const [url, init] = fetchMock.mock.calls[0];
            expect(url).toBe("/api/migration/altmount/symlinks/apply");
            expect(init?.method).toBe("POST");
            expect(JSON.parse(String(init?.body))).toEqual({
                confirm: true,
                acknowledgeUnreadable: expected,
            });
        } finally {
            globalThis.fetch = originalFetch;
        }
    });
});

describe("beginLatestRequest", () => {
    it("invalidates older request tickets when a newer refresh starts", () => {
        const generation = { current: 0 };
        const firstIsLatest = beginLatestRequest(generation);
        const secondIsLatest = beginLatestRequest(generation);

        expect(firstIsLatest()).toBe(false);
        expect(secondIsLatest()).toBe(true);
    });

    it("keeps the current request ticket valid until another request starts", () => {
        const generation = { current: 7 };
        const isLatest = beginLatestRequest(generation);

        expect(isLatest()).toBe(true);
        expect(isLatest()).toBe(true);
        expect(generation.current).toBe(8);
    });
});

describe("isMigrationWorkActive", () => {
    it.each<SessionStatus>(["scanning", "scan_cancelling", "running", "paused", "cancelling", "linking", "applying", "restoring"])(
        "blocks destructive wizard actions while status is %s",
        (status) => expect(isMigrationWorkActive(status)).toBe(true),
    );

    it.each<SessionStatus>(["idle", "connected", "mapped", "scanned", "complete", "cancelled", "linked"])(
        "allows destructive wizard actions after status is %s",
        (status) => expect(isMigrationWorkActive(status)).toBe(false),
    );

    it("treats an unloaded status as inactive", () => {
        expect(isMigrationWorkActive(undefined)).toBe(false);
    });
});

describe("canResetMigration", () => {
    it.each<SessionStatus>(["scanning", "scan_cancelling", "running", "paused", "cancelling", "linking", "applying", "restoring"])(
        "blocks Reset Wizard while status is %s",
        (status) => expect(canResetMigration(status, null)).toBe(false),
    );

    it("allows reset only after status has loaded and no mutation is busy", () => {
        expect(canResetMigration("scanned", null)).toBe(true);
        expect(canResetMigration(undefined, null)).toBe(false);
        expect(canResetMigration("scanned", "reset")).toBe(false);
    });
});

describe("review mutation state guards", () => {
    it.each<SessionStatus>(["idle", "connected", "mapped", "scanned", "complete", "cancelled", "linked"])(
        "allows Connect from resting state %s",
        (status) => expect(canConnectMigration(status)).toBe(true),
    );

    it.each<SessionStatus>(["scanning", "scan_cancelling", "running", "paused", "cancelling", "linking", "applying", "restoring"])(
        "blocks Connect during active state %s",
        (status) => expect(canConnectMigration(status)).toBe(false),
    );

    it.each<SessionStatus>(["connected", "mapped", "scanned", "complete", "cancelled", "linked"])(
        "allows Scan from resting configured state %s",
        (status) => expect(canStartScanMigration(status)).toBe(true),
    );

    it.each<SessionStatus>(["idle", "scanning", "scan_cancelling", "running", "paused", "cancelling", "linking", "applying", "restoring"])(
        "blocks Scan from illegal state %s",
        (status) => expect(canStartScanMigration(status)).toBe(false),
    );

    it.each<SessionStatus>(["connected", "mapped", "scanned"])(
        "allows category mapping edits while status is %s",
        (status) => expect(canEditCategoryMappings(status)).toBe(true),
    );

    it.each<SessionStatus>(["idle", "scanning", "scan_cancelling", "running", "paused", "cancelling", "complete", "cancelled", "linking", "linked", "applying", "restoring"])(
        "locks category mapping edits while status is %s",
        (status) => expect(canEditCategoryMappings(status)).toBe(false),
    );

    it("allows release selection edits only for a completed scan", () => {
        const statuses: (SessionStatus | undefined)[] = [
            undefined, "idle", "connected", "mapped", "scanning", "scan_cancelling", "running", "paused", "cancelling",
            "complete", "cancelled", "linking", "linked", "applying", "restoring",
        ];
        expect(canEditReleaseSelection("scanned")).toBe(true);
        statuses.forEach((status) => expect(canEditReleaseSelection(status)).toBe(false));
    });
});

describe("hasScanData", () => {
    it.each<SessionStatus>([
        "scanned", "running", "paused", "cancelling", "complete", "cancelled",
        "linking", "linked", "applying", "restoring",
    ])("reports scan data available for status %s", (status) => {
        expect(hasScanData(status)).toBe(true);
    });

    it.each<SessionStatus>(["idle", "connected", "mapped", "scanning", "scan_cancelling"])(
        "reports no scan data for status %s",
        (status) => expect(hasScanData(status)).toBe(false),
    );
});

describe("runUiMutation", () => {
    it("returns true without recording an error after a successful mutation", async () => {
        const recordError = vi.fn();

        await expect(runUiMutation(() => Promise.resolve(), recordError)).resolves.toBe(true);
        expect(recordError).not.toHaveBeenCalled();
    });

    it("records a rejected mutation and returns false", async () => {
        const recordError = vi.fn();

        await expect(runUiMutation(
            () => Promise.reject(new Error("API rejected the mutation")),
            recordError,
        )).resolves.toBe(false);
        expect(recordError).toHaveBeenCalledOnce();
        expect(recordError).toHaveBeenCalledWith("API rejected the mutation");
    });
});

describe("loadTableLatest", () => {
    it("commits only the newest response when requests resolve out of order", async () => {
        const generation = { current: 0 };
        const commit = vi.fn();
        const recordError = vi.fn();

        let resolveStale!: (value: string) => void;
        let resolveFresh!: (value: string) => void;
        const stale = new Promise<string>((resolve) => { resolveStale = resolve; });
        const fresh = new Promise<string>((resolve) => { resolveFresh = resolve; });

        const first = loadTableLatest(generation, () => stale, commit, recordError);
        const second = loadTableLatest(generation, () => fresh, commit, recordError);

        resolveFresh("fresh");
        await expect(second).resolves.toBe(true);
        expect(commit).toHaveBeenCalledOnce();
        expect(commit).toHaveBeenCalledWith("fresh");

        resolveStale("stale");
        await expect(first).resolves.toBe(false);
        expect(commit).toHaveBeenCalledOnce();
        expect(recordError).not.toHaveBeenCalled();
    });
});

describe("loadTableRetainingLastGood", () => {
    it("commits a successful response", async () => {
        const commit = vi.fn();
        const recordError = vi.fn();

        await expect(loadTableRetainingLastGood(
            () => Promise.resolve(["fresh"]),
            commit,
            recordError,
        )).resolves.toBe(true);
        expect(commit).toHaveBeenCalledWith(["fresh"]);
        expect(recordError).not.toHaveBeenCalled();
    });

    it("records a failure without replacing the last committed data", async () => {
        const commit = vi.fn();
        const recordError = vi.fn();

        await expect(loadTableRetainingLastGood(
            () => Promise.reject(new Error("table unavailable")),
            commit,
            recordError,
        )).resolves.toBe(false);
        expect(commit).not.toHaveBeenCalled();
        expect(recordError).toHaveBeenCalledWith("table unavailable");
    });
});
