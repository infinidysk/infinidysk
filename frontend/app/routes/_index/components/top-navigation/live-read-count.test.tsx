// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { LiveReadCount } from "./live-read-count";

const socket = vi.hoisted(() => ({ receive: (_message: string) => {}, close: () => {} }));
vi.mock("~/utils/shared-websocket", () => ({
  useWebsocketTopic: (
    _topic: string,
    _kind: string,
    receive: (message: string) => void,
    options: { onClose: () => void },
  ) => {
    socket.receive = receive;
    socket.close = options.onClose;
  },
}));

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function renderCount() {
  render(
    <MemoryRouter>
      <LiveReadCount />
    </MemoryRouter>,
  );
}

function receiveCount(activeReads: number, ts = Date.now()) {
  act(() => socket.receive(JSON.stringify({ activeReads, ts })));
}

describe("LiveReadCount", () => {
  it("shows live counts, an idle state, and an accessible link to the details", () => {
    renderCount();
    expect(screen.getByText("-- reads")).toBeTruthy();
    receiveCount(3);
    const link = screen.getByRole("link", {
      name: "3 active file reads. View active-read details",
    });
    expect(link.getAttribute("href")).toBe("/overview#active-reads");
    expect(link.className).toContain("w-24");
    fireEvent.focus(link);
    expect(screen.getByRole("tooltip").textContent).toBe("3 active file reads");
    receiveCount(1);
    expect(screen.getByText("1 read")).toBeTruthy();
    receiveCount(0);
    expect(screen.getByText("0 reads")).toBeTruthy();
    expect(link.className).toContain("text-base-content/60");
  });

  it("does not report zero when disconnected and recovers on fresh data", () => {
    renderCount();
    receiveCount(3);
    act(() => socket.close());
    expect(screen.getByRole("link", { name: /Active file reads unavailable/ })).toBeTruthy();
    expect(screen.queryByText("0 reads")).toBeNull();
    receiveCount(2);
    expect(screen.getByText("2 reads")).toBeTruthy();
  });

  it("expires silent or stale updates", async () => {
    vi.useFakeTimers();
    renderCount();
    receiveCount(3);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(15_000);
    });
    expect(screen.getByText("-- reads")).toBeTruthy();
    receiveCount(8, Date.now() - 20_000);
    expect(screen.queryByText("8 reads")).toBeNull();
    receiveCount(0);
    expect(screen.getByText("0 reads")).toBeTruthy();
  });

  it.each([-1, 1.5])("rejects invalid read count %s", (count) => {
    renderCount();
    receiveCount(count);
    expect(screen.getByRole("link", { name: /Active file reads unavailable/ })).toBeTruthy();
  });
});
