// @vitest-environment jsdom

import { createElement } from "react";
import { render, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, describe, expect, it } from "vitest";
import { ErrorBreakdown, isHardFailureStatus, statusLabel } from "./error-donut";

afterEach(cleanup);

describe("ErrorBreakdown", () => {
  it("sizes colored segments as direct flex children, excluding provider misses", () => {
    const { getByRole } = render(
      createElement(
        MemoryRouter,
        null,
        createElement(ErrorBreakdown, {
          errors: [
            { status: "Timeout", count: 57 },
            { status: "Other", count: 4 },
            { status: "Corrupt", count: 1 },
            { status: "Missing", count: 1377046 },
          ],
        }),
      ),
    );
    const bar = getByRole("img", { name: "62 hard fetch errors broken down by type" });
    const segments = Array.from(bar.children) as HTMLElement[];

    expect(segments).toHaveLength(3);
    expect(segments.map((segment) => segment.style.flexGrow)).toEqual(["57", "4", "1"]);
    for (const segment of segments) {
      expect(segment.style.background).not.toBe("");
      expect(segment.querySelector('[role="tooltip"]')).not.toBeNull();
    }
  });
});

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
