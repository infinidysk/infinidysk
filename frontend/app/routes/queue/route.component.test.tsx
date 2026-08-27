// @vitest-environment jsdom
/* global HTMLSelectElement */
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode, SelectHTMLAttributes } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { isReadOnlyMock, openMock, dropzoneArgs } = vi.hoisted(() => ({
  isReadOnlyMock: vi.fn(),
  openMock: vi.fn(),
  dropzoneArgs: { current: [] as unknown[] },
}));

vi.mock("react-router", () => ({
  useRevalidator: () => ({ revalidate: vi.fn() }),
  useSearchParams: () => [new URLSearchParams(), vi.fn()],
}));

vi.mock("./components/history-table/history-table", () => ({
  HistoryTable: () => <div data-testid="history-table" />,
}));

vi.mock("./components/queue-table/queue-table", () => ({
  QueueTable: () => <div data-testid="queue-table" />,
}));

vi.mock("./controllers/events-controller", () => ({
  useHistoryEvents: () => ({
    onSelectHistorySlots: vi.fn(),
    onRemovingHistorySlots: vi.fn(),
    onRemoveHistorySlots: vi.fn(),
  }),
  useQueueEvents: () => ({
    onSelectQueueSlots: vi.fn(),
    onRemovingQueueSlots: vi.fn(),
    onRemoveQueueSlots: vi.fn(),
    onMoveQueueSlotsToTop: vi.fn(),
  }),
}));

vi.mock("./controllers/websocket-controller", () => ({
  useQueueHistoryWebsocket: vi.fn(),
}));

vi.mock("./controllers/nzb-upload-controller", () => ({
  useUploadController: vi.fn(),
}));

vi.mock("./controllers/dropzone-controller", () => ({
  useQueueDropzone: (...args: unknown[]) => {
    dropzoneArgs.current = args;
    return {
      getRootProps: () => ({}),
      getInputProps: () => ({}),
      isDragActive: false,
      open: openMock,
    };
  },
}));

vi.mock("~/components/ui", () => ({
  Alert: ({ children }: { children: ReactNode }) => <>{children}</>,
  PageHeader: ({ title }: { title: string }) => <h1>{title}</h1>,
  Button: ({ children, onClick }: { children: ReactNode; onClick: () => void }) => (
    <button type="button" onClick={onClick}>
      {children}
    </button>
  ),
  Select: ({
    children,
    ...props
  }: SelectHTMLAttributes<HTMLSelectElement> & { children?: ReactNode }) => (
    <select {...props}>{children}</select>
  ),
}));

vi.mock("~/auth/authorization", () => ({
  useIsReadOnly: isReadOnlyMock,
}));

import Queue from "./route";

afterEach(cleanup);

function renderQueue(queueSlots: Array<{ nzo_id: string }> = []) {
  return render(
    <Queue
      {...({
        loaderData: {
          queueSlots,
          historySlots: [],
          totalQueueCount: queueSlots.length,
          totalHistoryCount: 0,
          categories: ["tv", "movies", "anime"],
          manualCategory: "anime",
          queuePage: 1,
          historyPage: 1,
          queuePageSize: 100,
          historyPageSize: 100,
          queueParams: { query: "", category: "", status: "", sort: null, direction: null },
          historyParams: { query: "", category: "", status: "", sort: null, direction: null },
        },
      } as unknown as Parameters<typeof Queue>[0])}
    />,
  );
}

function getUploadCategorySelect() {
  return screen.getByRole<HTMLSelectElement>("combobox", { name: "Upload category" });
}

function getManualCategoryRef() {
  return dropzoneArgs.current[2] as { current: string };
}

describe("Queue upload control", () => {
  beforeEach(() => {
    isReadOnlyMock.mockReset();
    isReadOnlyMock.mockReturnValue(false);
    openMock.mockReset();
    dropzoneArgs.current = [];
  });

  it.each([
    ["the queue is empty", []],
    ["the queue has items", [{ nzo_id: "queue-1" }]],
  ])("renders Upload NZB when %s", (_, queueSlots) => {
    renderQueue(queueSlots);

    expect(screen.getByRole("button", { name: "Upload NZB" })).toBeTruthy();
  });

  it.each([
    ["the queue is empty", []],
    ["the queue has items", [{ nzo_id: "queue-1" }]],
  ])("renders a labeled Category selector next to Upload NZB when %s", (_, queueSlots) => {
    renderQueue(queueSlots);

    expect(screen.getByText("Category")).toBeTruthy();
    expect(getUploadCategorySelect()).toBeTruthy();
    expect(screen.getByRole("button", { name: "Upload NZB" })).toBeTruthy();
  });

  it("displays the loader manual category in the selector", () => {
    renderQueue();

    expect(getUploadCategorySelect().value).toBe("anime");
  });

  it("updates the dropzone manualCategoryRef when the selector changes", async () => {
    const user = userEvent.setup();
    renderQueue();

    expect(getManualCategoryRef().current).toBe("anime");

    await user.selectOptions(getUploadCategorySelect(), "movies");

    expect(getManualCategoryRef().current).toBe("movies");
    expect(getUploadCategorySelect().value).toBe("movies");
  });

  it("opens the file picker from the page-level action", () => {
    renderQueue([{ nzo_id: "queue-1" }]);

    screen.getByRole("button", { name: "Upload NZB" }).click();

    expect(openMock).toHaveBeenCalledOnce();
  });

  it("hides Upload NZB and the category selector for read-only users", () => {
    isReadOnlyMock.mockReturnValue(true);

    renderQueue([{ nzo_id: "queue-1" }]);

    expect(screen.queryByRole("button", { name: "Upload NZB" })).toBeNull();
    expect(screen.queryByRole("combobox", { name: "Upload category" })).toBeNull();
    expect(screen.queryByText("Category")).toBeNull();
  });
});
