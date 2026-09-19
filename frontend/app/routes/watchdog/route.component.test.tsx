// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { WatchdogEntry } from "~/clients/backend-client.server";
import type { ComponentProps } from "react";
import Watchdog from "./route";

vi.mock("~/clients/backend-client.server", () => ({ backendClient: {} }));
vi.mock("~/auth/authorization", () => ({ useIsReadOnly: () => true }));
afterEach(cleanup);

const entry = (overrides: Partial<WatchdogEntry> = {}): WatchdogEntry => ({
  clickId: "click-1",
  attemptedAtUnix: 1_800_000_000,
  contentType: "series",
  requestedTitle: "Example S01E01",
  candidateTitle: "Example release",
  indexerName: "",
  size: 2_000_000_000,
  rankIndex: 0,
  outcome: "QueueFailed",
  failReason: "Missing volumes. Try another release.",
  durationMs: 5754,
  isWinner: false,
  ...overrides,
});

describe("Watchdog attempt timeline", () => {
  it("groups only by click ID and preserves full attempt details", async () => {
    render(
      <Watchdog
        {...({
          loaderData: {
            entries: [
              entry(),
              entry({ rankIndex: 1, isWinner: true, outcome: "QueueCompleted", failReason: null }),
              entry({ clickId: "click-2" }),
            ],
          },
        } as ComponentProps<typeof Watchdog>)}
      />,
    );
    expect(screen.getAllByText("Example S01E01")).toHaveLength(2);
    expect(screen.getByText("2 attempts")).toBeTruthy();
    expect(screen.getByText("Resolved · indexer unavailable")).toBeTruthy();
    const summary = screen.getByText("2 attempts").closest("summary")!;
    expect(summary.closest("details")?.open).toBe(false);
    await userEvent.setup().click(summary);
    expect(summary.closest("details")?.open).toBe(true);
    expect(screen.getAllByText("Missing volumes. Try another release.")).toHaveLength(2);
    expect(screen.getAllByText("5.8s").length).toBeGreaterThan(0);
  });
});
