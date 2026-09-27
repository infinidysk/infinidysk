// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createMemoryRouter, RouterProvider } from "react-router";
import SmartPrefetchActivityPage from "./route";

vi.mock("~/auth/authorization", () => ({ useIsReadOnly: () => true }));
vi.mock("~/components/prefetch-queue", () => ({ PrefetchQueue: () => null }));
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const now = Date.now();
const response = {
  available: true,
  healthy: true,
  paused: false,
  dailyBudgetUsed: 2_000_000_000,
  settings: { dailyByteBudget: 10_000_000_000, maxConcurrentJobs: 2 },
  jobs: [
    {
      id: "a",
      itemId: "item-a",
      displayName: "Movie warming",
      source: "Plex",
      trigger: "plex",
      state: "running",
      start: 0,
      length: 0,
      committedBytes: 1_000_000_000,
      fileSize: 2_000_000_000,
      updated: now,
    },
    {
      id: "b",
      itemId: "item-b",
      displayName: "Series finished",
      source: "Plex",
      trigger: "plex",
      state: "completed",
      start: 0,
      length: 0,
      committedBytes: 2_000_000_000,
      fileSize: 2_000_000_000,
      updated: now,
    },
  ],
};

describe("Smart Prefetch activity page", () => {
  it("separates active jobs from recent history and shows real budget accounting", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(response) }),
    );
    const router = createMemoryRouter(
      [
        { path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> },
        { path: "/settings", element: <div>Settings</div> },
      ],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    expect(await screen.findByText("Movie warming")).toBeTruthy();
    expect(screen.queryByText("Series finished")).toBeNull();
    expect(screen.getByText("Daily provider budget").parentElement?.textContent).toContain(
      "2 GB / 10 GB",
    );
    await userEvent.setup().click(screen.getByRole("tab", { name: /Warming history/ }));
    expect(screen.getByText("Series finished")).toBeTruthy();
    expect(screen.queryByText("Movie warming")).toBeNull();
    expect(screen.getByText("Completed today")).toBeTruthy();
    expect(screen.queryByText("Advanced warming controls")).toBeNull();
  });
});
