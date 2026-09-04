// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { fetchMock, isReadOnlyMock, revalidateMock } = vi.hoisted(() => ({
  fetchMock: vi.fn(),
  isReadOnlyMock: vi.fn(),
  revalidateMock: vi.fn(),
}));

vi.mock("react-router", () => ({
  useRevalidator: () => ({ state: "idle", revalidate: revalidateMock }),
  useSearchParams: () => [new URLSearchParams(), vi.fn()],
}));

vi.mock("~/auth/authorization", () => ({
  useIsReadOnly: isReadOnlyMock,
}));

vi.mock("~/utils/shared-websocket", () => ({
  useWebsocketTopics: vi.fn(),
}));

vi.mock("./components/health-table/health-table", () => ({
  HealthTable: () => <div data-testid="health-table" />,
}));

vi.mock("./components/health-stats/health-stats", () => ({
  HealthStats: () => <div data-testid="health-stats" />,
}));

vi.mock("./components/health-history-table/health-history-table", () => ({
  HealthHistoryTable: ({
    canRequeueActionNeeded,
    requeueingActionNeeded,
    onRequeueActionNeeded,
  }: {
    canRequeueActionNeeded: boolean;
    requeueingActionNeeded: boolean;
    onRequeueActionNeeded: () => void;
  }) => (
    <div data-testid="health-history">
      {canRequeueActionNeeded && (
        <button type="button" disabled={requeueingActionNeeded} onClick={onRequeueActionNeeded}>
          {requeueingActionNeeded ? "Queueing..." : "Re-check action needed"}
        </button>
      )}
    </div>
  ),
}));

vi.mock("~/components/ui", () => ({
  Alert: ({ children, ...props }: { children: ReactNode; [key: string]: unknown }) => (
    <div {...props}>{children}</div>
  ),
  Button: ({ children, ...props }: { children: ReactNode; [key: string]: unknown }) => (
    <button type="button" {...props}>
      {children}
    </button>
  ),
  Icon: () => null,
  PageHeader: ({ title, actions }: { title: string; actions: ReactNode }) => (
    <header>
      <h1>{title}</h1>
      {actions}
    </header>
  ),
}));

import Health from "./route";

afterEach(cleanup);

beforeEach(() => {
  fetchMock.mockReset();
  isReadOnlyMock.mockReset();
  isReadOnlyMock.mockReturnValue(false);
  revalidateMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

function renderHealth(options: { isEnabled?: boolean } = {}) {
  return render(
    <Health
      {...({
        loaderData: {
          uncheckedCount: 0,
          queueItems: Array.from({ length: 15 }, (_, index) => ({ id: `queue-${index}` })),
          historyStats: [],
          historyItems: [],
          historyTotalCount: 0,
          historyPage: 1,
          historyPageSize: 25,
          historyFilter: "all",
          isEnabled: options.isEnabled ?? true,
          schedule: null,
        },
      } as unknown as Parameters<typeof Health>[0])}
    />,
  );
}

function jsonResponse(body: object, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("Health action-needed re-check", () => {
  it("queues current action-needed items and revalidates the page", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ status: true, requeuedCount: 2 }));
    renderHealth();

    await userEvent.setup().click(screen.getByRole("button", { name: "Re-check action needed" }));

    await waitFor(() => {
      expect(screen.getByRole("status").textContent).toContain("Queued 2 items for re-check.");
    });
    expect(fetchMock).toHaveBeenCalledWith("/api/requeue-action-needed-health-checks", {
      method: "POST",
    });
    expect(revalidateMock).toHaveBeenCalledOnce();
  });

  it("reports when no current action-needed items remain", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ status: true, requeuedCount: 0 }));
    renderHealth();

    await userEvent.setup().click(screen.getByRole("button", { name: "Re-check action needed" }));

    expect((await screen.findByRole("status")).textContent).toContain(
      "No current action-needed items to re-check.",
    );
    expect(revalidateMock).toHaveBeenCalledOnce();
  });

  it("shows the backend conflict reason without revalidating", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ status: false, error: "Enable Background Repairs is off" }, 409),
    );
    renderHealth();

    await userEvent.setup().click(screen.getByRole("button", { name: "Re-check action needed" }));

    expect((await screen.findByRole("status")).textContent).toContain(
      "Enable Background Repairs is off",
    );
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it("shows a recoverable message when the request fails", async () => {
    fetchMock.mockRejectedValueOnce(new TypeError("network unavailable"));
    renderHealth();

    await userEvent.setup().click(screen.getByRole("button", { name: "Re-check action needed" }));

    expect((await screen.findByRole("status")).textContent).toContain(
      "Could not queue action-needed items for re-check.",
    );
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it("hides the action when repairs are disabled or the UI is read-only", () => {
    const disabled = renderHealth({ isEnabled: false });
    expect(screen.queryByRole("button", { name: "Re-check action needed" })).toBeNull();
    disabled.unmount();

    isReadOnlyMock.mockReturnValue(true);
    renderHealth();

    expect(screen.queryByRole("button", { name: "Re-check action needed" })).toBeNull();
  });
});
