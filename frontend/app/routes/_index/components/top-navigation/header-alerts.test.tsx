// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { HeaderAlerts } from "./header-alerts";

const socket = vi.hoisted(() => ({
  receive: (_message: string) => {},
  close: () => {},
  enabled: false,
}));
vi.mock("~/utils/shared-websocket", () => ({
  useWebsocketTopic: (
    _topic: string,
    _kind: string,
    receive: (message: string) => void,
    options: { enabled: boolean; onClose: () => void },
  ) => {
    socket.receive = receive;
    socket.close = options.onClose;
    socket.enabled = options.enabled;
  },
}));

let healthCount = 0;
let arrUnavailable = false;

beforeEach(() => {
  healthCount = 0;
  arrUnavailable = false;
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string) =>
      Promise.resolve({
        ok: !url.includes("get-arr-health") || !arrUnavailable,
        json: () =>
          Promise.resolve(
            url.includes("get-arr-health")
              ? { configured: false, instances: [] }
              : { totalCount: healthCount },
          ),
      }),
    ),
  );
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function renderAlerts(hasUsenetProviders = true) {
  render(
    <MemoryRouter>
      <HeaderAlerts hasUsenetProviders={hasUsenetProviders} />
    </MemoryRouter>,
  );
}

function receiveProviders(circuitState: "closed" | "open" | "halfOpen", ts = Date.now()) {
  act(() =>
    socket.receive(
      JSON.stringify({ ts, providerBreakers: [{ provider: "news.example.com", circuitState }] }),
    ),
  );
}

describe("HeaderAlerts live data", () => {
  it.each([
    {
      update: {
        kind: "release",
        latestVersion: "1.4.3",
        releaseUrl: "https://example.com/release",
      } as const,
      label: "Update to v1.4.3",
      href: "https://example.com/release",
    },
    {
      update: {
        kind: "dev",
        commitsBehind: 2,
        trackRef: "main",
        compareUrl: "https://example.com/compare",
      } as const,
      label: "2 new commits on main",
      href: "https://example.com/compare",
    },
  ])("surfaces $label in the bell without other warnings", async ({ update, label, href }) => {
    render(
      <MemoryRouter>
        <HeaderAlerts hasUsenetProviders={false} updateAvailable={update} />
      </MemoryRouter>,
    );
    const trigger = await screen.findByLabelText("Alerts: needs attention");
    fireEvent.click(trigger);
    expect(screen.getByRole("link", { name: label }).getAttribute("href")).toBe(href);
    expect(trigger.querySelector("span[class*='bg-warning']")).not.toBeNull();
    expect(screen.queryByText("No issues need attention")).toBeNull();
  });

  it("does not show a notification dot when all checks succeed and no providers or Arrs are configured", async () => {
    renderAlerts(false);
    const trigger = await screen.findByLabelText("Alerts: no issues need attention");
    expect(trigger.querySelector("span[class*='bg-success']")).toBeNull();
    expect(trigger.querySelector("span[class*='bg-warning']")).toBeNull();
    expect(socket.enabled).toBe(false);
    expect(screen.queryByText("Arr status unavailable")).toBeNull();
    expect(screen.queryByText("Provider status unavailable")).toBeNull();
  });

  it("waits for initial provider state, tracks open/recovering circuits, and clears resolved alerts", async () => {
    renderAlerts();
    expect(
      screen.getByLabelText("Alerts: checking status").querySelector("span[class*='bg-success']"),
    ).toBeNull();
    expect(screen.queryByText("Provider status unavailable")).toBeNull();
    receiveProviders("open");
    await screen.findByText("1 provider circuit open or recovering");
    expect(
      screen.getByLabelText("Alerts: needs attention").querySelector("span[class*='bg-success']"),
    ).toBeNull();
    fireEvent.click(screen.getByLabelText("Alerts: needs attention"));
    const link = screen.getByRole("link", { name: /1 provider circuit/ });
    expect(link.getAttribute("href")).toBe("/settings");
    receiveProviders("halfOpen");
    expect(screen.getByText("1 provider circuit open or recovering")).toBeTruthy();
    receiveProviders("closed");
    await screen.findByText("No issues need attention");
    expect(screen.queryByText("1 provider circuit open or recovering")).toBeNull();
  });

  it("passes consecutive trips and provider reason through the live socket message", async () => {
    renderAlerts();
    act(() =>
      socket.receive(JSON.stringify({
        ts: Date.now(),
        providerBreakers: [{
          provider: "news.example.com",
          nickname: "Backup account",
          circuitState: "halfOpen",
          consecutiveTrips: 3,
          lastFailureReason: "Login rejected",
        }],
      })),
    );

    await screen.findByText("Backup account unreachable");
    expect(screen.getByText("Login rejected")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Disable or fix it in Usenet settings" }))
      .toBeTruthy();
  });

  it("keeps health alerts when Arr checks fail and recovers on visibility refresh", async () => {
    healthCount = 7;
    arrUnavailable = true;
    renderAlerts(false);
    await screen.findByText("7 files need attention");
    await screen.findByText("Arr status unavailable");
    healthCount = 0;
    arrUnavailable = false;
    act(() => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await screen.findByText("No issues need attention");
  });

  it("marks stale or disconnected provider data unavailable until a fresh update arrives", async () => {
    renderAlerts();
    receiveProviders("closed", Date.now() - 20_000);
    await screen.findByText("Provider status unavailable");
    receiveProviders("closed");
    await screen.findByText("No issues need attention");
    act(() => socket.close());
    expect(screen.getByText("Provider status unavailable")).toBeTruthy();
    receiveProviders("closed");
    await waitFor(() => expect(screen.queryByText("Provider status unavailable")).toBeNull());
  });

  it("expires provider state when the socket stops publishing without closing", async () => {
    vi.useFakeTimers();
    renderAlerts();
    receiveProviders("closed");
    await act(async () => {
      await vi.advanceTimersByTimeAsync(15_000);
    });
    expect(screen.getByText("Provider status unavailable")).toBeTruthy();
    expect(screen.queryByText("No issues need attention")).toBeNull();
  });
});
