// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import type { HealthCheckResult } from "~/clients/backend-client.server";
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
  HealthHistoryTable: () => <div data-testid="health-history" />,
  HealthAttentionTable: ({
    canRequeueActionNeeded,
    requeueingActionNeeded,
    onRequeueActionNeeded,
    onDelete,
  }: {
    canRequeueActionNeeded: boolean;
    requeueingActionNeeded: boolean;
    onRequeueActionNeeded: (davItemId?: string) => void;
    onDelete?: (item: HealthCheckResult) => void;
  }) => (
    <div data-testid="health-attention">
      {onDelete && (
        <button
          onClick={() =>
            onDelete({
              id: "11111111-1111-4111-8111-111111111111",
              path: "/content/example & file.mkv",
              davItemId: "file-1",
              nzbFileName: "Example.nzb",
            } as HealthCheckResult)
          }
        >
          Delete attention file
        </button>
      )}
      {canRequeueActionNeeded && (
        <button
          type="button"
          disabled={requeueingActionNeeded}
          onClick={() => onRequeueActionNeeded()}
        >
          {requeueingActionNeeded ? "Queueing..." : "Re-check action needed"}
        </button>
      )}
      {canRequeueActionNeeded && (
        <button
          type="button"
          disabled={requeueingActionNeeded}
          onClick={() => onRequeueActionNeeded("file-1")}
        >
          Re-check file
        </button>
      )}
    </div>
  ),
}));

vi.mock("~/components/ui", () => ({
  Modal: ({
    open,
    title,
    children,
    footer,
  }: {
    open: boolean;
    title: string;
    children: ReactNode;
    footer: ReactNode;
  }) =>
    open ? (
      <div role="dialog" aria-label={title}>
        {children}
        {footer}
      </div>
    ) : null,
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
          attentionItems: [],
          attentionTotalCount: 2,
          attentionPage: 1,
          attentionPageSize: 25,
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

function mockActionResponse(response: Response | Error) {
  fetchMock.mockImplementation((input: unknown) => {
    if (String(input).includes("/api/get-active-health-checks"))
      return Promise.resolve(jsonResponse({ items: [] }));
    return response instanceof Error ? Promise.reject(response) : Promise.resolve(response);
  });
}

describe("Health action-needed re-check", () => {
  it("previews and confirms deletion even when background repairs are disabled", async () => {
    const healthCheckResultId = "11111111-1111-4111-8111-111111111111";
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ status: true, fileCount: 1, dirCount: 0 }))
      .mockResolvedValueOnce(jsonResponse({ status: true }));
    renderHealth({ isEnabled: false });
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Delete attention file" }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Remove" })).toHaveProperty("disabled", false),
    );
    const previewUrl = new URL(String(fetchMock.mock.calls[0]?.[0]), "http://localhost");
    expect(previewUrl.searchParams.get("healthCheckResultId")).toBe(healthCheckResultId);
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/api/delete-webdav-item-preview?"),
      expect.objectContaining({ signal: expect.any(AbortSignal) as unknown }),
    );
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole("button", { name: "Remove" }));
    const [, options] = fetchMock.mock.calls[1] as [string, { method: string; body: FormData }];
    expect(options.method).toBe("POST");
    expect(options.body.get("path")).toBe("/content/example & file.mkv");
    expect(options.body.get("healthCheckResultId")).toBe(
      healthCheckResultId,
    );
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(revalidateMock).toHaveBeenCalledOnce();
    expect(screen.getByRole("status").textContent).toContain("No replacement search was requested");
  });

  it.each([
    { status: false, error: "WebDAV is read-only." },
    { status: true, fileCount: 2, dirCount: 1 },
  ])("blocks deletion when the preview is not an eligible single file: %j", async (preview) => {
    fetchMock.mockResolvedValue(jsonResponse(preview));
    renderHealth({ isEnabled: false });
    await userEvent.setup().click(screen.getByRole("button", { name: "Delete attention file" }));
    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Remove" })).toHaveProperty("disabled", true);
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it.each([
    {
      status: 403,
      detail: "WebDAV is read-only. Disable 'Enforce Read-Only' in Settings > WebDAV.",
    },
    { status: 404, detail: "Item not found." },
    { status: 409, detail: "Cannot delete while a matching download is in progress." },
  ])("shows the backend problem detail when removal preview is rejected: %j", async (problem) => {
    fetchMock.mockResolvedValue(jsonResponse(problem, problem.status));
    renderHealth({ isEnabled: false });
    await userEvent.setup().click(screen.getByRole("button", { name: "Delete attention file" }));
    expect((await screen.findByRole("alert")).textContent).toBe(problem.detail);
    expect(screen.getByRole("button", { name: "Remove" })).toHaveProperty("disabled", true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it("does not delete after cancellation", async () => {
    fetchMock.mockResolvedValue(jsonResponse({ status: true, fileCount: 1, dirCount: 0 }));
    renderHealth({ isEnabled: false });
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Delete attention file" }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it.each([
    { status: false, error: "Download in progress." },
    { status: 409, detail: "Download in progress." },
  ])("keeps deletion failures visible without reporting success: %j", async (problem) => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ status: true, fileCount: 1, dirCount: 0 }))
      .mockResolvedValueOnce(jsonResponse(problem, 409));
    renderHealth({ isEnabled: false });
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Delete attention file" }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Remove" })).toHaveProperty("disabled", false),
    );
    await user.click(screen.getByRole("button", { name: "Remove" }));
    expect((await screen.findByRole("alert")).textContent).toContain("Download in progress.");
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it("hides deletion for read-only users", () => {
    isReadOnlyMock.mockReturnValue(true);
    renderHealth({ isEnabled: false });
    expect(screen.queryByRole("button", { name: "Delete attention file" })).toBeNull();
  });

  it("places attention and its actions before the schedule and history", () => {
    renderHealth();
    const attention = screen.getByTestId("health-attention");
    expect(attention.contains(screen.getByRole("button", { name: "Re-check action needed" }))).toBe(
      true,
    );
    expect(
      attention.compareDocumentPosition(screen.getByTestId("health-table")) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(
      attention.compareDocumentPosition(screen.getByTestId("health-history")) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it("queues current action-needed items and revalidates the page", async () => {
    mockActionResponse(jsonResponse({ status: true, requeuedCount: 2 }));
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
    mockActionResponse(jsonResponse({ status: true, requeuedCount: 0 }));
    renderHealth();

    await userEvent.setup().click(screen.getByRole("button", { name: "Re-check action needed" }));

    expect((await screen.findByRole("status")).textContent).toContain(
      "No current action-needed items to re-check.",
    );
    expect(revalidateMock).toHaveBeenCalledOnce();
  });

  it("sends a selected file ID for a per-item re-check", async () => {
    mockActionResponse(jsonResponse({ status: true, requeuedCount: 1 }));
    renderHealth();

    await userEvent.setup().click(screen.getByRole("button", { name: "Re-check file" }));

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/requeue-action-needed-health-checks?davItemId=file-1",
      { method: "POST" },
    );
    expect((await screen.findByRole("status")).textContent).toContain(
      "Queued 1 item for re-check.",
    );
  });

  it("shows the backend conflict reason without revalidating", async () => {
    mockActionResponse(
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
    mockActionResponse(new TypeError("network unavailable"));
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
