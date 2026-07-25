import { describe, expect, it } from "vitest";
import { isHardFailureStatus, statusLabel } from "./error-donut";

describe("isHardFailureStatus", () => {
    it("treats Missing as a provider miss, not a hard failure", () => {
        expect(isHardFailureStatus("Missing")).toBe(false);
    });

    it("flags Timeout, Network, Auth, Corrupt, Protocol, and Other as hard failures", () => {
        expect(isHardFailureStatus("Timeout")).toBe(true);
        expect(isHardFailureStatus("Network")).toBe(true);
        expect(isHardFailureStatus("Auth")).toBe(true);
        expect(isHardFailureStatus("Corrupt")).toBe(true);
        expect(isHardFailureStatus("Protocol")).toBe(true);
        expect(isHardFailureStatus("Other")).toBe(true);
    });
});

describe("statusLabel", () => {
    it("labels Other as unclassified so it's never mistaken for a clean provider miss", () => {
        expect(statusLabel("Other")).toBe("Other (unclassified)");
    });

    it("passes through known status names unchanged", () => {
        expect(statusLabel("Timeout")).toBe("Timeout");
        expect(statusLabel("Corrupt")).toBe("Corrupt");
        expect(statusLabel("Auth")).toBe("Auth");
        expect(statusLabel("Network")).toBe("Network");
        expect(statusLabel("Protocol")).toBe("Protocol");
        expect(statusLabel("Missing")).toBe("Missing");
    });
});
